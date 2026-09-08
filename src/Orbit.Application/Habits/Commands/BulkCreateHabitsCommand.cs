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

public record BulkCreateHabitsCommand(
    Guid UserId,
    IReadOnlyList<BulkHabitItem> Habits,
    bool FromSyncReview = false) : IRequest<Result<BulkCreateResult>>;

public record BulkHabitItem(
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    IReadOnlyList<BulkHabitItem>? SubHabits = null,
    bool IsGeneral = false,
    DateOnly? EndDate = null,
    bool IsFlexible = false,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    string? GoogleEventId = null,
    string? Emoji = null,
    IReadOnlyList<string>? Tags = null,
    int? IntervalWeeks = null);

public record BulkCreateResult(IReadOnlyList<BulkCreateItemResult> Results);

public record BulkCreateItemResult(
    int Index,
    BulkItemStatus Status,
    Guid? HabitId = null,
    string? Title = null,
    string? Error = null,
    string? Field = null);

public enum BulkItemStatus { Success, Failed }

/// <summary>Groups the repositories a bulk habit create touches to keep the handler constructor small.</summary>
public record BulkCreateHabitsRepositories(
    IGenericRepository<Habit> Habits,
    IGenericRepository<GoogleCalendarSyncSuggestion> Suggestions,
    IGenericRepository<Tag> Tags);

