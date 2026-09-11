using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Commands;

public sealed record BulkHabitChanges(
    bool HasTitle = false,
    string? Title = null,
    bool HasDescription = false,
    string? Description = null,
    bool HasEmoji = false,
    string? Emoji = null,
    bool HasFrequencyUnit = false,
    FrequencyUnit? FrequencyUnit = null,
    bool HasFrequencyQuantity = false,
    int? FrequencyQuantity = null,
    bool HasIntervalWeeks = false,
    int? IntervalWeeks = null,
    bool HasDays = false,
    IReadOnlyList<DayOfWeek>? Days = null,
    bool HasDueDate = false,
    DateOnly? DueDate = null,
    bool HasEndDate = false,
    DateOnly? EndDate = null,
    bool HasDueTime = false,
    TimeOnly? DueTime = null,
    bool HasIsBadHabit = false,
    bool IsBadHabit = false,
    bool HasIsFlexible = false,
    bool IsFlexible = false,
    bool HasReminderEnabled = false,
    bool ReminderEnabled = false,
    bool HasReminderTimes = false,
    IReadOnlyList<int>? ReminderTimes = null,
    bool HasChecklistItems = false,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    bool HasScheduledReminders = false,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null)
{
    public bool HasAnyChange =>
        HasTitle || HasDescription || HasEmoji || HasFrequencyUnit || HasFrequencyQuantity
        || HasIntervalWeeks || HasDays || HasDueDate || HasEndDate || HasDueTime
        || HasIsBadHabit || HasIsFlexible || HasReminderEnabled || HasReminderTimes
        || HasChecklistItems || HasScheduledReminders;
}

public sealed record BulkUpdateHabitsCommand(
    Guid UserId,
    BulkHabitFilter Filter,
    BulkHabitChanges Changes) : IRequest<Result<BulkHabitMutationResult>>;

public sealed record BulkHabitMutationResult(
    int AppliedCount,
    int TotalMatched,
    int SkippedCount,
    bool Partial);

public sealed partial class BulkUpdateHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkUpdateHabitsCommandHandler> logger) : IRequestHandler<BulkUpdateHabitsCommand, Result<BulkHabitMutationResult>>
{
    internal const int ChunkSize = 100;

    public async Task<Result<BulkHabitMutationResult>> Handle(
        BulkUpdateHabitsCommand request,
        CancellationToken cancellationToken)
    {
        var selectedHabits = await BulkHabitSelection.LoadAsync(
            habitRepository,
            request.UserId,
            request.Filter,
            cancellationToken);
        var selectedIds = selectedHabits.Select(habit => habit.Id).ToList();
        var totalMatched = selectedIds.Count;
        var appliedCount = 0;
        var stopped = false;
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        foreach (var chunk in selectedIds.Chunk(ChunkSize))
        {
            Result<int> chunkResult;
            try
            {
                chunkResult = await HabitCeilingLock.ExecuteAsync(
                    unitOfWork,
                    request.UserId,
                    transactionToken => ApplyChunkAsync(
                        request.UserId,
                        chunk,
                        request.Changes,
                        today,
                        transactionToken),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unitOfWork.DiscardChanges();
                LogChunkFailed(logger, appliedCount, totalMatched, ex);
                stopped = true;
                break;
            }

            if (chunkResult.IsFailure)
            {
                if (appliedCount == 0)
                    return chunkResult.PropagateError<BulkHabitMutationResult>();
                stopped = true;
                break;
            }

            appliedCount += chunkResult.Value;
        }

        if (appliedCount > 0)
            CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        var skippedCount = totalMatched - appliedCount;
        return Result.Success(new BulkHabitMutationResult(
            appliedCount,
            totalMatched,
            skippedCount,
            stopped || skippedCount > 0));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Bulk habit update chunk failed after {AppliedCount} of {TotalMatched} matches")]
    private static partial void LogChunkFailed(ILogger logger, int appliedCount, int totalMatched, Exception ex);

    private async Task<Result<int>> ApplyChunkAsync(
        Guid userId,
        IReadOnlyCollection<Guid> habitIds,
        BulkHabitChanges changes,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindTrackedAsync(
            habit => habit.UserId == userId && habitIds.Contains(habit.Id),
            query => query,
            cancellationToken);
        var updates = habits
            .Select(habit => (Habit: habit, Update: ResolveUpdate(habit, changes, today)))
            .ToList();

        foreach (var item in updates)
        {
            var validation = item.Habit.ValidateUpdate(item.Update);
            if (validation.IsFailure)
                return validation.PropagateError<int>();
        }

        var liveRootEntries = updates.Count(item => HabitLiveRootEntry.FromUpdate(item.Habit, item.Update));
        if (liveRootEntries > 0)
        {
            var allowance = await payGate.CanCreateHabits(userId, liveRootEntries, cancellationToken);
            if (allowance.IsFailure)
                return allowance.PropagateError<int>();
        }

        foreach (var item in updates)
            item.Habit.Update(item.Update);

        if (updates.Count > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(updates.Count);
    }

    private static HabitUpdateParams ResolveUpdate(Habit habit, BulkHabitChanges changes, DateOnly today)
    {
        var dueTime = changes.HasDueTime ? changes.DueTime : habit.DueTime;
        var (reminderTimes, scheduledReminders) = ReminderStoreNormalizer.NormalizeForUpdate(
            dueTime,
            habit.DueTime,
            changes.HasReminderTimes ? changes.ReminderTimes?.ToList() ?? [] : null,
            changes.HasScheduledReminders ? changes.ScheduledReminders?.ToList() ?? [] : null,
            habit.ReminderTimes,
            habit.ScheduledReminders);

        return new HabitUpdateParams(
            changes.HasTitle ? changes.Title ?? habit.Title : habit.Title,
            changes.HasDescription ? changes.Description : habit.Description,
            changes.HasFrequencyUnit ? changes.FrequencyUnit : habit.FrequencyUnit,
            changes.HasFrequencyQuantity ? changes.FrequencyQuantity : habit.FrequencyQuantity,
            changes.HasDays ? changes.Days : habit.Days.ToList(),
            changes.HasIsBadHabit ? changes.IsBadHabit : habit.IsBadHabit,
            changes.HasDueDate ? changes.DueDate : habit.DueDate,
            DueTime: dueTime,
            DueEndTime: habit.DueEndTime,
            ReminderEnabled: changes.HasReminderEnabled ? changes.ReminderEnabled : null,
            ReminderTimes: reminderTimes,
            ChecklistItems: changes.HasChecklistItems ? changes.ChecklistItems : null,
            IsFlexible: changes.HasIsFlexible ? changes.IsFlexible : null,
            EndDate: changes.HasEndDate ? changes.EndDate : null,
            ClearEndDate: changes.HasEndDate && changes.EndDate is null,
            ScheduledReminders: scheduledReminders,
            Emoji: changes.HasEmoji ? changes.Emoji : habit.Emoji,
            UserToday: today,
            IntervalWeeks: changes.HasIntervalWeeks ? changes.IntervalWeeks : habit.IntervalWeeks);
    }
}
