using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Notifications;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public partial class ProactiveCheckinSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProactiveCheckinSchedulerService> logger,
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
    private const int DefaultCheckinHour = 19;
    private const int MaxTimeZoneSkewDays = 1;

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:ProactiveCheckinIntervalMinutes", 60));

    private readonly int _checkinHour = configuration.GetValue(
        "BackgroundServices:ProactiveCheckinHour", DefaultCheckinHour);

    public string Name => "proactive-checkin-scheduler";

    public string CronExpression => "0 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await CheckAndSendCheckins(stoppingToken);
        BackgroundServiceHealthCheck.RecordTick("ProactiveCheckinScheduler");
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendCheckins(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
        var messageService = scope.ServiceProvider.GetRequiredService<IProactiveCheckinMessageService>();

        var candidates = (await dbContext.Users
            .AsNoTracking()
            .Where(u => u.ProactiveAstraEnabled)
            .ToListAsync(ct))
            .Where(u => u.HasProAccess)
            .ToList();

        if (candidates.Count == 0) return;

        var context = await BuildContext(dbContext, pushService, messageService, candidates, ct);

        foreach (var user in candidates)
        {
            try
            {
                await ProcessUserCheckinAsync(user, context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogUserCheckinFailed(logger, user.Id, ex);
            }
        }
    }

    private sealed record ProactiveCheckinContext(
        Dictionary<Guid, List<Habit>> HabitsByUser,
        HashSet<(Guid HabitId, DateOnly Date)> LoggedHabitDates,
        HashSet<(Guid UserId, DateOnly Date)> SentDates,
        IPushNotificationService PushService,
        IProactiveCheckinMessageService MessageService,
        OrbitDbContext DbContext);

    private static async Task<ProactiveCheckinContext> BuildContext(
        OrbitDbContext dbContext,
        IPushNotificationService pushService,
        IProactiveCheckinMessageService messageService,
        List<User> candidates,
        CancellationToken ct)
    {
        var userIds = candidates.Select(u => u.Id).ToList();
#pragma warning disable ORBIT0004
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
#pragma warning restore ORBIT0004
        var windowFloor = utcToday.AddDays(-MaxTimeZoneSkewDays);
        var windowCeiling = utcToday.AddDays(MaxTimeZoneSkewDays);

        var habits = await dbContext.Habits
            .AsNoTracking()
            .Where(h => userIds.Contains(h.UserId)
                && !h.IsCompleted && !h.IsGeneral
                && h.DueDate <= windowCeiling
                && (!h.EndDate.HasValue || h.EndDate.Value >= windowFloor))
            .ToListAsync(ct);
        var habitsByUser = habits.GroupBy(h => h.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var habitIds = habits.Select(h => h.Id).ToList();
        var loggedHabitDates = (await dbContext.HabitLogs
            .AsNoTracking()
            .Where(l => habitIds.Contains(l.HabitId) && l.Value > 0
                && l.Date >= windowFloor && l.Date <= windowCeiling)
            .Select(l => new { l.HabitId, l.Date })
            .ToListAsync(ct))
            .Select(l => (l.HabitId, l.Date))
            .ToHashSet();

        var sentDates = (await dbContext.SentProactiveCheckins
            .AsNoTracking()
            .Where(a => userIds.Contains(a.UserId) && a.Date >= windowFloor)
            .Select(a => new { a.UserId, a.Date })
            .ToListAsync(ct))
            .Select(a => (a.UserId, a.Date))
            .ToHashSet();

        return new ProactiveCheckinContext(
            habitsByUser, loggedHabitDates, sentDates, pushService, messageService, dbContext);
    }

    private async Task ProcessUserCheckinAsync(User user, ProactiveCheckinContext ctx, CancellationToken ct)
    {
        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        var userTimeNow = TimeOnly.FromDateTime(userNow);

        if (ctx.SentDates.Contains((user.Id, userToday))) return;
        if (!IsWithinSendWindow(userTimeNow, new TimeOnly(_checkinHour, 0))) return;

        var offTrackTitles = GetOffTrackHabitTitles(user.Id, userToday, ctx);
        if (offTrackTitles.Count == 0) return;

        var lang = user.Language ?? "en";
        var messageResult = await ctx.MessageService.GenerateMessageAsync(
            user.Name, offTrackTitles, user.CurrentStreak, lang, ct);

        if (messageResult.IsFailure)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogProactiveCheckinMessageFailed(logger, user.Id);
            return;
        }

        var (title, body) = messageResult.Value;

        if (!await TryRecordSentCheckinAsync(user.Id, userToday, title, body, ctx.DbContext, ct))
            return;

        ctx.SentDates.Add((user.Id, userToday));

        await ctx.PushService.SendToUserAsync(user.Id, title, body, NotificationUrls.Chat, ct);

        if (logger.IsEnabled(LogLevel.Information))
            LogSentProactiveCheckin(logger, user.Id);
    }

    private static List<string> GetOffTrackHabitTitles(Guid userId, DateOnly userToday, ProactiveCheckinContext ctx)
    {
        if (!ctx.HabitsByUser.TryGetValue(userId, out var habits))
            return [];

        var titles = new List<string>();
        foreach (var habit in habits)
        {
            if (habit.DueDate > userToday) continue;
            if (habit.EndDate.HasValue && habit.EndDate.Value < userToday) continue;
            if (ctx.LoggedHabitDates.Contains((habit.Id, userToday))) continue;
            titles.Add(habit.Title);
        }

        return titles;
    }

    private bool IsWithinSendWindow(TimeOnly userTimeNow, TimeOnly target)
    {
        var diff = userTimeNow.ToTimeSpan() - target.ToTimeSpan();
        return diff >= TimeSpan.Zero && diff < _interval;
    }

    private async Task<bool> TryRecordSentCheckinAsync(
        Guid userId, DateOnly date, string title, string body,
        OrbitDbContext dbContext, CancellationToken ct)
    {
        await dbContext.SentProactiveCheckins.AddAsync(SentProactiveCheckin.Create(userId, date), ct);
        await dbContext.Notifications.AddAsync(
            Notification.Create(userId, title, body, NotificationUrls.Chat), ct);

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            DetachPendingEntries(dbContext);
            if (logger.IsEnabled(LogLevel.Debug))
                LogProactiveCheckinAlreadySent(logger, userId);
            return false;
        }
    }

    private static void DetachPendingEntries(OrbitDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "ProactiveCheckinSchedulerService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "ProactiveCheckinSchedulerService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in proactive check-in scheduler")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to generate proactive check-in message for user {UserId}")]
    private static partial void LogProactiveCheckinMessageFailed(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Sent proactive check-in to user {UserId}")]
    private static partial void LogSentProactiveCheckin(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Proactive check-in already recorded for user {UserId}; skipping push")]
    private static partial void LogProactiveCheckinAlreadySent(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to process proactive check-in for user {UserId}; continuing with remaining users")]
    private static partial void LogUserCheckinFailed(ILogger logger, Guid userId, Exception ex);

}
