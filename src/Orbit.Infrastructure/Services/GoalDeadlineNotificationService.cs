using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Notifications;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public partial class GoalDeadlineNotificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GoalDeadlineNotificationService> logger,
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
    private static readonly int[] NotifyDaysBefore = [7, 3, 1];

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:GoalDeadlineIntervalMinutes", 30));

    public string Name => "goal-deadline-notification";

    public string CronExpression => "*/30 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await CheckAndSendDeadlineNotifications(stoppingToken);
        BackgroundServiceHealthCheck.RecordTick("GoalDeadlineNotification");
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendDeadlineNotifications(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var freshDerivedValues = await ComputeFreshDerivedValuesAsync(dbContext, ct);

        var candidateGoals = await dbContext.Goals
            .AsNoTracking()
            .Where(g => g.Status == GoalStatus.Active && g.Deadline != null)
            .ToListAsync(ct);

        var goals = candidateGoals
            .Where(g => EffectiveCurrentValue(g, freshDerivedValues) < g.TargetValue)
            .ToList();

        if (goals.Count == 0) return;

        var userIds = goals.Select(g => g.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var allKeys = goals
            .Where(g => g.Deadline.HasValue)
            .SelectMany(g => NotifyDaysBefore.Select(d => BuildDedupeKey(g.Id, d)))
            .ToList();
        var sentKeys = (await dbContext.Notifications
            .Where(n => allKeys.Contains(n.DedupeKey!))
            .Select(n => n.DedupeKey!)
            .ToListAsync(ct))
            .ToHashSet();

        foreach (var goal in goals)
        {
            try
            {
                await ProcessGoalDeadlineAsync(
                    goal, EffectiveCurrentValue(goal, freshDerivedValues), users, sentKeys, pushService, dbContext, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogGoalDeadlineProcessingFailed(logger, goal.Id, goal.UserId, ex);
            }
        }
    }

    private async Task<Dictionary<Guid, int>> ComputeFreshDerivedValuesAsync(OrbitDbContext dbContext, CancellationToken ct)
    {
#pragma warning disable ORBIT0004
        var streakWindowStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-AppConstants.MaxStreakLookbackDays - 1);
#pragma warning restore ORBIT0004
        var candidates = await dbContext.Goals
            .AsNoTracking()
            .Where(g => g.Status == GoalStatus.Active
                        && g.Deadline != null
                        && !g.IsDeleted
                        && (g.Type == GoalType.Streak || g.Habits.Any()))
            .ToListAsync(ct);

        var freshValues = new Dictionary<Guid, int>();
        if (candidates.Count == 0) return freshValues;

        var standardWindowStart = candidates
            .Where(goal => goal.Type == GoalType.Standard)
            .Select(goal => goal.CreatedAtUtc)
            .DefaultIfEmpty(DateTime.MaxValue)
            .Min();
        var candidateIds = candidates.Select(goal => goal.Id).ToList();
        var goals = await dbContext.Goals
            .AsNoTracking()
            .Where(goal => candidateIds.Contains(goal.Id))
            .Include(goal => goal.Habits)
            .ThenInclude(habit => habit.Logs.Where(log =>
                log.Date >= streakWindowStart || log.CreatedAtUtc >= standardWindowStart))
            .AsSplitQuery()
            .ToListAsync(ct);

        var userIds = goals.Select(goal => goal.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        foreach (var goal in goals)
        {
            if (!users.TryGetValue(goal.UserId, out var user)) continue;

            var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var userToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));

            var readValue = GoalProgressSyncService.ComputeReadValue(goal, userToday, user.WeekStartDay);
            if (readValue.HasValue)
                freshValues[goal.Id] = readValue.Value;
        }

        return freshValues;
    }

    private static decimal EffectiveCurrentValue(Goal goal, Dictionary<Guid, int> freshStreakValues) =>
        freshStreakValues.TryGetValue(goal.Id, out var fresh) ? fresh : goal.CurrentValue;

    private async Task ProcessGoalDeadlineAsync(
        Goal goal, decimal currentValue, Dictionary<Guid, User> users, HashSet<string> sentKeys,
        IPushNotificationService pushService, OrbitDbContext dbContext, CancellationToken ct)
    {
        if (!users.TryGetValue(goal.UserId, out var user)) return;
        if (!goal.Deadline.HasValue) return;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        var daysUntilDeadline = goal.Deadline.Value.DayNumber - userToday.DayNumber;

        if (daysUntilDeadline < 1) return;

        foreach (var daysBefore in NotifyDaysBefore)
        {
            if (daysUntilDeadline > daysBefore) continue;

            var notificationKey = BuildDedupeKey(goal.Id, daysBefore);
            if (sentKeys.Contains(notificationKey)) continue;

            var body = FormatDeadlineBody(goal, currentValue, daysBefore, user.Language ?? "en");

            sentKeys.Add(notificationKey);

            if (!await TryRecordAndSendAsync(goal, body, notificationKey, pushService, dbContext, ct))
                return;

            if (logger.IsEnabled(LogLevel.Debug))
                LogSentDeadlineNotification(logger, daysBefore, goal.Id, goal.UserId);

            return;
        }
    }

    private async Task<bool> TryRecordAndSendAsync(
        Goal goal, string body, string notificationKey,
        IPushNotificationService pushService, OrbitDbContext dbContext, CancellationToken ct)
    {
        await dbContext.Notifications.AddAsync(
            Notification.Create(
                goal.UserId,
                goal.Title,
                body,
                NotificationUrls.Progress,
                dedupeKey: notificationKey), ct);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            DetachPendingEntries(dbContext);
            if (logger.IsEnabled(LogLevel.Debug))
                LogDeadlineAlreadySent(logger, goal.Id);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetachPendingEntries(dbContext);
            LogDeadlineRecordFailed(logger, goal.Id, goal.UserId, ex);
            return false;
        }

        await pushService.SendToUserAsync(
            goal.UserId, goal.Title, body, NotificationUrls.Progress, ct);
        return true;
    }

    internal static string BuildDedupeKey(Guid goalId, int daysBefore) =>
        $"goal-deadline-{goalId}-{daysBefore}d";

    private static void DetachPendingEntries(OrbitDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }

    internal static string FormatDeadlineBody(Goal goal, decimal currentValue, int daysBefore, string lang)
    {
        var isPt = LocaleHelper.IsPortuguese(lang);
        var progressText = $"{currentValue}/{goal.TargetValue} {goal.Unit}";
        return daysBefore switch
        {
            1 => isPt
                ? $"Sua meta termina amanhã - você está em {progressText}"
                : $"Your goal is due tomorrow - you're at {progressText}",
            _ => isPt
                ? $"Sua meta termina em {daysBefore} dias - você está em {progressText}"
                : $"Your goal is due in {daysBefore} days - you're at {progressText}"
        };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "GoalDeadlineNotificationService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "GoalDeadlineNotificationService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in goal deadline notification service")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Sent deadline notification ({Days}d before) for goal {GoalId} to user {UserId}")]
    private static partial void LogSentDeadlineNotification(ILogger logger, int days, Guid goalId, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Deadline notification already recorded for goal {GoalId}; skipping push")]
    private static partial void LogDeadlineAlreadySent(ILogger logger, Guid goalId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to record deadline notification for goal {GoalId} (user {UserId}); skipping push")]
    private static partial void LogDeadlineRecordFailed(ILogger logger, Guid goalId, Guid userId, Exception ex);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to process deadline notification for goal {GoalId} (user {UserId}); continuing with remaining goals")]
    private static partial void LogGoalDeadlineProcessingFailed(ILogger logger, Guid goalId, Guid userId, Exception ex);

}
