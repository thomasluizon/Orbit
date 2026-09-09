using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class HabitDueDateAdvancementService(
    IServiceScopeFactory scopeFactory,
    ILogger<HabitDueDateAdvancementService> logger,
    IConfiguration configuration) : BackgroundService, IScheduledJob
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:DueDateAdvancementIntervalMinutes", 30));

    public string Name => "habit-due-date-advancement";

    public string CronExpression => "*/30 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await AdvanceStaleDueDates(cancellationToken);
        BackgroundServiceHealthCheck.RecordTick("HabitDueDateAdvancement");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await AdvanceStaleDueDates(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("HabitDueDateAdvancement");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogServiceError(logger, ex);
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        finally
        {
            LogServiceStopped(logger);
        }
    }

    internal static DateOnly ConservativeCutoffUtc() =>
#pragma warning disable ORBIT0004
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
#pragma warning restore ORBIT0004

    internal static Expression<Func<Habit, bool>> StaleBadHabitFilter(DateOnly cutoff) =>
        h => !h.IsCompleted
            && h.FrequencyUnit != null
            && h.FrequencyQuantity != null
            && !h.IsFlexible
            && h.IsBadHabit
            && h.DueDate < cutoff;

    internal static bool ShouldAdvanceForUserToday(Habit habit, DateOnly userToday) =>
        habit.DueDate < userToday;

    internal async Task AdvanceStaleDueDates(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var cutoff = ConservativeCutoffUtc();
        var habits = await dbContext.Habits
            .Where(StaleBadHabitFilter(cutoff))
            .ToListAsync(ct);

        if (habits.Count == 0) return;

        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var advanced = 0;
        foreach (var habit in habits)
        {
            try
            {
                if (!users.TryGetValue(habit.UserId, out var user)) continue;

                var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
                var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                var userToday = DateOnly.FromDateTime(userNow);

                if (!ShouldAdvanceForUserToday(habit, userToday)) continue;

                var result = habit.CatchUpDueDate(userToday, user.WeekStartDay);
                if (result.IsFailure)
                {
                    LogHabitSkipped(logger, habit.Id, result.ErrorCode);
                    continue;
                }

                advanced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogHabitUnexpectedError(logger, ex, habit.Id);
            }
        }

        if (advanced > 0)
        {
            await dbContext.SaveChangesAsync(ct);
            if (logger.IsEnabled(LogLevel.Debug))
                LogDueDatesAdvanced(logger, advanced);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "HabitDueDateAdvancementService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "HabitDueDateAdvancementService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in habit due date advancement")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Advanced DueDate for {Count} bad habits")]
    private static partial void LogDueDatesAdvanced(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Skipped due date advancement for habit {HabitId}: {ErrorCode}")]
    private static partial void LogHabitSkipped(ILogger logger, Guid habitId, string? errorCode);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Skipped due date advancement for habit {HabitId} after unexpected error")]
    private static partial void LogHabitUnexpectedError(ILogger logger, Exception ex, Guid habitId);

}
