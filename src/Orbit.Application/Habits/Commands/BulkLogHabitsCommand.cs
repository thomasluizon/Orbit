using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkLogItem(Guid HabitId, DateOnly? Date = null) : IBulkHabitItem;

public record BulkLogHabitsCommand(
    Guid UserId,
    IReadOnlyList<BulkLogItem> Items) : IRequest<Result<BulkLogResult>>, IBulkHabitCommand<BulkLogItem>;

public record BulkLogResult(IReadOnlyList<BulkLogItemResult> Results);

public record BulkLogItemResult(
    int Index,
    BulkItemStatus Status,
    Guid HabitId,
    Guid? LogId = null,
    string? Error = null,
    string? ErrorCode = null);

/// <summary>
/// Groups supporting services for bulk habit logging to reduce constructor parameter count (S107).
/// </summary>
public record BulkLogServices(
    IUserDateService UserDateService,
    IUserStreakService UserStreakService,
    IGamificationService GamificationService);

public partial class BulkLogHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    BulkLogServices services,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkLogHabitsCommandHandler> logger) : IRequestHandler<BulkLogHabitsCommand, Result<BulkLogResult>>
{
    public async Task<Result<BulkLogResult>> Handle(BulkLogHabitsCommand request, CancellationToken cancellationToken)
    {
        var today = await services.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var weekStartDay = await services.UserDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var results = new List<BulkLogItemResult>();

        var habitMap = await BulkHabitLoader.LoadHabitsWithRecentLogsAsync(
            habitRepository, request.Items.Select(i => i.HabitId), request.UserId, today, cancellationToken);
        var dueDateResolution = await HabitDueDateResolutionLoader.LoadAsync(
            habitLogRepository,
            habitMap.Values,
            today.AddDays(-AppConstants.MaxRangeDays),
            cancellationToken);

        /** A completion log and its due-date advance are both inputs a streak repair reads. */
        await HabitCeilingLock.ExecuteAsync(unitOfWork, request.UserId, async ct =>
        {
            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                var targetDate = item.Date ?? today;

                try
                {
                    results.Add(await ProcessLogItem(
                        i,
                        item.HabitId,
                        targetDate,
                        today,
                        weekStartDay,
                        habitMap,
                        dueDateResolution,
                        ct));
                }
                catch (Exception ex)
                {
                    LogBulkLogItemError(logger, ex, item.HabitId);
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: item.HabitId,
                        Error: ErrorMessages.BulkLogItemFailed.Message,
                        ErrorCode: ErrorMessages.BulkLogItemFailed.Code));
                }
            }

            var changedHabitIds = results
                .Where(r => r.Status == BulkItemStatus.Success && r.LogId is not null)
                .Select(r => r.HabitId)
                .ToHashSet();
            var goalIds = habitMap.Values
                .Where(h => changedHabitIds.Contains(h.Id))
                .SelectMany(h => h.Goals)
                .Select(g => g.Id)
                .ToHashSet();
            await goalCompletionService.SyncDerivedGoalsAsync(request.UserId, goalIds, today, cancellationToken: ct);

            var loggedHabitIds = changedHabitIds.ToList();
            if (loggedHabitIds.Count > 0)
            {
                try
                {
                    await services.GamificationService.ProcessHabitsLogged(request.UserId, loggedHabitIds, ct);
                }
                catch (Exception ex) { LogGamificationBulkLogFailed(logger, ex, request.UserId); }

                await ConcurrencyRetry.SaveWithRetryAsync(
                    unitOfWork,
                    c => services.UserStreakService.RecalculateAsync(request.UserId, cancellationToken: c),
                    ct);

            }
        }, cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success(new BulkLogResult(results));
    }

    private async Task<BulkLogItemResult> ProcessLogItem(
        int index, Guid habitId, DateOnly targetDate, DateOnly today, int weekStartDay,
        Dictionary<Guid, Habit> habitMap,
        IReadOnlySet<Guid> dueDateResolution,
        CancellationToken cancellationToken)
    {
        if (targetDate > today)
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.CannotLogFutureDate.Message, ErrorCode: ErrorMessages.CannotLogFutureDate.Code);

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.BeyondOverdueWindow.Message, ErrorCode: ErrorMessages.BeyondOverdueWindow.Code);

        if (!habitMap.TryGetValue(habitId, out var habit))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.HabitNotFound.Message, ErrorCode: ErrorMessages.HabitNotFound.Code);

        if (habit.FrequencyUnit is not null
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate, weekStartDay))
        {
            var isOverdue = !habit.IsFlexible
                && targetDate == today
                && HabitScheduleService.HasMissedPastOccurrence(
                    habit,
                    today,
                    weekStartDay,
                    dueDateResolution.Contains(habit.Id));
            if (!isOverdue)
                return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                    Error: ErrorMessages.NotScheduledOnDate.Message, ErrorCode: ErrorMessages.NotScheduledOnDate.Code);
        }

        if (habit.Logs.Any(l => l.Date == targetDate))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);

        var shouldAdvanceDueDate = targetDate >= today;
        var logResult = habit.Log(
            targetDate,
            advanceDueDate: shouldAdvanceDueDate,
            weekStartDay: weekStartDay);
        if (logResult.IsFailure)
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: logResult.Error, ErrorCode: logResult.ErrorCode);

        await habitLogRepository.AddAsync(logResult.Value, cancellationToken);

        return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId,
            LogId: logResult.Value.Id);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Error processing bulk item {HabitId}")]
    private static partial void LogBulkLogItemError(ILogger logger, Exception ex, Guid habitId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Gamification processing failed for bulk log by user {UserId}")]
    private static partial void LogGamificationBulkLogFailed(ILogger logger, Exception ex, Guid userId);

}
