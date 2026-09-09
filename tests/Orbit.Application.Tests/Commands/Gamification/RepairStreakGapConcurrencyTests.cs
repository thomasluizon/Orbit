using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Gamification;

/// <summary>
/// The consistency boundary, proved against a REAL production writer rather than against the
/// repair's own call order.
///
/// A repair reads the habits, the logs and the schedule derived from them, then spends banked
/// freezes against what it read. <c>User.xmin</c> cannot hold those two together: a habit-only write
/// commits without touching the user row, so the optimistic token never fires. The only thing that
/// holds them together is that both sides take <see cref="HabitCeilingLock"/>.
///
/// So this test runs <see cref="DeleteHabitCommandHandler"/> concurrently with the repair and asserts
/// the INTERLEAVING. Before the fix the delete acquired nothing, and its soft delete could commit
/// between the repair's eligibility read and its spend, leaving a freeze paid for a schedule that no
/// longer existed. A test that merely called the two in order stayed green through that.
/// </summary>
public class RepairStreakGapConcurrencyTests
{
    private static readonly DateOnly Today = new(2026, 9, 6);
    private static readonly DateOnly[] Dates = [Today.AddDays(-1)];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly SerializingUnitOfWork _unitOfWork = new();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _freezes = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _dateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _streakService = Substitute.For<IUserStreakService>();
    private readonly IFeatureFlagService _flags = Substitute.For<IFeatureFlagService>();
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly User _user = User.Create("Test", "test@example.com").Value;

    [Fact]
    public async Task ConcurrentHabitDelete_CannotCommitBetweenTheRepairsEligibilityReadAndItsSpend()
    {
        var habit = Habit.Create(new HabitCreateParams(
            _user.Id, "Walk", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var stagedFreeze = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRepair = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(_user);
        _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        _flags.GetEnabledKeysForUserAsync(_user.Id, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.GamificationFreeTier]);
        Bank(1);
        _sender.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new StreakInfoResponse(
                14, 14, Dates[^1], 2, 1, 3, false, Dates, 0, 3, 7, 0, true, false, null, 0)));

        _streakService.EvaluateGapRepairAsync(_user.Id, Today, Dates, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _unitOfWork.Record("repair:evaluate");
                return new UserStreakState(14, 14, Dates[^1]);
            });
        _freezes.AddAsync(Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _unitOfWork.Record("repair:stage");
                stagedFreeze.TrySetResult(true);
                return releaseRepair.Task;
            });

        _habits.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>())
            .Returns(_ => { _unitOfWork.Record("delete:load"); return habit; });
        _habits.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });
        _streakService.RecalculateAsync(_user.Id, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => { _unitOfWork.Record("delete:recalculate"); return new UserStreakState(0, 0, null); });

        var repairHandler = new RepairStreakGapCommandHandler(
            _users, _freezes, _dateService, _streakService, _flags, _unitOfWork, _sender,
            NullLogger<RepairStreakGapCommandHandler>.Instance);
        var deleteHandler = new DeleteHabitCommandHandler(
            _habits, _streakService, _unitOfWork, _dateService, _cache);

        var repair = repairHandler.Handle(new(_user.Id, Dates), CancellationToken.None);
        await stagedFreeze.Task.WaitAsync(Timeout);
        var delete = deleteHandler.Handle(new(_user.Id, habit.Id), CancellationToken.None);
        // Bounded, so a delete that takes no lock fails this test in seconds instead of hanging it.
        await _unitOfWork.SecondLockRequested.Task.WaitAsync(Timeout);

        // The repair is parked between its eligibility read and its spend. The delete has asked for
        // the lock and is holding, so it has not even LOADED the habit, let alone soft-deleted it.
        delete.IsCompleted.Should().BeFalse();
        _unitOfWork.Order.Should().NotContain("delete:load");
        habit.IsDeleted.Should().BeFalse();

        releaseRepair.TrySetResult(true);
        var repairResult = await repair.WaitAsync(Timeout);
        var deleteResult = await delete.WaitAsync(Timeout);

        repairResult.IsSuccess.Should().BeTrue();
        deleteResult.IsSuccess.Should().BeTrue();
        habit.IsDeleted.Should().BeTrue();
        _user.StreakFreezesAccumulated.Should().Be(0);

        var key = $"lock:{HabitCeilingLock.ForUser(_user.Id)}";
        _unitOfWork.Order.Should().Equal(
            key, "repair:evaluate", "repair:stage", "save",
            key, "delete:load", "save", "delete:recalculate", "save");
    }

    private void Bank(int count)
    {
        _user.SetStreakState(count * 7, count * 7, Today.AddDays(-3));
        _user.AwardStreakFreezeIfEligible();
        _user.SetStreakState(0, count * 7, null);
    }
}
