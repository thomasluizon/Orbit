using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Notifications;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public partial class ReminderSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReminderSchedulerService> logger,
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:ReminderIntervalMinutes", 1));

    private readonly int _pushConcurrency = Math.Max(1,
        configuration.GetValue("BackgroundServices:ReminderPushConcurrency", 8));

    public string Name => "reminder-scheduler";

    public string CronExpression => "* * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await CheckAndSendReminders(stoppingToken);
        BackgroundServiceHealthCheck.RecordTick("ReminderScheduler");
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendReminders(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var pending = new List<PendingReminderPush>();

        await ProcessRelativeReminders(dbContext, pending, ct);

        await ProcessScheduledReminders(dbContext, pending, ct);

        await SendPushesAsync(pending, ct);
    }

    private async Task ProcessRelativeReminders(OrbitDbContext dbContext, List<PendingReminderPush> pending, CancellationToken ct)
    {
#pragma warning disable ORBIT0004
        var minLocalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var maxLocalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
#pragma warning restore ORBIT0004
        var habits = await dbContext.Habits
            .AsNoTracking()
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ReminderEnabled && h.DueTime != null
                && h.DueDate <= maxLocalDate
                && (!h.EndDate.HasValue || h.EndDate.Value >= minLocalDate))
            .ToListAsync(ct);

        if (habits.Count == 0) return;

        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var habitIds = habits.Select(h => h.Id).ToList();
#pragma warning disable ORBIT0004
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
#pragma warning restore ORBIT0004
        var minWindowDate = utcToday.AddDays(-1);
        var maxWindowDate = utcToday.AddDays(1);

        var loggedHabitDates = (await dbContext.HabitLogs
            .AsNoTracking()
            .Where(l => habitIds.Contains(l.HabitId)
                && l.Date >= minWindowDate && l.Date <= maxWindowDate)
            .Select(l => new { l.HabitId, l.Date })
            .ToListAsync(ct))
            .Select(l => (l.HabitId, l.Date))
            .ToHashSet();

        var sentReminderKeys = await dbContext.SentReminders
            .AsNoTracking()
            .Where(r => habitIds.Contains(r.HabitId) && r.ReminderTimeUtc == null
                && r.Date >= minWindowDate && r.Date <= maxWindowDate)
            .Select(r => new { r.HabitId, r.Date, r.MinutesBefore })
            .ToListAsync(ct);
        var sentReminderSet = sentReminderKeys
            .Select(r => (r.HabitId, r.Date, r.MinutesBefore))
            .ToHashSet();

        foreach (var habit in habits)
        {
            await ProcessSingleRelativeReminderAsync(
                habit, users, loggedHabitDates, sentReminderSet, pending, dbContext, ct);
        }
    }

    private async Task ProcessSingleRelativeReminderAsync(
        Habit habit, Dictionary<Guid, User> users,
        HashSet<(Guid HabitId, DateOnly Date)> loggedHabitDates,
        HashSet<(Guid HabitId, DateOnly Date, int MinutesBefore)> sentReminderSet,
        List<PendingReminderPush> pending, OrbitDbContext dbContext, CancellationToken ct)
    {
        if (!users.TryGetValue(habit.UserId, out var user)) return;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        var userTimeNow = TimeOnly.FromDateTime(userNow);

        if (!HabitScheduleService.IsHabitDueOnDate(habit, userToday, user.WeekStartDay)) return;
        if (loggedHabitDates.Contains((habit.Id, userToday))) return;

        foreach (var minutesBefore in habit.ReminderTimes)
        {
            var reminderTime = habit.DueTime!.Value.AddMinutes(-minutesBefore);
            if (userTimeNow < reminderTime) continue;
            if (sentReminderSet.Contains((habit.Id, userToday, minutesBefore))) continue;

            var lang = user.Language ?? "en";
            var minutesText = FormatReminderText(minutesBefore, lang);

            var sentReminder = SentReminder.Create(habit.Id, userToday, minutesBefore);
            var notification = Notification.Create(
                habit.UserId, habit.Title, minutesText, NotificationUrls.Home, habit.Id);

            sentReminderSet.Add((habit.Id, userToday, minutesBefore));

            if (!await TryRecordReminderAsync(habit, sentReminder, notification, dbContext, ct))
                continue;

            pending.Add(new PendingReminderPush(habit.UserId, habit.Title, minutesText, habit.Id));

            if (logger.IsEnabled(LogLevel.Debug))
                LogSentReminder(logger, minutesBefore, habit.Id, habit.UserId);
        }
    }

    private async Task ProcessScheduledReminders(OrbitDbContext dbContext, List<PendingReminderPush> pending, CancellationToken ct)
    {
#pragma warning disable ORBIT0004
        var minLocalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var maxDayBeforeDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
#pragma warning restore ORBIT0004
        var habits = await dbContext.Habits
            .AsNoTracking()
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ReminderEnabled && h.DueTime == null
                && h.DueDate <= maxDayBeforeDate
                && (!h.EndDate.HasValue || h.EndDate.Value >= minLocalDate))
            .ToListAsync(ct);

        habits = habits.Where(h => h.ScheduledReminders.Count > 0).ToList();

        if (habits.Count == 0) return;

        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var habitIds = habits.Select(h => h.Id).ToList();
