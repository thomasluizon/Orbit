using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Services;

/// <summary>
/// Computes the structured, deterministic metrics shown on the retrospective dashboard
/// (completion rates, streak echo, active/period days, per-weekday consistency, and the
/// top / needs-attention habit lists) from the habits and in-range logs already loaded by
/// the query handler. The AI narrative is produced separately by <see cref="IRetrospectiveService"/>.
/// </summary>
public static class RetrospectiveMetricsCalculator
{
    private const int MaxHabitStats = 3;

    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    ];

    public static RetrospectiveMetrics Compute(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        int currentStreak,
        int bestStreak,
        int weekStartDay = 1)
    {
        return Compute(
            habits,
            dateFrom,
            dateTo,
            currentStreak,
            bestStreak,
            habit => HabitScheduleService.GetScheduledDates(habit, dateFrom, dateTo, weekStartDay));
    }

    /// <summary>
    /// Computes closed-period metrics from the schedule that applied during the resolved window,
    /// independent of the habit's later forward-advanced due date.
    /// </summary>
    public static RetrospectiveMetrics ComputeHistorical(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        int currentStreak,
        int bestStreak,
        TimeZoneInfo userTimeZone,
        int weekStartDay = 1)
    {
        return Compute(
            habits,
            dateFrom,
            dateTo,
            currentStreak,
            bestStreak,
            habit => HabitScheduleService.GetHistoricalScheduledDates(
                habit,
                dateFrom,
                dateTo,
                userTimeZone,
                weekStartDay));
    }

    private static RetrospectiveMetrics Compute(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        int currentStreak,
        int bestStreak,
        Func<Habit, List<DateOnly>> resolveScheduledDates)
    {
        var trackedHabits = habits.Where(h => h.ParentHabitId is null).ToList();

        var totalCompletions = 0;
        var totalMet = 0;
        var totalScheduled = 0;
        var badHabitSlips = 0;
        var stats = new List<RetrospectiveHabitStat>();
        var weekdayScheduled = new int[7];
        var weekdayCompleted = new int[7];

        foreach (var habit in trackedHabits)
        {
            var scheduledDates = resolveScheduledDates(habit);
            var completedCount = habit.Logs.Count(l => !l.IsDeleted
                && l.Date >= dateFrom
                && l.Date <= dateTo
                && l.Value > 0);

            if (scheduledDates.Count == 0 && completedCount == 0)
                continue;

            if (habit.IsBadHabit)
            {
                badHabitSlips += completedCount;
                continue;
            }

            totalScheduled += scheduledDates.Count;
            totalCompletions += completedCount;
            totalMet += Math.Min(completedCount, scheduledDates.Count);

            AccumulateWeekdayConsistency(habit, scheduledDates, weekdayScheduled, weekdayCompleted);
            stats.Add(BuildHabitStat(habit, scheduledDates.Count, completedCount));
        }

        var completionRate = Percent(totalMet, totalScheduled);
        var activeDays = CountActiveDays(habits, dateFrom, dateTo);
        var periodDays = dateTo.DayNumber - dateFrom.DayNumber + 1;
        var weeklyConsistency = BuildWeeklyConsistency(weekdayScheduled, weekdayCompleted);

        var recurringStats = stats.Where(s => !s.IsOneTime);

        var topHabits = recurringStats
            .OrderByDescending(s => s.CompletionRate)
            .ThenByDescending(s => s.CompletedCount)
            .Take(MaxHabitStats)
            .ToList();

        var needsAttention = recurringStats
            .Where(s => s.CompletionRate < 100)
            .OrderBy(s => s.CompletionRate)
            .ThenByDescending(s => s.ScheduledCount)
            .Take(MaxHabitStats)
            .ToList();

        return new RetrospectiveMetrics(
            completionRate,
            totalCompletions,
            totalScheduled,
            activeDays,
            periodDays,
            currentStreak,
            bestStreak,
            badHabitSlips,
            weeklyConsistency,
            topHabits,
            needsAttention);
    }

    private static void AccumulateWeekdayConsistency(
        Habit habit, List<DateOnly> scheduledDates, int[] weekdayScheduled, int[] weekdayCompleted)
    {
        var completedDates = habit.Logs
            .Where(l => !l.IsDeleted && l.Value > 0)
            .Select(l => l.Date)
            .ToHashSet();

        foreach (var date in scheduledDates)
        {
            var index = WeekdayIndex(date.DayOfWeek);
            weekdayScheduled[index]++;
            if (completedDates.Contains(date))
                weekdayCompleted[index]++;
        }
    }

    private static RetrospectiveHabitStat BuildHabitStat(Habit habit, int scheduledCount, int completedCount) =>
        new(
            habit.Title,
            habit.Emoji,
            Math.Min(100, Percent(completedCount, scheduledCount)),
            completedCount,
            scheduledCount,
            habit.FrequencyUnit is null);

    private static int[] BuildWeeklyConsistency(int[] weekdayScheduled, int[] weekdayCompleted)
    {
        var consistency = new int[7];
        for (var i = 0; i < 7; i++)
            consistency[i] = Math.Min(100, Percent(weekdayCompleted[i], weekdayScheduled[i]));
        return consistency;
    }

    private static int CountActiveDays(List<Habit> habits, DateOnly dateFrom, DateOnly dateTo)
    {
        var activeDates = new HashSet<DateOnly>();
        foreach (var habit in habits)
        {
            foreach (var log in habit.Logs)
            {
                if (!log.IsDeleted && log.Value > 0 && log.Date >= dateFrom && log.Date <= dateTo)
                    activeDates.Add(log.Date);
            }
        }
        return activeDates.Count;
    }

    private static int WeekdayIndex(DayOfWeek day) => Array.IndexOf(WeekOrder, day);

    private static int Percent(int numerator, int denominator) =>
        denominator > 0 ? (int)Math.Round(100.0 * numerator / denominator) : 0;
}
