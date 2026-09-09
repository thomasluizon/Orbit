using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Commands.Habits;

public class DeleteHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly DeleteHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public DeleteHabitCommandHandlerTests()
    {
        _unitOfWork.PassThroughTransactions<Result>();
        _handler = new DeleteHabitCommandHandler(_habitRepo, _userStreakService, _unitOfWork, _userDateService, _cache);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        _userStreakService.RecalculateAsync(UserId, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(0, 0, null));
    }

    private static Habit CreateTestHabit(Guid? userId = null)
    {
        return Habit.Create(new HabitCreateParams(
            userId ?? UserId, "Test Habit", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    [Fact]
    public async Task Handle_ValidCommand_RemovesHabit()
    {
        var habit = CreateTestHabit();
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>())
            .Returns(habit);
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });

        var command = new DeleteHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.IsDeleted.Should().BeTrue();
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new DeleteHabitCommand(UserId, Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit not found.");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsFailure()
    {
        var otherUserId = Guid.NewGuid();
        var habit = CreateTestHabit(otherUserId);
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new DeleteHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("You don't have permission to delete this habit.");
        habit.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_HabitWithSubHabits_CascadeSoftDeletesSubtreeWithSharedTimestamp()
    {
        var parent = CreateTestHabit();
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", FrequencyUnit.Day, 1, DueDate: Today, ParentHabitId: parent.Id)).Value;
        var grandchild = Habit.Create(new HabitCreateParams(
            UserId, "Grandchild", FrequencyUnit.Day, 1, DueDate: Today, ParentHabitId: child.Id)).Value;

        _habitRepo.GetByIdAsync(parent.Id, Arg.Any<CancellationToken>()).Returns(parent);
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child, grandchild });

        var result = await _handler.Handle(new DeleteHabitCommand(UserId, parent.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        parent.IsDeleted.Should().BeTrue();
        child.IsDeleted.Should().BeTrue();
        grandchild.IsDeleted.Should().BeTrue();
        child.DeletedAtUtc.Should().Be(parent.DeletedAtUtc);
        grandchild.DeletedAtUtc.Should().Be(parent.DeletedAtUtc);
    }

    [Fact]
    public async Task Handle_LastLinkedHabit_ClearsActiveDerivedGoalAndRestoresManualProgress()
    {
        var habit = CreateTestHabit();
        var goal = Goal.Create(UserId, "Exercise", 10, "sessions").Value;
        goal.AddHabit(habit);
        goal.SyncStandardProgress(3).IsSuccess.Should().BeTrue();
        var updatedBeforeDelete = goal.UpdatedAtUtc;
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>()).Returns(habit);
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });

        var result = await _handler.Handle(new DeleteHabitCommand(UserId, habit.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.Goals.Should().BeEmpty();
        goal.Habits.Should().BeEmpty();
        goal.CurrentValue.Should().Be(0);
        goal.IsProgressDerived.Should().BeFalse();
        goal.UpdatedAtUtc.Should().BeOnOrAfter(updatedBeforeDelete);
        goal.UpdateProgress(1).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_LastLinkedHabit_PreservesCompletedGoalStateWhileRestoringManualMode()
    {
        var habit = CreateTestHabit();
        var goal = Goal.Create(UserId, "Exercise", 2, "sessions").Value;
        goal.AddHabit(habit);
        goal.SyncStandardProgress(2).IsSuccess.Should().BeTrue();
        var completedAtUtc = goal.CompletedAtUtc;
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>()).Returns(habit);
        _habitRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });

        var result = await _handler.Handle(new DeleteHabitCommand(UserId, habit.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.IsProgressDerived.Should().BeFalse();
        goal.CurrentValue.Should().Be(2);
        goal.Status.Should().Be(GoalStatus.Completed);
        goal.CompletedAtUtc.Should().Be(completedAtUtc);
    }
}
