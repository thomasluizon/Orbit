using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Habits.Queries;

internal static class HabitScheduleFilters
{
    internal static IEnumerable<Habit> ApplyFrequencyUnitFilter(
        IEnumerable<Habit> habits, string? frequencyUnitFilter)
    {
        if (string.IsNullOrWhiteSpace(frequencyUnitFilter))
            return habits;

        if (frequencyUnitFilter.Equals("none", StringComparison.OrdinalIgnoreCase))
            return habits.Where(h => h.FrequencyUnit == null);

        if (Enum.TryParse<FrequencyUnit>(frequencyUnitFilter, true, out var unit))
            return habits.Where(h => h.FrequencyUnit == unit);

        return habits;
    }

    internal static bool NeedsTagsForFiltering(GetHabitScheduleQuery request)
    {
        return !string.IsNullOrWhiteSpace(request.Search)
            || request.TagIds is { Count: > 0 };
    }

    internal static void AddSubtreeIds(Guid habitId, ILookup<Guid?, Habit> lookup, HashSet<Guid> ids)
    {
        if (!ids.Add(habitId))
            return;

        foreach (var child in lookup[habitId])
            AddSubtreeIds(child.Id, lookup, ids);
    }

    internal static IQueryable<Habit> IncludeHabitGraph(
        IQueryable<Habit> query,
        DateOnly logFrom,
        DateOnly logTo,
        bool includeTags,
        bool includeGoals)
    {
        query = query.Include(h => h.Logs.Where(l => l.Date >= logFrom && l.Date <= logTo));

        if (includeTags)
            query = query.Include(h => h.Tags);

        if (includeGoals)
            query = query.Include(h => h.Goals);

        return query.AsSplitQuery();
    }

    internal static List<(Habit habit, List<DateOnly> scheduledDates, bool isOverdue)> FilterScheduledHabits(
        IEnumerable<Habit> topLevel,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeOverdue,
        ILookup<Guid?, Habit> lookup,
        int weekStartDay,
        IReadOnlySet<Guid> dueDateResolution)
    {
        var filtered = new List<(Habit habit, List<DateOnly> scheduledDates, bool isOverdue)>();

        foreach (var habit in topLevel)
        {
            var hasCompletedLogInRange = HabitScheduleService.HasCompletedLogInRange(habit, dateFrom, dateTo);
            var scheduledDates = HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo, weekStartDay);

            if (habit.IsFlexible
                && !hasCompletedLogInRange
                && !scheduledDates.Any(date =>
                    HabitScheduleService.IsFlexibleHabitDueOnDate(habit, date, habit.Logs, weekStartDay)))
                continue;

            var isOverdue = DetermineOverdueStatus(habit, dateFrom, includeOverdue, weekStartDay, dueDateResolution);
            var hasDescendantDue = HasAnyDescendantDue(
                habit.Id,
                lookup,
                dateFrom,
                dateTo,
                includeOverdue,
                weekStartDay,
                dueDateResolution);

            if (scheduledDates.Count > 0 || isOverdue || hasDescendantDue || hasCompletedLogInRange)
                filtered.Add((habit, scheduledDates, isOverdue));
        }

