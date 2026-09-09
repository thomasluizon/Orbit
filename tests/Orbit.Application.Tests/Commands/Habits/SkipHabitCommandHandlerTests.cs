using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using Orbit.Application.Tests.Common;

namespace Orbit.Application.Tests.Commands.Habits;

public class SkipHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IGoalCompletionService _goalCompletionService = Substitute.For<IGoalCompletionService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly SkipHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public SkipHabitCommandHandlerTests()
    {
        _unitOfWork.PassThroughTransactions<Result>();
        _handler = new SkipHabitCommandHandler(
            new SkipHabitRepositories(_habitRepo, _habitLogRepo), _userDateService,
            _goalCompletionService, _unitOfWork, _cache);

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    [Fact]
    public async Task Handle_OneTimeTask_PostponesToTomorrow()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One-time task", null, null,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(Today.AddDays(1));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RecurringHabitDueToday_AdvancesDueDate()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Daily habit", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id, Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().BeAfter(Today);
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OverdueHabit_NotScheduledToday_SkipsAndResolves()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Overdue", FrequencyUnit.Week, 1, DueDate: Today.AddDays(-4))).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().BeAfter(Today);
    }

    [Fact]
    public async Task Handle_IntervalChangedAfterAdvancement_RejectsInactiveWeekThenAllowsMissedActiveDay()
    {
        var anchor = new DateOnly(2025, 1, 6);
        var inactiveDueDate = anchor.AddDays(7);
        var inactiveToday = inactiveDueDate.AddDays(1);
        var missedActiveDay = anchor.AddDays(14);
        var catchUpDay = missedActiveDay.AddDays(1);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Alternating", FrequencyUnit.Day, 1, DueDate: anchor,
            Days: [DayOfWeek.Monday])).Value;
        habit.AdvanceDueDate(anchor.AddDays(6), weekStartDay: 1);
        typeof(Habit).GetProperty(nameof(Habit.IntervalWeeks))!.SetValue(habit, 2);
        habit.DueDate.Should().Be(inactiveDueDate);

        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(inactiveToday, catchUpDay);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        SetupHabitFound(habit);

        var inactiveResult = await _handler.Handle(
            new SkipHabitCommand(UserId, habit.Id), CancellationToken.None);
        var activeResult = await _handler.Handle(
            new SkipHabitCommand(UserId, habit.Id), CancellationToken.None);

        inactiveResult.IsFailure.Should().BeTrue();
        inactiveResult.ErrorCode.Should().Be(ErrorCodes.NotScheduledOnDate);
        activeResult.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new SkipHabitCommand(UserId, Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotFound.Message);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsFailure()
    {
        var otherUserId = Guid.NewGuid();
        var habit = Habit.Create(new HabitCreateParams(
            otherUserId, "Someone else's habit", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotOwned.Message);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotOwned);
    }

    [Fact]
    public async Task Handle_CompletedHabit_ReturnsFailure()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Task", null, null,
            DueDate: Today)).Value;
        habit.Log(Today);
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("completed");
    }

    [Fact]
    public async Task Handle_FutureDate_ReturnsFailure()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Daily", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var futureDate = Today.AddDays(5);
        var command = new SkipHabitCommand(UserId, habit.Id, futureDate);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("future");
    }

    [Fact]
    public async Task Handle_NotYetDue_ReturnsFailure()
    {
        var futureDueDate = Today.AddDays(5);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Future habit", FrequencyUnit.Day, 1,
            DueDate: futureDueDate)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id, Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not yet due");
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Task", null, null,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"summary:{UserId}:{realToday:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new SkipHabitCommand(UserId, habit.Id);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithLinkedStreakGoal_UsesMinimumStreakAcrossAllLinkedHabits()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId,
            "Avoid doom scrolling",
            7,
            "days",
            Type: GoalType.Streak)).Value;

        var skippedHabit = Habit.Create(new HabitCreateParams(
            UserId,
            "Read",
            FrequencyUnit.Day,
            1,
            DueDate: Today.AddDays(-3))).Value;
        skippedHabit.Log(Today.AddDays(-3), advanceDueDate: false);
        skippedHabit.Log(Today.AddDays(-2), advanceDueDate: false);
        skippedHabit.Log(Today.AddDays(-1), advanceDueDate: false);

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId,
            "Doom scrolling",
            FrequencyUnit.Day,
            1,
            IsBadHabit: true,
            DueDate: Today.AddDays(-1))).Value;

        skippedHabit.AddGoal(goal);
        badHabit.AddGoal(goal);
        goal.AddHabit(skippedHabit);
        goal.AddHabit(badHabit);

        SetupHabitFound(skippedHabit);
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var result = await _handler.Handle(new SkipHabitCommand(UserId, skippedHabit.Id, Today), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { goal.Id })),
            Today,
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FlexibleHabit_WithRemainingCompletions_SkipsAndAddsLog()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Workout", FrequencyUnit.Week, 3,
            IsFlexible: true, DueDate: Today)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id, Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FlexibleHabit_AllInstancesDoneInWindow_ReturnsFailure()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Workout", FrequencyUnit.Week, 1,
            IsFlexible: true, DueDate: Today)).Value;
        habit.Log(Today, advanceDueDate: false);
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id, Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AllInstancesDone);
        await _habitLogRepo.DidNotReceive().AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TargetBeyondOverdueWindow_ReturnsFailure()
    {
        var beyondWindow = Today.AddDays(-(AppConstants.DefaultOverdueWindowDays + 3));
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Stale overdue", FrequencyUnit.Day, 1,
            DueDate: beyondWindow)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id, beyondWindow);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BeyondOverdueWindow);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TargetAtOverdueWindowBoundary_Succeeds()
    {
        var boundary = Today.AddDays(-AppConstants.DefaultOverdueWindowDays);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Boundary", FrequencyUnit.Day, 1,
            DueDate: boundary)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id, boundary);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().BeAfter(boundary);
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    private void SetupHabitFound(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
    }
}