public partial class BulkCreateHabitsCommandHandler(
    BulkCreateHabitsRepositories repos,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkCreateHabitsCommandHandler> logger) : IRequestHandler<BulkCreateHabitsCommand, Result<BulkCreateResult>>
{
    private const string DefaultTagColor = "#7c3aed";

    public async Task<Result<BulkCreateResult>> Handle(BulkCreateHabitsCommand request, CancellationToken cancellationToken)
    {
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var result = await HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            request.UserId,
            ct => HandleLockedAsync(request, userToday, ct),
            cancellationToken);

        if (result.IsSuccess)
            CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, userToday);

        return result;
    }

    private async Task<Result<BulkCreateResult>> HandleLockedAsync(
        BulkCreateHabitsCommand request,
        DateOnly userToday,
        CancellationToken cancellationToken)
    {
        var parentCount = request.Habits.Count;
        var habitGate = await payGate.CanCreateHabits(request.UserId, parentCount, cancellationToken);
        if (habitGate.IsFailure)
            return habitGate.PropagateError<BulkCreateResult>();

        var hasSubHabits = request.Habits.Any(h => h.SubHabits is { Count: > 0 });
        if (hasSubHabits)
        {
            var subGate = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
            if (subGate.IsFailure)
                return subGate.PropagateError<BulkCreateResult>();
        }

        var results = new List<BulkCreateItemResult>();

        var existingRoots = await repos.Habits.FindAsync(
            h => h.UserId == request.UserId && h.ParentHabitId == null && !h.IsDeleted,
            cancellationToken);
        var rootPositionCursor = existingRoots.Count == 0
            ? 0
            : existingRoots.Max(h => h.Position ?? -1) + 1;

        var tagsByName = await LoadTagCacheAsync(request, cancellationToken);

        for (int i = 0; i < request.Habits.Count; i++)
        {
            var itemResult = await CreateSingleHabit(
                request.UserId,
                request.Habits[i],
                i,
                userToday,
                rootPositionCursor + i,
                tagsByName,
                cancellationToken);
            results.Add(itemResult);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (request.FromSyncReview)
        {
            await MarkSyncSuggestionsImported(request.UserId, results, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(new BulkCreateResult(results));
    }

    private async Task<BulkCreateItemResult> CreateSingleHabit(
        Guid userId, BulkHabitItem item, int index, DateOnly userToday, int rootPosition,
        Dictionary<string, Tag> tagsByName, CancellationToken cancellationToken)
    {
        try
        {
            var habitResult = Habit.Create(new HabitCreateParams(
                userId,
                item.Title,
                item.FrequencyUnit,
                item.FrequencyQuantity,
                item.DueDate ?? userToday,
                item.Description,
                Emoji: item.Emoji,
                Days: item.Days,
                IsBadHabit: item.IsBadHabit,
                DueTime: item.DueTime,
                DueEndTime: item.DueEndTime,
                ReminderEnabled: item.ReminderEnabled,
                ReminderTimes: item.ReminderTimes,
                IsGeneral: item.IsGeneral,
                IsFlexible: item.IsFlexible,
                ScheduledReminders: item.ScheduledReminders,
                ChecklistItems: item.ChecklistItems,
                Position: rootPosition,
                GoogleEventId: item.GoogleEventId,
                IntervalWeeks: item.IntervalWeeks));

            if (habitResult.IsFailure)
            {
                return new BulkCreateItemResult(
                    Index: index,
                    Status: BulkItemStatus.Failed,
                    Title: item.Title,
                    Error: habitResult.Error,
                    Field: DetermineFieldFromError(habitResult.Error));
            }

            var parentHabit = habitResult.Value;
            await repos.Habits.AddAsync(parentHabit, cancellationToken);

            await AttachTagsAsync(parentHabit, userId, item.Tags, tagsByName, cancellationToken);

            if (item.SubHabits is { Count: > 0 })
            {
                var subPositionCursor = 0;
                foreach (var subItem in item.SubHabits)
                {
                    var hasExplicitCadence = subItem.FrequencyUnit is not null
                        || subItem.FrequencyQuantity is not null
                        || subItem.IntervalWeeks is not null
                        || subItem.Days is not null;
                    var childFrequencyUnit = subItem.FrequencyUnit ?? item.FrequencyUnit;
                    var childFrequencyQuantity = subItem.FrequencyQuantity ?? item.FrequencyQuantity;
                    var childDays = subItem.Days;
                    if (childDays is null
                        && childFrequencyUnit == FrequencyUnit.Day
                        && childFrequencyQuantity == 1)
                    {
                        childDays = item.Days;
                    }

                    var childResult = Habit.Create(new HabitCreateParams(
                        userId,
                        subItem.Title,
                        childFrequencyUnit,
                        childFrequencyQuantity,
                        subItem.DueDate ?? item.DueDate ?? userToday,
                        subItem.Description,
                        Emoji: subItem.Emoji,
                        Days: childDays,
                        IsBadHabit: subItem.IsBadHabit,
                        ParentHabitId: parentHabit.Id,
                        IsGeneral: item.IsGeneral,
                        IsFlexible: subItem.IsFlexible,
                        Position: subPositionCursor++,
                        IntervalWeeks: subItem.IntervalWeeks
                            ?? (hasExplicitCadence ? null : item.IntervalWeeks)));

                    if (childResult.IsFailure)
                    {
                        repos.Habits.Remove(parentHabit);
                        return new BulkCreateItemResult(
                            Index: index,
                            Status: BulkItemStatus.Failed,
                            Title: item.Title,
                            Error: $"Sub-habit '{subItem.Title}' failed: {childResult.Error}",
                            Field: "SubHabits");
                    }

                    await repos.Habits.AddAsync(childResult.Value, cancellationToken);
                }
            }

            return new BulkCreateItemResult(
                Index: index,
                Status: BulkItemStatus.Success,
                HabitId: parentHabit.Id,
                Title: parentHabit.Title);
        }
        catch (Exception ex)
        {
            LogBulkCreateItemFailed(logger, ex, index);
            return new BulkCreateItemResult(
                Index: index,
                Status: BulkItemStatus.Failed,
                Title: item.Title,
                Error: "An error occurred processing this item");
        }
    }

    private async Task<Dictionary<string, Tag>> LoadTagCacheAsync(
        BulkCreateHabitsCommand request, CancellationToken cancellationToken)
    {
        var tagsByName = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        if (!request.Habits.Any(h => h.Tags is { Count: > 0 }))
            return tagsByName;

        var existingTags = await repos.Tags.FindTrackedAsync(t => t.UserId == request.UserId, cancellationToken);
        foreach (var tag in existingTags)
            tagsByName[tag.Name] = tag;

        return tagsByName;
    }

    private async Task AttachTagsAsync(
        Habit habit, Guid userId, IReadOnlyList<string>? tagNames,
        Dictionary<string, Tag> tagsByName, CancellationToken cancellationToken)
    {
        if (tagNames is not { Count: > 0 })
            return;

        foreach (var rawName in tagNames)
        {
            var trimmed = rawName.Trim();
            if (trimmed.Length == 0)
                continue;

            if (!tagsByName.TryGetValue(trimmed, out var tag))
            {
                var created = Tag.Create(userId, trimmed, DefaultTagColor);
                if (created.IsFailure)
                    continue;

                tag = created.Value;
                await repos.Tags.AddAsync(tag, cancellationToken);
                tagsByName[tag.Name] = tag;
            }

            habit.AddTag(tag);
        }
    }

    private static string? DetermineFieldFromError(string error)
    {
        if (error.Contains("title", StringComparison.OrdinalIgnoreCase))
            return "Title";
        if (error.Contains("frequency", StringComparison.OrdinalIgnoreCase))
            return "FrequencyUnit";
        if (error.Contains("days", StringComparison.OrdinalIgnoreCase))
            return "Days";
        return null;
    }

    private async Task MarkSyncSuggestionsImported(
        Guid userId,
        List<BulkCreateItemResult> results,
        CancellationToken cancellationToken)
    {
        var createdHabitIds = results
            .Where(r => r.Status == BulkItemStatus.Success && r.HabitId is not null)
            .Select(r => r.HabitId!.Value)
            .ToList();

        if (createdHabitIds.Count == 0)
            return;

        var createdHabits = await repos.Habits.FindAsync(
            h => createdHabitIds.Contains(h.Id) && h.GoogleEventId != null,
            cancellationToken);

        var habitsByEventId = createdHabits
            .Where(h => h.GoogleEventId is not null)
            .ToDictionary(h => h.GoogleEventId!, h => h.Id);

        if (habitsByEventId.Count == 0)
            return;

        var eventIds = habitsByEventId.Keys.ToList();
        var suggestions = await repos.Suggestions.FindAsync(
            s => s.UserId == userId && eventIds.Contains(s.GoogleEventId) && s.ImportedAtUtc == null,
            cancellationToken);

#pragma warning disable ORBIT0004
        var now = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        foreach (var suggestion in suggestions)
        {
            if (habitsByEventId.TryGetValue(suggestion.GoogleEventId, out var habitId))
                suggestion.MarkImported(habitId, now);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "BulkCreate item {Index} failed")]
    private static partial void LogBulkCreateItemFailed(ILogger logger, Exception ex, int index);
}