        return filtered;
    }

    /// <summary>
    /// Whether a habit is overdue on the reference date, honoring the request's
    /// <paramref name="includeOverdue"/> flag. Delegates the overdue rule to
    /// <see cref="HabitScheduleService.IsOverdueOnDate"/> so the schedule query and the
    /// daily summary share a single definition of "overdue".
    /// </summary>
    private static bool DetermineOverdueStatus(
        Habit habit,
        DateOnly dateFrom,
        bool includeOverdue,
        int weekStartDay,
        IReadOnlySet<Guid>? dueDateResolution = null) =>
        includeOverdue && HabitScheduleService.IsOverdueOnDate(
            habit,
            dateFrom,
            weekStartDay,
            dueDateResolution?.Contains(habit.Id) == true);

    internal static IEnumerable<Habit> ApplyCommonFilters(
        IEnumerable<Habit> topLevel,
        GetHabitScheduleQuery request,
        ILookup<Guid?, Habit> lookup,
        DateOnly? userToday = null,
        int weekStartDay = 1,
        IReadOnlySet<Guid>? dueDateResolution = null)
    {
        if (!string.IsNullOrWhiteSpace(request.Search))
            topLevel = ApplySearchFilter(
                topLevel,
                request.Search.Trim(),
                request.DateFrom,
                request.DateTo,
                request.IncludeOverdue,
                lookup,
                weekStartDay,
                dueDateResolution);

        if (request.IsCompleted.HasValue)
            topLevel = topLevel.Where(h =>
                GetResponseCompletion(h, userToday) == request.IsCompleted.Value);

        if (request.TagIds is { Count: > 0 })
            topLevel = ApplyTagFilter(topLevel, request.TagIds, lookup);

        return topLevel;
    }

    private static IEnumerable<Habit> ApplySearchFilter(
        IEnumerable<Habit> topLevel,
        string term,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        bool includeOverdue,
        ILookup<Guid?, Habit> lookup,
        int weekStartDay,
        IReadOnlySet<Guid>? dueDateResolution)
    {
        return topLevel.Where(h => MatchesSearch(
            h,
            term,
            lookup,
            dateFrom,
            dateTo,
            includeOverdue,
            weekStartDay,
            dueDateResolution));
    }

    private static bool MatchesSearch(
        Habit h,
        string term,
        ILookup<Guid?, Habit> lookup,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        bool includeOverdue,
        int weekStartDay,
        IReadOnlySet<Guid>? dueDateResolution)
    {
        if (FuzzyMatcher.FuzzyContains(h.Title, term)) return true;
        if (h.Description != null && FuzzyMatcher.FuzzyContains(h.Description, term)) return true;
        if (h.Tags.Any(t => FuzzyMatcher.FuzzyContains(t.Name, term))) return true;
        return HasDescendantMatchingSearch(
            h.Id,
            lookup,
            term,
            dateFrom,
            dateTo,
            includeOverdue,
            weekStartDay,
            dueDateResolution);
    }

    private static bool HasDescendantMatchingSearch(
        Guid parentId,
        ILookup<Guid?, Habit> lookup,
        string term,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        bool includeOverdue,
        int weekStartDay,
        IReadOnlySet<Guid>? dueDateResolution)
    {
        foreach (var child in lookup[parentId])
        {
            if (!IsChildRelevantForSearch(
                    child,
                    dateFrom,
                    dateTo,
                    includeOverdue,
                    weekStartDay,
                    dueDateResolution))
                continue;
            if (FuzzyMatcher.FuzzyContains(child.Title, term)) return true;
            if (child.Description != null && FuzzyMatcher.FuzzyContains(child.Description, term)) return true;
            if (child.Tags.Any(t => FuzzyMatcher.FuzzyContains(t.Name, term))) return true;
            if (HasDescendantMatchingSearch(
                    child.Id,
                    lookup,
                    term,
                    dateFrom,
                    dateTo,
                    includeOverdue,
                    weekStartDay,
                    dueDateResolution))
                return true;
        }
        return false;
    }

    private static bool IsChildRelevantForSearch(
        Habit child,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        bool includeOverdue,
        int weekStartDay,
        IReadOnlySet<Guid>? dueDateResolution)
    {
        if (child.IsCompleted) return false;
        if (!dateFrom.HasValue || !dateTo.HasValue) return true;

        var scheduledDates = HabitScheduleService.GetScheduledDates(child, dateFrom.Value, dateTo.Value, weekStartDay);
        var isOverdue = DetermineOverdueStatus(
            child,
            dateFrom.Value,
            includeOverdue,
            weekStartDay,
            dueDateResolution);

        return scheduledDates.Count > 0 || isOverdue;
    }

    private static IEnumerable<Habit> ApplyTagFilter(
        IEnumerable<Habit> topLevel,
        IReadOnlyList<Guid> tagIds,
        ILookup<Guid?, Habit> lookup)
    {
        var tagIdSet = tagIds.ToHashSet();
        bool HasMatchingTag(Habit h) => h.Tags.Any(t => tagIdSet.Contains(t.Id));
        bool HasDescendantWithTag(Guid parentId)
        {
            foreach (var child in lookup[parentId])
            {
                if (HasMatchingTag(child)) return true;
                if (HasDescendantWithTag(child.Id)) return true;
            }
            return false;
        }
        return topLevel.Where(h => HasMatchingTag(h) || HasDescendantWithTag(h.Id));
    }

    internal static HabitScheduleItem MapToScheduleItem(
        Habit h,
        List<DateOnly> scheduledDates,
        bool isOverdue,
        ScheduleMapContext ctx)
    {
        var (flexibleTarget, flexibleCompleted) = CalculateFlexibleProgress(h, ctx.ReferenceDate, ctx.WeekStartDay);

        var instances = ctx.DateFrom.HasValue && ctx.DateTo.HasValue && ctx.UserToday.HasValue
            ? HabitScheduleService.GetInstances(
                h,
                ctx.DateFrom.Value,
                ctx.DateTo.Value,
                ctx.UserToday.Value,
                weekStartDay: ctx.WeekStartDay)
            : [];

        var isLoggedInRange = ctx.DateFrom.HasValue
            && ctx.DateTo.HasValue
            && HabitScheduleService.HasCompletedLogInRange(h, ctx.DateFrom.Value, ctx.DateTo.Value);

        return new HabitScheduleItem(
            h.Id, h.Title, h.Description, h.FrequencyUnit, h.FrequencyQuantity,
            h.IsBadHabit, GetResponseCompletion(h, ctx.UserToday), h.IsGeneral, h.IsFlexible,
            h.Days.ToList(), h.Position, h.CreatedAtUtc,
            h.DueDate, h.DueTime, h.DueEndTime, h.EndDate,
            scheduledDates, isOverdue,
            h.ReminderEnabled, h.ReminderTimes, h.ScheduledReminders, h.SlipAlertEnabled,
            h.ChecklistItems, MapTags(h), MapGoals(h),
            MapChildren(h.Id, ctx),
            HasSubHabits(h.Id, ctx.ChildLookup),
            flexibleTarget, flexibleCompleted,
            isLoggedInRange, instances,
            ComputeSearchMatches(h, ctx),
            Emoji: h.Emoji,
            IntervalWeeks: h.IntervalWeeks);
    }

    private static (int? Target, int? Completed) CalculateFlexibleProgress(
        Habit h, DateOnly? referenceDate, int weekStartDay)
    {
        if (!h.IsFlexible || !referenceDate.HasValue)
            return (null, null);

        if (!HabitScheduleService.IsActiveIntervalWeek(h, referenceDate.Value, weekStartDay))
            return (null, null);

        var totalTarget = h.FrequencyQuantity ?? 1;
        var skipped = HabitScheduleService.GetSkippedInWindow(h, referenceDate.Value, h.Logs, weekStartDay);
        var target = Math.Max(0, totalTarget - skipped);
        var completed = HabitScheduleService.GetCompletedInWindow(h, referenceDate.Value, h.Logs, weekStartDay);
        return (target, completed);
    }

    private static List<SearchMatchField>? ComputeSearchMatches(Habit h, ScheduleMapContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Search)) return null;

        var matches = new List<SearchMatchField>();
        if (FuzzyMatcher.FuzzyContains(h.Title, ctx.Search))
            matches.Add(new SearchMatchField("title", null));
        if (h.Description != null && FuzzyMatcher.FuzzyContains(h.Description, ctx.Search))
            matches.Add(new SearchMatchField("description", null));
        matches.AddRange(h.Tags
            .Where(tag => FuzzyMatcher.FuzzyContains(tag.Name, ctx.Search))
            .Select(tag => new SearchMatchField("tag", tag.Name)));
        AddChildSearchMatches(matches, h.Id, ctx);
        return matches.Count > 0 ? matches : null;
    }

    private static void AddChildSearchMatches(
        List<SearchMatchField> matches, Guid parentId, ScheduleMapContext ctx)
    {
        foreach (var child in ctx.ChildLookup[parentId])
        {
            if (child.IsCompleted) continue;
            if (ctx.DateFrom.HasValue && ctx.DateTo.HasValue)
            {
                var childScheduledDates = ctx.GetScheduledDates(child);
                var childIsOverdue = DetermineOverdueStatus(
                    child,
                    ctx.DateFrom.Value,
                    ctx.IncludeOverdue,
                    ctx.WeekStartDay,
                    ctx.DueDateResolution);

                if (childScheduledDates.Count == 0 && !childIsOverdue)
                    continue;
            }

            var childMatches = FuzzyMatcher.FuzzyContains(child.Title, ctx.Search!)
                || child.Description != null && FuzzyMatcher.FuzzyContains(child.Description, ctx.Search!)
                || child.Tags.Any(tag => FuzzyMatcher.FuzzyContains(tag.Name, ctx.Search!));
            if (childMatches)
                matches.Add(new SearchMatchField("child", child.Title));

            AddChildSearchMatches(matches, child.Id, ctx);
        }
    }

    private static bool HasAnyDescendantDue(
        Guid parentId,
        ILookup<Guid?, Habit> lookup,
        DateOnly dateFrom,
        DateOnly dateTo,
        bool includeOverdue,
        int weekStartDay,
        IReadOnlySet<Guid>? dueDateResolution)
    {
        foreach (var child in lookup[parentId])
        {
            var scheduledDates = HabitScheduleService.GetScheduledDates(child, dateFrom, dateTo, weekStartDay);
            var isOverdue = DetermineOverdueStatus(
                child,
                dateFrom,
                includeOverdue,
                weekStartDay,
                dueDateResolution);

            if (scheduledDates.Count > 0 || isOverdue)
                return true;
            if (child.Logs.Any(l => l.Date >= dateFrom && l.Date <= dateTo))
                return true;
            if (HasAnyDescendantDue(
                    child.Id,
                    lookup,
                    dateFrom,
                    dateTo,
                    includeOverdue,
                    weekStartDay,
                    dueDateResolution))
                return true;
        }
        return false;
    }

    private static List<HabitScheduleChildItem> MapChildren(Guid parentId, ScheduleMapContext ctx)
    {
        var children = ctx.ChildLookup[parentId];

        if (!ctx.IncludeAllChildren && ctx.DateFrom.HasValue && ctx.DateTo.HasValue)
        {
            var df = ctx.DateFrom.Value;
            var dt = ctx.DateTo.Value;
            children = children
                .Where(c =>
                {
                    var scheduledDates = ctx.GetScheduledDates(c);
                    var isOverdue = DetermineOverdueStatus(
                        c,
                        df,
                        ctx.IncludeOverdue,
                        ctx.WeekStartDay,
                        ctx.DueDateResolution);

                    return scheduledDates.Count > 0
                        || c.IsCompleted
                        || isOverdue
                        || HasAnyDescendantDue(
                            c.Id,
                            ctx.ChildLookup,
                            df,
                            dt,
                            ctx.IncludeOverdue,
                            ctx.WeekStartDay,
                            ctx.DueDateResolution)
                        || c.Logs.Any(l => l.Date >= df && l.Date <= dt);
                });
        }

        return children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapSingleChild(c, ctx))
            .ToList();
    }

    private static HabitScheduleChildItem MapSingleChild(Habit c, ScheduleMapContext ctx)
    {
        var (ft, fc) = CalculateFlexibleProgress(c, ctx.ReferenceDate, ctx.WeekStartDay);
        var isLoggedInRange = ctx.DateFrom.HasValue && ctx.DateTo.HasValue
            && HabitScheduleService.HasCompletedLogInRange(c, ctx.DateFrom.Value, ctx.DateTo.Value);
        var scheduledDates = ctx.DateFrom.HasValue && ctx.DateTo.HasValue
            ? ctx.GetScheduledDates(c)
            : [];
        var isOverdue = ctx.DateFrom.HasValue
            && DetermineOverdueStatus(
                c,
                ctx.DateFrom.Value,
                ctx.IncludeOverdue,
                ctx.WeekStartDay,
                ctx.DueDateResolution);

        var instances = ctx.DateFrom.HasValue && ctx.DateTo.HasValue && ctx.UserToday.HasValue
            ? HabitScheduleService.GetInstances(
                c,
                ctx.DateFrom.Value,
                ctx.DateTo.Value,
                ctx.UserToday.Value,
                weekStartDay: ctx.WeekStartDay)
            : [];

        return new HabitScheduleChildItem(
            c.Id, c.Title, c.Description,
            c.FrequencyUnit, c.FrequencyQuantity, c.IsBadHabit,
            GetResponseCompletion(c, ctx.UserToday), c.IsGeneral, c.IsFlexible,
            c.Days.ToList(), c.DueDate, c.DueTime, c.DueEndTime, c.EndDate,
            scheduledDates, isOverdue,
            c.Position, c.ChecklistItems, MapTags(c),
            MapChildren(c.Id, ctx),
            HasSubHabits(c.Id, ctx.ChildLookup), ft, fc, isLoggedInRange,
            instances,
            ComputeSearchMatches(c, ctx),
            Emoji: c.Emoji,
            IntervalWeeks: c.IntervalWeeks);
    }

    private static bool GetResponseCompletion(Habit habit, DateOnly? userToday)
    {
        if (!habit.IsGeneral || !userToday.HasValue)
            return habit.IsCompleted;

        return HabitScheduleService.HasCompletedLogInRange(
            habit,
            userToday.Value,
            userToday.Value);
    }

    /// <summary>
    /// Whether a habit has a sub-habit worth navigating to: any incomplete one-time task or any
    /// recurring/flexible child, regardless of today's schedule. A child that is a completed
    /// one-time task (no <see cref="Habit.FrequencyUnit"/>, already done) does not count, so the
    /// "go to sub habits" affordance hides once every child has been finished.
    /// </summary>
    private static bool HasSubHabits(Guid parentId, ILookup<Guid?, Habit> childLookup) =>
        childLookup[parentId].Any(c => !(c.IsCompleted && c.FrequencyUnit is null));

    private static List<HabitTagItem> MapTags(Habit h) =>
        h.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList();

    private static List<LinkedGoalDto> MapGoals(Habit h) =>
        h.Goals.Select(g => new LinkedGoalDto(g.Id, g.Title)).ToList();
}
