using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Challenges.Services;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;
using Orbit.Application.Tests.Common;

namespace Orbit.Application.Tests.Commands.Habits;

/// <summary>
/// Tests for LogHabitCommandHandler covering linked goal updates,
/// referral completion, and gamification result propagation.
/// </summary>
public class LogHabitLinkedGoalTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IGoalCompletionService _goalCompletionService = Substitute.For<IGoalCompletionService>();
    private readonly IChallengeProgressService _challengeProgressService = Substitute.For<IChallengeProgressService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MediatR.IMediator _mediator = Substitute.For<MediatR.IMediator>();
    private readonly LogHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public LogHabitLinkedGoalTests()
    {
        _unitOfWork.PassThroughTransactions<Result<LogHabitCommandHandler.PersistedLog>>();
        var repos = new LogHabitRepositories(_habitRepo, _habitLogRepo, _userRepo);
        var services = new LogHabitServices(
            _userDateService,
            _userStreakService,
            _gamificationService,
            _goalCompletionService,
            _challengeProgressService,
            _mediator,
            Substitute.For<IPayGateService>());
        _handler = new LogHabitCommandHandler(
            repos, services, _unitOfWork, _cache, Substitute.For<ILogger<LogHabitCommandHandler>>());
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<LogHabitResponse>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<LogHabitResponse>>>>(0)(
                call.ArgAt<CancellationToken>(1)));

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _userStreakService.RecalculateAsync(Arg.Any<Guid>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(5, 5, Today));
        _goalCompletionService.SyncDerivedGoalsAsync(
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<DateOnly>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GoalCompletionUpdate>());

        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Handle_WithLinkedStandardGoal_DerivesProgressFromEveryCompletion()
    {
        var goal = Goal.Create(UserId, "Exercise 10 times", 10, "sessions").Value;

        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Exercise", FrequencyUnit.Day, 2, DueDate: Today, IsFlexible: true)).Value;
        habit.AddGoal(goal);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        _goalCompletionService.SyncDerivedGoalsAsync(
                UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>())
            .Returns(
                [new GoalCompletionUpdate(goal.Id, goal.Title, 1, goal.TargetValue, false)],
                [new GoalCompletionUpdate(goal.Id, goal.Title, 2, goal.TargetValue, false)]);

        var first = await _handler.Handle(new LogHabitCommand(UserId, habit.Id), CancellationToken.None);
        var second = await _handler.Handle(new LogHabitCommand(UserId, habit.Id), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.LinkedGoalUpdates.Should().ContainSingle();
        first.Value.LinkedGoalUpdates![0].NewProgress.Should().Be(1);
        second.IsSuccess.Should().BeTrue();
        second.Value.LinkedGoalUpdates.Should().ContainSingle();
        second.Value.LinkedGoalUpdates![0].GoalId.Should().Be(goal.Id);
        second.Value.LinkedGoalUpdates[0].NewProgress.Should().Be(2);
    }

    [Fact]
    public async Task Handle_UnlogLinkedStandardGoal_RecomputesProgressDownward()
    {
        var goal = Goal.Create(UserId, "Exercise 10 times", 10, "sessions").Value;
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Exercise", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var otherHabit = Habit.Create(new HabitCreateParams(
            UserId, "Stretch", FrequencyUnit.Day, 2, DueDate: Today, IsFlexible: true)).Value;
        habit.Log(Today, advanceDueDate: false);
        otherHabit.Log(Today, advanceDueDate: false);
        habit.AddGoal(goal);
        otherHabit.AddGoal(goal);
        goal.SyncStandardProgress(2);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });
        _goalCompletionService.SyncDerivedGoalsAsync(
                UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>())
            .Returns([new GoalCompletionUpdate(goal.Id, goal.Title, 1, goal.TargetValue, false)]);
        var unlogged = await _handler.Handle(new LogHabitCommand(UserId, habit.Id, Today), CancellationToken.None);

        unlogged.IsSuccess.Should().BeTrue();
        unlogged.Value.LinkedGoalUpdates.Should().ContainSingle();
        unlogged.Value.LinkedGoalUpdates![0].NewProgress.Should().Be(1);
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

        var completedHabit = Habit.Create(new HabitCreateParams(
            UserId,
            "Read",
            FrequencyUnit.Day,
            1,
            DueDate: Today.AddDays(-3))).Value;
        completedHabit.Log(Today.AddDays(-3), advanceDueDate: false);
        completedHabit.Log(Today.AddDays(-2), advanceDueDate: false);
        completedHabit.Log(Today.AddDays(-1), advanceDueDate: false);

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId,
            "Doom scrolling",
            FrequencyUnit.Day,
            1,
            IsBadHabit: true,
            DueDate: Today.AddDays(-1))).Value;

        completedHabit.AddGoal(goal);
        badHabit.AddGoal(goal);
        goal.AddHabit(completedHabit);
        goal.AddHabit(badHabit);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(completedHabit);

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });
        _goalCompletionService.SyncDerivedGoalsAsync(
                UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>())
            .Returns([new GoalCompletionUpdate(goal.Id, goal.Title, 2, goal.TargetValue, false)]);

        var result = await _handler.Handle(new LogHabitCommand(UserId, completedHabit.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedGoalUpdates.Should().ContainSingle();
        result.Value.LinkedGoalUpdates![0].NewProgress.Should().Be(2);
    }

    [Fact]
    public async Task Handle_WithLinkedStreakGoal_SyncsStreakProgress()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "7-day streak", 7, "days", Type: GoalType.Streak)).Value;

        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Meditate", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        habit.AddGoal(goal);
        goal.AddHabit(habit);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });
        _goalCompletionService.SyncDerivedGoalsAsync(
                UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>())
            .Returns([new GoalCompletionUpdate(goal.Id, goal.Title, 1, goal.TargetValue, false)]);

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedGoalUpdates.Should().NotBeNull();
        result.Value.LinkedGoalUpdates![0].NewProgress.Should().Be(1);
    }

    [Fact]
    public async Task Handle_LinkedStreakGoalReachesTarget_FiresGamificationOnce()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "3-day streak", 3, "days", Type: GoalType.Streak)).Value;

        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Meditate", FrequencyUnit.Day, 1, DueDate: Today.AddDays(-2))).Value;
        SetCreatedAtUtc(habit, Today.AddDays(-2));
        habit.Log(Today.AddDays(-2), advanceDueDate: false);
        habit.Log(Today.AddDays(-1), advanceDueDate: false);
        habit.AddGoal(goal);
        goal.AddHabit(habit);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });
        _goalCompletionService.SyncDerivedGoalsAsync(
                UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>())
            .Returns([new GoalCompletionUpdate(goal.Id, goal.Title, 3, goal.TargetValue, true)]);

        var result = await _handler.Handle(new LogHabitCommand(UserId, habit.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_LinkedStreakGoalAlreadyCompleted_DoesNotFireGamificationAgain()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "3-day streak", 3, "days", Type: GoalType.Streak)).Value;
        goal.SyncStreakProgress(3);
        goal.Status.Should().Be(GoalStatus.Completed);

        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Meditate", FrequencyUnit.Day, 1, DueDate: Today.AddDays(-2))).Value;
        SetCreatedAtUtc(habit, Today.AddDays(-2));
        habit.Log(Today.AddDays(-2), advanceDueDate: false);
        habit.Log(Today.AddDays(-1), advanceDueDate: false);
        habit.AddGoal(goal);
        goal.AddHabit(habit);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var result = await _handler.Handle(new LogHabitCommand(UserId, habit.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoLinkedGoals_LinkedGoalUpdatesIsNull()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Solo", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedGoalUpdates.Should().BeNull();
    }

    [Fact]
    public async Task Handle_GamificationReturnsXp_IncludesInResponse()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _gamificationService.ProcessHabitLogged(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new HabitLogGamificationResult(15, new List<string> { "liftoff" }));

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.XpEarned.Should().Be(15);
        result.Value.NewAchievementIds.Should().Contain("liftoff");
    }

    [Fact]
    public async Task Handle_ReferralCompletionFails_DoesNotBreakLog()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _mediator.Send(Arg.Any<MediatR.IRequest<Orbit.Domain.Common.Result>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Referral service down"));

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_StreakRecalculateReturnsNull_UsesZeroStreak()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _userStreakService.RecalculateAsync(Arg.Any<Guid>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns((UserStreakState?)null);

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(0);
    }
}
