using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

/// <summary>Groups the repositories the streak service touches to keep its constructor small.</summary>
public record UserStreakRepositories(
    IGenericRepository<User> Users,
    IGenericRepository<Habit> Habits,
    IGenericRepository<HabitLog> HabitLogs,
    IGenericRepository<StreakFreeze> StreakFreezes);

public class UserStreakService(
    UserStreakRepositories repos,
    IUserDateService userDateService,
    IFriendFeedEventEmitter friendFeedEventEmitter) : IUserStreakService
{
    public async Task<UserStreakState?> RecalculateAsync(
        Guid userId,
        bool awardFreezeIfEligible = true,
        CancellationToken cancellationToken = default)
    {
        var user = await repos.Users.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);
        if (user is null)
            return null;

        var previousStreak = user.CurrentStreak;
        var state = await CalculateStateAsync(userId, user, cancellationToken);
        user.SetStreakState(state.CurrentStreak, state.LongestStreak, state.LastActiveDate);
        if (awardFreezeIfEligible)
        {
            user.AwardStreakFreezeIfEligible(
                AppConstants.MaxStreakFreezesAccumulated,
                AppConstants.StreakDaysPerFreeze);
        }
        await friendFeedEventEmitter.EmitStreakMilestonesAsync(user, previousStreak, cancellationToken);
        return state;
    }

    public async Task<UserStreakState?> CalculateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var users = await repos.Users.FindAsync(
            user => user.Id == userId,
            cancellationToken);
        var user = users.SingleOrDefault();
        return user is null
            ? null
            : await CalculateStateAsync(userId, user, cancellationToken);
    }

    public async Task<StreakRepairEvaluation?> EvaluateRepairAsync(
        Guid userId,
        DateOnly userToday,
        DateOnly missedDate,
        CancellationToken cancellationToken = default)
    {
        var user = await repos.Users.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);
        if (user is null)
            return null;

        var lookbackStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);
        var (completionDateSet, freezeDateSet, eligibleHabits) =
            await LoadStreakDataAsync(userId, lookbackStart, cancellationToken);

        return EvaluateRepair(
            user,
            userToday,
            missedDate,
            eligibleHabits,
            completionDateSet,
            freezeDateSet);
    }

    public async Task<UserStreakState?> EvaluateGapRepairAsync(
        Guid userId,
        DateOnly userToday,
        IReadOnlyCollection<DateOnly> dates,
        CancellationToken cancellationToken = default)
    {
        if (StreakFreeze.CreateGap(userId, dates, userToday).IsFailure)
            return null;

        var user = await repos.Users.FindOneTrackedAsync(
            candidate => candidate.Id == userId,
            cancellationToken: cancellationToken);
        if (user is null)
            return null;

        // ONE window, and it is the streak engine's own. Eligibility must be decided over exactly the
        // history CalculateStateAsync computes from, or a repair can be accepted on evidence the engine
        // cannot see: the response, and the next RecalculateAsync, would recompute a zero streak and
        // persist it AFTER the freeze was spent. A yearly predecessor sits 366 days back, outside this
        // window, so a yearly gap stays unrepairable rather than repairable-then-silently-undone.
        var lookbackStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);
        var gapStart = dates.Min();
        if (gapStart <= lookbackStart)
            return null;

        var (completions, freezes, eligibleHabits) =
            await LoadStreakDataAsync(userId, lookbackStart, cancellationToken);
        var contributingHabits = GetContributingHabits(eligibleHabits);
        if (!contributingHabits.Any(habit => habit.FrequencyUnit is not null))
            return null;

        var timeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, userId: user.Id);
        var expectedDates = HabitScheduleService.GetUnionScheduledDatesForStreak(
            contributingHabits, lookbackStart, userToday, timeZone, user.WeekStartDay);
        if (dates.Any(date => !expectedDates.Contains(date) || completions.Contains(date) || freezes.Contains(date)))
            return null;

        // Streak continuity runs over SCHEDULED occurrences, never calendar days: a weekly habit's
        // streak survives the six unscheduled days between two occurrences. Reading the calendar day
        // before the gap made every weekly and every-N-day gap unrepairable, because that day is
        // usually not scheduled and so carries neither a completion nor a freeze.
        var scheduled = expectedDates.Order().ToArray();
        var gapStartIndex = Array.IndexOf(scheduled, gapStart);
        if (gapStartIndex < 0)
            return null;

        // The selection must be an unbroken run of scheduled occurrences, so a caller cannot omit a
        // missed occurrence inside the gap and claim the streak carried across it.
        var orderedDates = dates.Order().ToArray();
        if (gapStartIndex + orderedDates.Length > scheduled.Length
            || orderedDates.Where((date, index) => date != scheduled[gapStartIndex + index]).Any())
        {
            return null;
        }

        // Index 0 means the gap opens the window with no predecessor inside it, so there is no evidence
        // the streak was alive going in.
        if (gapStartIndex == 0)
            return null;
        var precedingDate = scheduled[gapStartIndex - 1];
        if (!completions.Contains(precedingDate) && !freezes.Contains(precedingDate))
            return null;

        foreach (var month in dates.GroupBy(date => (date.Year, date.Month)))
        {
            var used = freezes.Count(date => date.Year == month.Key.Year && date.Month == month.Key.Month);
            if (used + month.Count() > AppConstants.MaxStreakFreezesPerMonth)
                return null;
        }

        var (currentStreak, _) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completions, freezes, lookbackStart, userToday);
        var repairedDates = new HashSet<DateOnly>(freezes);
        repairedDates.UnionWith(dates);
        var (repairedStreak, lastActiveDate) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completions, repairedDates, lookbackStart, userToday);
        if (repairedStreak <= currentStreak)
            return null;

        // The predecessor travels WITH the state. The handler restores the award cursor against it, and
        // deriving it there as `gapStart - 1` was the same calendar-versus-schedule mistake one layer up.
        // The streak AS OF the predecessor, which bounds the fallback award cursor. Without it the
        // domain rounded the full repaired streak down and marked milestones crossed AFTER the gap as
        // already awarded, so a row with no saved snapshot spent a freeze and never received the one it
        // had just earned.
        var (preGapStreak, _) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completions, freezes, lookbackStart, precedingDate);
        return new UserStreakState(repairedStreak,
            Math.Max(user.LongestStreak, ComputeLongestStreak(expectedDates, completions, repairedDates)),
            lastActiveDate, precedingDate, preGapStreak);
    }

    internal static StreakRepairEvaluation EvaluateRepair(
        User user,
        DateOnly userToday,
        DateOnly missedDate,
        IReadOnlyCollection<Habit> eligibleHabits,
        HashSet<DateOnly> completionDateSet,
        HashSet<DateOnly> freezeDateSet)
    {
        if (missedDate.DayNumber != userToday.DayNumber - 1
            || user.StreakFreezesAccumulated <= 0)
        {
            return StreakRepairEvaluation.Unavailable(missedDate);
        }

        var contributingHabits = GetContributingHabits(eligibleHabits);
        if (!contributingHabits.Any(habit => habit.FrequencyUnit is not null))
            return StreakRepairEvaluation.Unavailable(missedDate);

        var lookbackStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, userId: user.Id);
        var expectedDates = HabitScheduleService.GetUnionScheduledDatesForStreak(
            contributingHabits,
            lookbackStart,
            userToday,
            userTimeZone,
            user.WeekStartDay);

        if (!expectedDates.Contains(missedDate)
            || completionDateSet.Contains(missedDate)
            || freezeDateSet.Contains(missedDate))
        {
            return StreakRepairEvaluation.Unavailable(missedDate);
        }

        var monthStart = new DateOnly(missedDate.Year, missedDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var freezesThisMonth = freezeDateSet.Count(date => date >= monthStart && date < monthEnd);
        if (freezesThisMonth >= AppConstants.MaxStreakFreezesPerMonth)
            return StreakRepairEvaluation.Unavailable(missedDate);

        var (currentStreak, _) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completionDateSet, freezeDateSet, lookbackStart, userToday);
        var repairedFreezeDates = new HashSet<DateOnly>(freezeDateSet) { missedDate };
        var (repairedStreak, repairedLastActiveDate) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates, completionDateSet, repairedFreezeDates, lookbackStart, userToday);
        if (repairedStreak <= currentStreak)
            return StreakRepairEvaluation.Unavailable(missedDate);

        var repairedLongestStreak = ComputeLongestStreak(
            expectedDates, completionDateSet, repairedFreezeDates);
        repairedLongestStreak = Math.Max(
            user.LongestStreak,
            Math.Max(repairedLongestStreak, repairedStreak));

        return StreakRepairEvaluation.Available(
            missedDate,
            new UserStreakState(repairedStreak, repairedLongestStreak, repairedLastActiveDate));
    }

    private async Task<(HashSet<DateOnly> CompletionDates, HashSet<DateOnly> FreezeDates, List<Habit> EligibleHabits)>
        LoadStreakDataAsync(Guid userId, DateOnly lookbackStart, CancellationToken cancellationToken)
    {
        var allHabits = await repos.Habits.FindAsync(h => h.UserId == userId, cancellationToken);
        var streakEligibleHabitIds = allHabits
            .Where(h => !h.IsDeleted && !h.IsBadHabit)
            .Select(h => h.Id)
            .ToHashSet();

        var completionDateSet = streakEligibleHabitIds.Count == 0
            ? new HashSet<DateOnly>()
            : (await repos.HabitLogs.FindAsync(
                l => streakEligibleHabitIds.Contains(l.HabitId) && l.Value > 0 && l.Date >= lookbackStart,
                cancellationToken))
                .Select(log => log.Date)
                .ToHashSet();

        var freezeDateSet = (await repos.StreakFreezes.FindAsync(
            sf => sf.UserId == userId && sf.UsedOnDate >= lookbackStart,
            cancellationToken))
            .Select(freeze => freeze.UsedOnDate)
            .ToHashSet();

        var eligibleHabits = allHabits
            .Where(habit => !habit.IsDeleted && !habit.IsBadHabit)
            .ToList();

        return (completionDateSet, freezeDateSet, eligibleHabits);
    }

    private async Task<UserStreakState> CalculateStateAsync(
        Guid userId,
        User user,
        CancellationToken cancellationToken)
    {
        var userToday = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var lookbackStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);
        var (completionDateSet, freezeDateSet, eligibleHabits) =
            await LoadStreakDataAsync(userId, lookbackStart, cancellationToken);
        var contributingHabits = GetContributingHabits(eligibleHabits);

        if (!contributingHabits.Any(habit => habit.FrequencyUnit is not null))
            return CalendarFallback(completionDateSet, freezeDateSet);

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, userId: user.Id);
        var expectedDates = HabitScheduleService.GetUnionScheduledDatesForStreak(
            contributingHabits,
            lookbackStart,
            userToday,
            userTimeZone,
            user.WeekStartDay);
        var (currentStreak, lastActiveDate) = HabitScheduleService.ComputeStreakAsOf(
            expectedDates,
            completionDateSet,
            freezeDateSet,
            lookbackStart,
            userToday);
        var longestStreak = Math.Max(
            currentStreak,
            ComputeLongestStreak(expectedDates, completionDateSet, freezeDateSet));

        return new UserStreakState(currentStreak, longestStreak, lastActiveDate);
    }

    private static List<Habit> GetContributingHabits(IReadOnlyCollection<Habit> eligibleHabits) =>
        eligibleHabits
            .Where(habit => !habit.IsGeneral && !habit.IsFlexible)
            .Where(habit => !(habit.FrequencyUnit is null && habit.IsCompleted))
            .ToList();

    private static int ComputeLongestStreak(
        HashSet<DateOnly> expectedDates,
        HashSet<DateOnly> completionDateSet,
        HashSet<DateOnly> freezeDateSet)
    {
        if (expectedDates.Count == 0) return 0;

        var ordered = expectedDates.OrderBy(d => d).ToList();
        var longest = 0;
        var run = 0;
        foreach (var date in ordered)
        {
            if (completionDateSet.Contains(date))
            {
                run++;
                if (run > longest) longest = run;
            }
            else if (!freezeDateSet.Contains(date))
            {
                run = 0;
            }
        }
        return longest;
    }

    private static UserStreakState CalendarFallback(
        HashSet<DateOnly> completionDateSet,
        HashSet<DateOnly> freezeDateSet)
    {
        var orderedDates = completionDateSet
            .Concat(freezeDateSet)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        var currentStreak = 0;
        var longestStreak = 0;
        DateOnly? lastActiveDate = null;

        foreach (var date in orderedDates)
        {
            if (completionDateSet.Contains(date))
            {
                currentStreak = lastActiveDate.HasValue
                    && lastActiveDate.Value.DayNumber == date.DayNumber - 1
                    ? currentStreak + 1
                    : 1;
                lastActiveDate = date;
                longestStreak = Math.Max(longestStreak, currentStreak);
                continue;
            }

            if (!freezeDateSet.Contains(date))
                continue;

            if (!lastActiveDate.HasValue
                || (date.DayNumber - lastActiveDate.Value.DayNumber) > 2)
            {
                currentStreak = 0;
            }
            lastActiveDate = date;
        }

        return new UserStreakState(currentStreak, longestStreak, lastActiveDate);
    }
}