#pragma warning disable ORBIT0004
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
#pragma warning restore ORBIT0004

        var sentScheduledKeys = await dbContext.SentReminders
            .AsNoTracking()
            .Where(r => habitIds.Contains(r.HabitId) && r.ReminderTimeUtc != null
                && (r.Date == utcToday || r.Date == utcToday.AddDays(-1) || r.Date == utcToday.AddDays(1)))
            .Select(r => new { r.HabitId, r.Date, r.ReminderTimeUtc, r.When })
            .ToListAsync(ct);
        var sentScheduledSet = sentScheduledKeys
            .Select(r => (r.HabitId, r.Date, ReminderTimeUtc: r.ReminderTimeUtc!.Value, r.When))
            .ToHashSet();

        foreach (var habit in habits)
        {
            await ProcessSingleScheduledReminderAsync(
                habit, users, sentScheduledSet, pending, dbContext, ct);
        }
    }

    private async Task ProcessSingleScheduledReminderAsync(
        Habit habit, Dictionary<Guid, User> users,
        HashSet<(Guid HabitId, DateOnly Date, TimeOnly ReminderTimeUtc, ScheduledReminderWhen? When)> sentScheduledSet,
        List<PendingReminderPush> pending, OrbitDbContext dbContext, CancellationToken ct)
    {
        if (!users.TryGetValue(habit.UserId, out var user)) return;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        var userTomorrow = userToday.AddDays(1);
        var userTimeNow = TimeOnly.FromDateTime(userNow);

        var isDueToday = HabitScheduleService.IsHabitDueOnDate(habit, userToday, user.WeekStartDay);
        var isDueTomorrow = HabitScheduleService.IsHabitDueOnDate(habit, userTomorrow, user.WeekStartDay);

        foreach (var sr in habit.ScheduledReminders)
        {
            if (!ShouldSendScheduledReminder(sr, isDueToday, isDueTomorrow, userTimeNow))
                continue;

            if (sentScheduledSet.Contains((habit.Id, userToday, sr.Time, sr.When))
                || sentScheduledSet.Contains((habit.Id, userToday, sr.Time, null))) continue;

            var lang = user.Language ?? "en";
            var text = FormatScheduledReminderText(sr.When, lang);

            var sentReminder = SentReminder.Create(habit.Id, userToday, 0, sr.Time, sr.When);
            var notification = Notification.Create(
                habit.UserId, habit.Title, text, NotificationUrls.Home, habit.Id);

            sentScheduledSet.Add((habit.Id, userToday, sr.Time, sr.When));

            if (!await TryRecordReminderAsync(habit, sentReminder, notification, dbContext, ct))
                continue;

            pending.Add(new PendingReminderPush(habit.UserId, habit.Title, text, habit.Id));

            if (logger.IsEnabled(LogLevel.Debug))
                LogSentScheduledReminder(logger, sr.When, sr.Time, habit.Id, habit.UserId);
        }
    }

    private static bool ShouldSendScheduledReminder(
        Domain.ValueObjects.ScheduledReminderTime sr, bool isDueToday, bool isDueTomorrow, TimeOnly userTimeNow)
    {
        if (sr.When == ScheduledReminderWhen.SameDay && !isDueToday) return false;
        if (sr.When == ScheduledReminderWhen.DayBefore && !isDueTomorrow) return false;
        if (sr.When != ScheduledReminderWhen.SameDay && sr.When != ScheduledReminderWhen.DayBefore) return false;

        return userTimeNow >= sr.Time;
    }

    private async Task<bool> TryRecordReminderAsync(
        Habit habit, SentReminder sentReminder, Notification notification,
        OrbitDbContext dbContext, CancellationToken ct)
    {
        await dbContext.SentReminders.AddAsync(sentReminder, ct);
        await dbContext.Notifications.AddAsync(notification, ct);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            DetachPendingEntries(dbContext);
            if (logger.IsEnabled(LogLevel.Debug))
                LogReminderAlreadySent(logger, habit.Id);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetachPendingEntries(dbContext);
            LogReminderRecordFailed(logger, habit.Id, habit.UserId, ex);
            return false;
        }

        return true;
    }

    private async Task SendPushesAsync(IReadOnlyList<PendingReminderPush> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return;

        var concurrency = Math.Min(_pushConcurrency, pending.Count);
        var queue = new ConcurrentQueue<PendingReminderPush>(pending);
        var workers = new Task[concurrency];
        for (var i = 0; i < concurrency; i++)
            workers[i] = DrainPushQueueAsync(queue, ct);

        await Task.WhenAll(workers);
    }

    private async Task DrainPushQueueAsync(ConcurrentQueue<PendingReminderPush> queue, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        while (queue.TryDequeue(out var push))
        {
            try
            {
                await pushService.SendToUserAsync(
                    push.UserId, push.Title, push.Body, NotificationUrls.Home, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogReminderPushFailed(logger, push.HabitId, push.UserId, ex);
            }
        }
    }

    private static void DetachPendingEntries(OrbitDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }

    private readonly record struct PendingReminderPush(Guid UserId, string Title, string Body, Guid HabitId);

    private static string Pluralize(string singular, int count) => count > 1 ? singular + "s" : singular;

    private static string FormatReminderText(int minutesBefore, string lang)
    {
        var isPt = LocaleHelper.IsPortuguese(lang);
        return minutesBefore switch
        {
            0 => isPt ? "Agora" : "Due now",
            < 60 => isPt
                ? $"Em {minutesBefore} {Pluralize("minuto", minutesBefore)}"
                : $"Due in {minutesBefore} minutes",
            < 1440 => isPt
                ? $"Em {minutesBefore / 60} {Pluralize("hora", minutesBefore / 60)}"
                : $"Due in {minutesBefore / 60} {Pluralize("hour", minutesBefore / 60)}",
            _ => isPt
                ? $"Em {minutesBefore / 1440} {Pluralize("dia", minutesBefore / 1440)}"
                : $"Due in {minutesBefore / 1440} {Pluralize("day", minutesBefore / 1440)}"
        };
    }

    private static string FormatScheduledReminderText(ScheduledReminderWhen when, string lang)
    {
        var isPt = LocaleHelper.IsPortuguese(lang);
        return when switch
        {
            ScheduledReminderWhen.SameDay => isPt ? "Para hoje" : "Due today",
            ScheduledReminderWhen.DayBefore => isPt ? "Para amanhã" : "Due tomorrow",
            _ => isPt ? "Lembrete" : "Reminder"
        };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "ReminderSchedulerService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "ReminderSchedulerService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in reminder scheduler")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Sent reminder ({Minutes}min) for habit {HabitId} to user {UserId}")]
    private static partial void LogSentReminder(ILogger logger, int minutes, Guid habitId, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Sent scheduled reminder ({When} at {Time}) for habit {HabitId} to user {UserId}")]
    private static partial void LogSentScheduledReminder(ILogger logger, Orbit.Domain.Enums.ScheduledReminderWhen when, TimeOnly time, Guid habitId, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Reminder already recorded for habit {HabitId}; skipping push")]
    private static partial void LogReminderAlreadySent(ILogger logger, Guid habitId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to record reminder for habit {HabitId} (user {UserId}); skipping push")]
    private static partial void LogReminderRecordFailed(ILogger logger, Guid habitId, Guid userId, Exception ex);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Failed to deliver reminder push for habit {HabitId} (user {UserId})")]
    private static partial void LogReminderPushFailed(ILogger logger, Guid habitId, Guid userId, Exception ex);

}
