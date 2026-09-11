using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkUpdateHabitsCommandHandler> logger) : IRequestHandler<BulkUpdateHabitsCommand, Result<BulkHabitMutationResult>>
{
    internal const int ChunkSize = 100;

    public async Task<Result<BulkHabitMutationResult>> Handle(
        BulkUpdateHabitsCommand request,
        CancellationToken cancellationToken)
    {
        var habits = await BulkHabitSelection.LoadAsync(
            habitRepository,
            request.UserId,
            request.Filter,
            cancellationToken);
        var totalMatched = habits.Count;
        var appliedCount = 0;
        var stopped = false;
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        foreach (var chunk in habits.Chunk(ChunkSize))
        {
            var chunkApplied = 0;
            try
            {
                await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
                {
                    foreach (var habit in chunk)
                    {
                        var update = ResolveUpdate(habit, request.Changes, today);
                        if (habit.Update(update).IsSuccess)
                            chunkApplied++;
                    }

                    if (chunkApplied > 0)
                        await unitOfWork.SaveChangesAsync(transactionToken);
                }, cancellationToken);
                appliedCount += chunkApplied;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unitOfWork.DiscardChanges();
                LogChunkFailed(logger, appliedCount, totalMatched, ex);
                stopped = true;
                break;
            }
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

    private static HabitUpdateParams ResolveUpdate(Habit habit, BulkHabitChanges changes, DateOnly today)
    {
        return new HabitUpdateParams(
            changes.HasTitle ? changes.Title ?? habit.Title : habit.Title,
            changes.HasDescription ? changes.Description : habit.Description,
            changes.HasFrequencyUnit ? changes.FrequencyUnit : habit.FrequencyUnit,
            changes.HasFrequencyQuantity ? changes.FrequencyQuantity : habit.FrequencyQuantity,
            changes.HasDays ? changes.Days : habit.Days.ToList(),
            changes.HasIsBadHabit ? changes.IsBadHabit : habit.IsBadHabit,
            changes.HasDueDate ? changes.DueDate : habit.DueDate,
            DueTime: changes.HasDueTime ? changes.DueTime : habit.DueTime,
            DueEndTime: habit.DueEndTime,
            ReminderEnabled: changes.HasReminderEnabled ? changes.ReminderEnabled : null,
            ReminderTimes: changes.HasReminderTimes ? changes.ReminderTimes : null,
            ChecklistItems: changes.HasChecklistItems ? changes.ChecklistItems : null,
            IsFlexible: changes.HasIsFlexible ? changes.IsFlexible : null,
            EndDate: changes.HasEndDate ? changes.EndDate : null,
            ClearEndDate: changes.HasEndDate && changes.EndDate is null,
            ScheduledReminders: changes.HasScheduledReminders ? changes.ScheduledReminders : null,
            Emoji: changes.HasEmoji ? changes.Emoji : habit.Emoji,
            UserToday: today,
            IntervalWeeks: changes.HasIntervalWeeks ? changes.IntervalWeeks : habit.IntervalWeeks);
    }
}
