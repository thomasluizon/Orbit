using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class StreakGapRepairTests
{
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _logs = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<StreakFreeze> _freezes = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _dateService = Substitute.For<IUserDateService>();
    private readonly User _user = User.Create("Test", "test@example.com").Value;
    private readonly List<StreakFreeze> _persistedFreezes = [];
    private readonly UserStreakService _service;
    private readonly DateOnly _today = new(2026, 9, 6);
    private Habit _habit;

    public StreakGapRepairTests()
    {
        _service = new(new(_users, _habits, _logs, _freezes), _dateService,
            Substitute.For<IFriendFeedEventEmitter>());
        _users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(_user);
        _users.FindAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns([_user]);
        _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_today);
        _freezes.FindAsync(Arg.Any<Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => _persistedFreezes.Where(call.Arg<Expression<Func<StreakFreeze, bool>>>().Compile()).ToList());
        _habit = SetHistory(_today, 2);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 1)]
    public async Task RepairedRun_LoggedCompletionsAwardOnlyNewMilestones(int completions, int expectedBank)
    {
        _habit = SetHistory(_today, 2, precedingCompletions: 14);
        _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_today.AddDays(-2));
        await _service.RecalculateAsync(_user.Id);
        _user.CurrentStreak.Should().Be(14);
        _user.StreakFreezesAccumulated.Should().Be(2);
        _user.LastFreezeAwardStreak.Should().Be(14);

        _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_today);
        await _service.RecalculateAsync(_user.Id, awardFreezeIfEligible: false);
        await _service.RecalculateAsync(_user.Id, awardFreezeIfEligible: false);
        _user.CurrentStreak.Should().Be(0);
        _user.LastFreezeAwardStreak.Should().Be(0);

        var (handler, _) = BuildRepairHandler();

        var result = await handler.Handle(new(_user.Id, [_today.AddDays(-2), _today.AddDays(-1)]), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(14);
        result.Value.StreakFreezesAccumulated.Should().Be(0);

        for (var offset = 0; offset < completions; offset++)
        {
            var date = _today.AddDays(offset);
            _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(date);
            _habit.Log(date, advanceDueDate: false);
            await _service.RecalculateAsync(_user.Id);
            if (offset < 6)
                _user.StreakFreezesAccumulated.Should().Be(0);
        }

        _user.CurrentStreak.Should().Be(14 + completions);
        _user.StreakFreezesAccumulated.Should().Be(expectedBank);
        _user.LastFreezeAwardStreak.Should().Be(expectedBank == 0 ? 14 : 21);
    }

    [Fact]
    public async Task SavedCursor_SurvivesUserReloadBeforeRepair()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"StreakGapCursor_{Guid.NewGuid()}").Options;
        await using (var context = new OrbitDbContext(options))
        {
            _user.SetStreakState(14, 14, _today.AddDays(-3));
            _user.AwardStreakFreezeIfEligible();
            _user.SetStreakState(0, 14, null);
            context.Users.Add(_user);
            await context.SaveChangesAsync();
        }

        await using var reloaded = new OrbitDbContext(options);
        var user = await reloaded.Users.SingleAsync(candidate => candidate.Id == _user.Id);
        user.ConsumeStreakFreezes(2).IsSuccess.Should().BeTrue();
        user.RestoreStreakAfterGapRepair(14, 14, _today.AddDays(-1), _today.AddDays(-3), preGapStreak: 14);
        user.UpdateStreak(_today);

        user.AwardStreakFreezeIfEligible().Should().BeFalse();
        user.StreakFreezesAccumulated.Should().Be(0);
        user.LastFreezeAwardStreak.Should().Be(14);
    }

    [Fact]
    public async Task LegacyPersistedUserWithoutSavedCursor_DoesNotReawardRestoredMilestone()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"LegacyStreakGapCursor_{Guid.NewGuid()}").Options;
        await using (var context = new OrbitDbContext(options))
        {
            _user.SetStreakState(14, 14, _today.AddDays(-3));
            _user.AwardStreakFreezeIfEligible();
            _user.SetStreakState(0, 14, null);
            context.Users.Add(_user);
            context.Entry(_user).Property(user => user.PreGapFreezeAwardStreak).CurrentValue = null;
            context.Entry(_user).Property(user => user.PreGapLastActiveDate).CurrentValue = null;
            await context.SaveChangesAsync();
        }

        await using var reloaded = new OrbitDbContext(options);
        var user = await reloaded.Users.SingleAsync(candidate => candidate.Id == _user.Id);
        user.PreGapFreezeAwardStreak.Should().BeNull();
        user.PreGapLastActiveDate.Should().BeNull();
        user.LastFreezeAwardStreak.Should().Be(0);
        user.ConsumeStreakFreezes(2).IsSuccess.Should().BeTrue();
        user.RestoreStreakAfterGapRepair(14, 14, _today.AddDays(-1), _today.AddDays(-3), preGapStreak: 14);
        user.UpdateStreak(_today);

        user.AwardStreakFreezeIfEligible().Should().BeFalse();
        user.StreakFreezesAccumulated.Should().Be(0);
        user.LastFreezeAwardStreak.Should().Be(14);
    }

    [Theory]
    [InlineData(2, true, 0)]
    [InlineData(1, false, 1)]
    public async Task TwoDayGap_RealEvaluationAndHandler_SpendEntireCostOrNothing(int bank, bool succeeds, int remaining)
    {
        _user.SetStreakState(bank * 7, bank * 7, _today.AddDays(-3));
        _user.AwardStreakFreezeIfEligible();
        _user.SetStreakState(0, bank * 7, null);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        /**
         * The repair runs inside HabitCeilingLock, the same per-user advisory lock every habit writer
         * holds, so eligibility and the bank spend cannot be split by a concurrent schedule edit. A
         * substituted unit of work returns null from the transaction wrapper unless the operation is
         * actually invoked, so these end-to-end cases run it exactly as the real one does.
         */
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task<Result<int>>>>(0);
                return operation(call.ArgAt<CancellationToken>(1));
            });
        var sender = Substitute.For<ISender>();
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        sender.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new StreakInfoResponse(3, bank * 7, _today.AddDays(-1), 2, 1, 3,
                false, [_today.AddDays(-2), _today.AddDays(-1)], remaining, 3, 4, remaining, true, false, null, 0)));
        var staged = new List<StreakFreeze>();
        _freezes.AddAsync(Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>())
            .Returns(call => { staged.Add(call.Arg<StreakFreeze>()); return Task.CompletedTask; });
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            staged.Count.Should().Be(2);
            _persistedFreezes.AddRange(staged);
            return Task.FromResult(3);
        });
        var handler = new RepairStreakGapCommandHandler(_users, _freezes, _dateService, _service,
            flags, unitOfWork, sender, NullLogger<RepairStreakGapCommandHandler>.Instance);

        var result = await handler.Handle(new(_user.Id, [_today.AddDays(-2), _today.AddDays(-1)]), CancellationToken.None);

        result.IsSuccess.Should().Be(succeeds);
        _user.StreakFreezesAccumulated.Should().Be(remaining);
        if (succeeds)
        {
            _user.CurrentStreak.Should().Be(3);
            var recalculated = await _service.CalculateAsync(_user.Id);
            recalculated!.CurrentStreak.Should().Be(3);
            recalculated.LastActiveDate.Should().Be(_today.AddDays(-1));
            await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        }
        else
        {
            result.ErrorCode.Should().Be(DomainErrors.InsufficientStreakFreezes.Code);
            _persistedFreezes.Should().BeEmpty();
            staged.Should().BeEmpty();
            await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(-3, -1)]
    [InlineData(-1, -1)]
    [InlineData(-3, -2)]
    [InlineData(-1, 0)]
    public async Task InvalidSelection_IsUnavailable(int first, int last)
    {
        var result = await _service.EvaluateGapRepairAsync(_user.Id, _today,
            [_today.AddDays(first), _today.AddDays(last)]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task OnlySuffixOfGap_IsUnavailable()
    {
        var result = await _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-1)]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SelectionContainsCompletedDay_IsUnavailable()
    {
        _habit.Log(_today.AddDays(-2), advanceDueDate: false);

        (await Evaluate()).Should().BeNull();
    }

    [Fact]
    public async Task SelectionContainsFrozenDay_IsUnavailable()
    {
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, _today.AddDays(-1)));

        (await Evaluate()).Should().BeNull();
    }

    [Fact]
    public async Task SelectionContainsUnscheduledDay_IsUnavailable()
    {
        _habit = SetHistory(_today, 2, frequencyQuantity: 2);

        (await Evaluate()).Should().BeNull();
    }

    /// <summary>
    /// The sparse-schedule case the calendar-adjacency reading could never repair. A weekly habit's
    /// prior contributing occurrence is seven days back, so requiring activity on the previous CALENDAR
    /// day made every weekly gap unavailable: that day is not scheduled and carries nothing.
    /// </summary>
    [Fact]
    public async Task WeeklyGapWhosePriorOccurrenceIsAWeekBack_IsRepairable()
    {
        _habit = SetWeeklyHistory();

        var state = await _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-1)]);

        state.Should().NotBeNull();
        state!.CurrentStreak.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DailyTwoDayGap_IsReturned()
    {
        var streak = await GetStreakInfo();

        streak.RepairableGapDates.Should().Equal(_today.AddDays(-2), _today.AddDays(-1));
    }

    [Fact]
    public async Task WeeklyTwoOccurrenceGap_IsReturnedAndAcceptedWhenEchoed()
    {
        _habit = SetWeeklyHistory(gapOccurrences: 2);
        SetUserProperty(nameof(User.StreakFreezesAccumulated), 2);
        var streak = await GetStreakInfo();
        var dates = streak.RepairableGapDates!;

        dates.Should().Equal(_today.AddDays(-8), _today.AddDays(-1));

        var (handler, _) = BuildRepairHandler();
        var repaired = await handler.Handle(new RepairStreakGapCommand(_user.Id, dates), CancellationToken.None);

        repaired.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GapWithMissedPredecessor_IsNotReturnedPartially()
    {
        _habit = SetHistory(_today, gapLength: 4);

        var streak = await GetStreakInfo();

        streak.RepairableGapDates.Should().BeEmpty();
    }

    [Fact]
    public async Task GapOpeningLookbackWindow_IsNotReturned()
    {
        _habit = SetHistory(_today, AppConstants.MaxStreakLookbackDays, precedingCompletions: 0);

        var streak = await GetStreakInfo();

        streak.RepairableGapDates.Should().BeEmpty();
    }

    /// <summary>Schedule-awareness widens which gaps are contiguous; it never waives the preceding
    /// occurrence. With the prior week's occurrence missed too, one selected date is not a whole gap.</summary>
    [Fact]
    public async Task WeeklyGapWhosePriorOccurrenceWasAlsoMissed_IsUnavailable()
    {
        _habit = SetWeeklyHistory(priorOccurrences: 3, skipMostRecentCompletion: true);

        (await _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-1)])).Should().BeNull();
    }

    /// <summary>
    /// A yearly gap is REFUSED, deliberately, and this pins why. Its predecessor sits 366 days back,
    /// outside the window the streak engine itself computes from. Accepting it on widened history was
    /// worse than refusing it: the response and the next RecalculateAsync both run on the ordinary
    /// window, would see yesterday's freeze without the preceding completion, and would persist a zero
    /// streak AFTER the freeze had been spent. Eligibility is decided over exactly the history that can
    /// represent the result. Yearly support needs the engine's own window widened, which is its own
    /// change.
    /// </summary>
    [Fact]
    public async Task YearlyGapWhosePredecessorSitsBeyondTheStreakWindow_IsRefused()
    {
        _habit = SetYearlyHistory();

        (await _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-1)])).Should().BeNull();
    }

    /// <summary>
    /// A habit older than the streak window, so its effectiveFrom sits at that boundary.
    /// A gap ending yesterday must still repair: the habit predating the window never truncates the
    /// recent end, because the request stays inside the generator.s range cap.
    ///
    /// </summary>
    [Fact]
    public async Task DailyHabitOlderThanTheWidenedStart_StillRepairsAGapEndingYesterday()
    {
        _habit = SetLongRunningDailyHistory();

        var state = await _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-1)]);

        state.Should().NotBeNull();
        state!.PrecedingScheduledDate.Should().Be(_today.AddDays(-2));
    }

    /// <summary>The predecessor must reach the caller, because the award cursor is restored against it
    /// and a sparse gap's predecessor is never the previous calendar day.</summary>
    [Fact]
    public async Task RepairCarriesTheScheduledPredecessorForCursorRestoration()
    {
        _habit = SetWeeklyHistory();

        var state = await _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-1)]);

        state!.PrecedingScheduledDate.Should().Be(_today.AddDays(-8));
        state.PrecedingScheduledDate.Should().NotBe(_today.AddDays(-2));
    }

    /// <summary>
    /// A second decrease must not replace the snapshot taken at the FIRST one. The first break saves
    /// the cursor identifying the still-repairable gap; a later decrease happens while the streak is
    /// already broken, and overwriting swapped (7, the day before the gap) for (0, today), after which
    /// repair fell through to the derived cursor and skipped a milestone that was never granted.
    ///
    /// Asserted on the entity rather than through RecalculateAsync deliberately: this fixture cannot
    /// produce the increase-then-decrease the bug needs, because Unlog leaves the recalculated streak
    /// at 1 rather than returning it to 0, so a service-level version of this test passes with the fix
    /// reverted and proves nothing. The handler-level regression below covers the wiring.
    /// </summary>
    [Fact]
    public void ASecondDecrease_KeepsTheSnapshotFromTheFirstBreak()
    {
        _user.SetStreakState(13, 13, _today.AddDays(-2));
        _user.AwardStreakFreezeIfEligible();
        _user.LastFreezeAwardStreak.Should().Be(7);

        _user.SetStreakState(0, 13, null);
        _user.SetStreakState(1, 13, _today);
        _user.SetStreakState(0, 13, null);

        _user.PreGapFreezeAwardStreak.Should().Be(7);
        _user.PreGapLastActiveDate.Should().Be(_today.AddDays(-2));
    }

    /// <summary>
    /// The pre-migration row, driven through the handler so the fix is proved where it is wired rather
    /// than only on the entity. Six completions before the gap and one after: repair reaches seven,
    /// spends the banked freeze, and the newly crossed milestone must still be awardable. Rounding the
    /// FULL repaired streak down recorded it as already granted, so its freeze was never issued.
    /// </summary>
    [Fact]
    public async Task LegacyRowCrossingAMilestoneAfterTheGap_StillEarnsItsFreeze()
    {
        _habit = SetHistory(_today, 1, precedingCompletions: 6);
        _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_today.AddDays(-1));
        await _service.RecalculateAsync(_user.Id, awardFreezeIfEligible: false);

        _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_today);
        await _service.RecalculateAsync(_user.Id, awardFreezeIfEligible: false);
        /**
         * The deployment boundary: rows written before the cursor migration carry no snapshot at all,
         * and the freeze being spent was banked by an earlier run this fixture does not replay.
         */
        SetUserProperty(nameof(User.PreGapFreezeAwardStreak), null);
        SetUserProperty(nameof(User.PreGapLastActiveDate), null);
        SetUserProperty(nameof(User.StreakFreezesAccumulated), 1);

        /**
         * The completion AFTER the gap. It is what carries the repaired run across seven days, and so
         * what the old fallback quietly recorded as an already-granted milestone.
         */
        _habit.Log(_today, advanceDueDate: false);

        var (handler, _) = BuildRepairHandler();
        var result = await handler.Handle(new(_user.Id, [_today.AddDays(-1)]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        /**
         * GRANTED by the repair itself, not merely left eligible. The response comes from
         * GetStreakInfoQuery, which calls CalculateAsync rather than RecalculateAsync, so an award only
         * made eligible here would sit pending and be lost the next time the streak reset.
         */
        _user.StreakFreezesAccumulated.Should().Be(1);
        _user.LastFreezeAwardStreak.Should().Be(7);
    }

    /// <summary>
    /// The saved snapshot outlives later recalculations by design, so it can describe a LONGER run than
    /// the one being repaired. Unlogging an older pre-gap completion shortens the run while a cursor of
    /// 14 still matches on date; taken alone that suppressed the awards the shorter run has re-earned.
    /// </summary>
    [Fact]
    public void SavedCursorLongerThanTheCurrentRun_IsCappedByWhatWasActuallyEarned()
    {
        _user.SetStreakState(14, 14, _today.AddDays(-2));
        _user.AwardStreakFreezeIfEligible();
        _user.LastFreezeAwardStreak.Should().Be(14);
        _user.SetStreakState(0, 14, null);

        /**
         * The same predecessor date the snapshot holds, but the run going into it is now only 7 long.
         */
        _user.RestoreStreakAfterGapRepair(7, 14, _today, _today.AddDays(-2), preGapStreak: 7);

        _user.LastFreezeAwardStreak.Should().Be(7);
    }

    [Fact]
    public async Task GapExceedsMonthlyAllowance_IsUnavailable()
    {
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 9, 1)));
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 9, 2)));

        (await Evaluate()).Should().BeNull();
    }

    [Fact]
    public async Task GapCrossesMonthBoundary_ChecksEachMonthsAllowance()
    {
        var today = new DateOnly(2026, 9, 2);
        SetHistory(today, 2);
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 8, 20)));
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 8, 21)));

        var result = await _service.EvaluateGapRepairAsync(_user.Id, today, [today.AddDays(-2), today.AddDays(-1)]);

        result!.CurrentStreak.Should().Be(3);
        _persistedFreezes.Add(StreakFreeze.Create(_user.Id, new DateOnly(2026, 8, 22)));
        (await _service.EvaluateGapRepairAsync(_user.Id, today, [today.AddDays(-2), today.AddDays(-1)])).Should().BeNull();
    }

    [Fact]
    public async Task CompletionToday_RestoresPriorStreakAndCountsToday()
    {
        _habit.Log(_today, advanceDueDate: false);

        var result = await Evaluate();

        result!.CurrentStreak.Should().Be(4);
        result.LastActiveDate.Should().Be(_today);
    }

    [Fact]
    public async Task CompletionToday_DoesNotHideEarlierGap()
    {
        _habit.Log(_today, advanceDueDate: false);

        var streak = await GetStreakInfo();

        streak.RepairableGapDates.Should().Equal(_today.AddDays(-2), _today.AddDays(-1));
    }

    [Fact]
    public async Task NoPriorStreak_IsUnavailable()
    {
        _logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>()).Returns([]);

        (await Evaluate()).Should().BeNull();
    }

    [Theory]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("America/Los_Angeles")]
    public async Task GapUsesPassedLocalTodayAndUserTimezone(string timezone)
    {
        _user.SetTimeZone(timezone);

        var result = await Evaluate();

        result!.LastActiveDate.Should().Be(_today.AddDays(-1));
    }

    private Task<UserStreakState?> Evaluate() =>
        _service.EvaluateGapRepairAsync(_user.Id, _today, [_today.AddDays(-2), _today.AddDays(-1)]);

    /// <summary>
    /// A weekly habit whose occurrences land on yesterday and every seventh day before it. Yesterday is
    /// missed; the earlier occurrences are completed unless <paramref name="skipMostRecentCompletion"/>
    /// leaves the one directly before the gap missed too.
    /// </summary>
    private Habit SetWeeklyHistory(
        int priorOccurrences = 3,
        bool skipMostRecentCompletion = false,
        int gapOccurrences = 1)
    {
        var firstOccurrence = _today.AddDays(-1 - (7 * (priorOccurrences + gapOccurrences - 1)));
        var habit = Habit.Create(new HabitCreateParams(_user.Id, "Long run", FrequencyUnit.Week, 1,
            DueDate: firstOccurrence)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(habit,
            firstOccurrence.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        for (var week = 0; week < priorOccurrences; week++)
        {
            if (skipMostRecentCompletion && week == priorOccurrences - 1)
                continue;
            habit.Log(firstOccurrence.AddDays(7 * week), advanceDueDate: false);
        }
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        _logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habit.Logs.Where(call.Arg<Expression<Func<HabitLog, bool>>>().Compile()).ToList());
        return habit;
    }

    /// <summary>
    /// A yearly habit occurring on yesterday and on the same day a year earlier, the earlier one
    /// completed. Its predecessor is 366 days before the gap, which is the boundary the 365-day
    /// history window used to cut off.
    /// </summary>
    private Habit SetYearlyHistory()
    {
        var firstOccurrence = _today.AddDays(-1).AddYears(-1);
        var habit = Habit.Create(new HabitCreateParams(_user.Id, "Annual review", FrequencyUnit.Year, 1,
            DueDate: firstOccurrence)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(habit,
            firstOccurrence.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        habit.Log(firstOccurrence, advanceDueDate: false);
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        _logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habit.Logs.Where(call.Arg<Expression<Func<HabitLog, bool>>>().Compile()).ToList());
        return habit;
    }

    /// <summary>
    /// A daily habit created 800 days ago, far older than the streak window, so its
    /// effectiveFrom sits at the window boundary rather than at its creation date.
    /// Completions run up to the day before the gap.
    /// </summary>
    private Habit SetLongRunningDailyHistory()
    {
        var createdOn = _today.AddDays(-800);
        var habit = Habit.Create(new HabitCreateParams(_user.Id, "Daily", FrequencyUnit.Day, 1,
            DueDate: createdOn)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(habit,
            createdOn.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        for (var offset = 0; offset < 799; offset++)
            habit.Log(createdOn.AddDays(offset), advanceDueDate: false);
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        _logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habit.Logs.Where(call.Arg<Expression<Func<HabitLog, bool>>>().Compile()).ToList());
        return habit;
    }

    /// <summary>
    /// Sets a persisted-only property the domain does not expose a setter for, the same reflection this
    /// fixture already uses for <see cref="Habit.CreatedAtUtc"/>. Used to stage state an earlier run
    /// would have produced, rather than adding test-only methods to the entity.
    /// </summary>
    private void SetUserProperty(string name, object? value) =>
        typeof(User).GetProperty(name)!.SetValue(_user, value);

    /// <summary>
    /// The repair handler wired to this fixture's substitutes, with the staged freezes it will persist.
    /// </summary>
    private (RepairStreakGapCommandHandler Handler, List<StreakFreeze> Staged) BuildRepairHandler()
    {
        var staged = new List<StreakFreeze>();
        _freezes.AddAsync(Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>())
            .Returns(call => { staged.Add(call.Arg<StreakFreeze>()); return Task.CompletedTask; });
        var unitOfWork = Substitute.For<IUnitOfWork>();
        /**
         * The repair runs inside HabitCeilingLock, the same per-user advisory lock every habit writer
         * holds, so eligibility and the bank spend cannot be split by a concurrent schedule edit. A
         * substituted unit of work returns null from the transaction wrapper unless the operation is
         * actually invoked, so these end-to-end cases run it exactly as the real one does.
         */
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task<Result<int>>>>(0);
                return operation(call.ArgAt<CancellationToken>(1));
            });
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _persistedFreezes.AddRange(staged);
            return Task.FromResult(3);
        });
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        var queryHandler = BuildStreakInfoHandler(flags);
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetStreakInfoQuery>(), call.Arg<CancellationToken>()));
        var handler = new RepairStreakGapCommandHandler(_users, _freezes, _dateService, _service,
            flags, unitOfWork, sender, NullLogger<RepairStreakGapCommandHandler>.Instance);
        return (handler, staged);
    }

    private async Task<StreakInfoResponse> GetStreakInfo()
    {
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        var result = await BuildStreakInfoHandler(flags)
            .Handle(new GetStreakInfoQuery(_user.Id), CancellationToken.None);
        return result.Value;
    }

    private GetStreakInfoQueryHandler BuildStreakInfoHandler(IFeatureFlagService flags) =>
        new(_users, _freezes, _dateService, _service, flags,
            Substitute.For<IProductAnalytics>(), NullLogger<GetStreakInfoQueryHandler>.Instance);

    private Habit SetHistory(DateOnly today, int gapLength, int frequencyQuantity = 1, int precedingCompletions = 3)
    {
        var createdOn = today.AddDays(-gapLength - precedingCompletions);
        var habit = Habit.Create(new HabitCreateParams(_user.Id, "Run", FrequencyUnit.Day, frequencyQuantity,
            DueDate: createdOn)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(habit,
            createdOn.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        for (var offset = 0; offset < precedingCompletions; offset++)
            habit.Log(createdOn.AddDays(offset), advanceDueDate: false);
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        _logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habit.Logs.Where(call.Arg<Expression<Func<HabitLog, bool>>>().Compile()).ToList());
        return habit;
    }
}
