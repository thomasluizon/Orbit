using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Gamification;

public class RepairStreakGapCommandHandlerTests
{
    private static readonly DateOnly Today = new(2026, 9, 6);
    private static readonly DateOnly[] Dates = [Today.AddDays(-2), Today.AddDays(-1)];
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _freezes = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _dateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _streakService = Substitute.For<IUserStreakService>();
    private readonly IFeatureFlagService _flags = Substitute.For<IFeatureFlagService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly User _user = User.Create("Test", "test@example.com").Value;
    private readonly RepairStreakGapCommandHandler _handler;

    public RepairStreakGapCommandHandlerTests()
    {
        _handler = new(_users, _freezes, _dateService, _streakService, _flags, _unitOfWork, _sender,
            NullLogger<RepairStreakGapCommandHandler>.Instance);
        _users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(_user);
        _dateService.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        _flags.GetEnabledKeysForUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        _streakService.EvaluateGapRepairAsync(_user.Id, Today, Dates, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(14, 14, Dates[^1]));
        _sender.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(Response()));
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task<Result<int>>>>(0);
                return operation(call.ArgAt<CancellationToken>(1));
            });
    }

    /**
     * The consistency boundary, asserted by ORDER rather than by presence.
     *
     * Eligibility is decided from the habits, the logs and the schedule derived from them, and the save
     * then spends the freeze bank. User.xmin cannot hold those together, because a schedule-only habit
     * edit commits without touching the user row and the optimistic token never fires. So the repair
     * has to hold the SAME per-user advisory lock every habit writer holds: UpdateHabitCommand,
     * LogHabitCommand, CreateHabitCommand, MoveHabitParentCommand and RestoreHabitCommand all take
     * HabitCeilingLock.ForUser. The key is taken from that shared helper here for exactly that reason,
     * so a rename cannot leave this passing against a lock nobody else holds.
     *
     * Lock, then evaluate, then save, in that order and inside one transaction, is what makes a
     * concurrent cadence or due-date change unable to land between the eligibility read and the spend.
     */
    [Fact]
    public async Task Repair_LocksBeforeEvaluatingAndSpends_SoAScheduleEditCannotLandBetweenThem()
    {
        Bank(2);
        var order = new List<string>();
        _unitOfWork.AcquireAdvisoryLockAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                order.Add($"lock:{call.ArgAt<string>(0)}");
                return Task.CompletedTask;
            });
        _streakService.EvaluateGapRepairAsync(_user.Id, Today, Dates, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("evaluate");
                return new UserStreakState(14, 14, Dates[^1]);
            });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("save");
                return Task.FromResult(3);
            });

        var result = await _handler.Handle(new(_user.Id, Dates), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Should().Equal($"lock:{HabitCeilingLock.ForUser(_user.Id)}", "evaluate", "save");
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<Result<int>>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoDayGapWithTwoFreezes_SpendsTwoInOneSaveAndRestoresStreak()
    {
        Bank(2);
        var staged = new List<StreakFreeze>();
        _freezes.AddAsync(Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>())
            .Returns(call => { staged.Add(call.Arg<StreakFreeze>()); return Task.CompletedTask; });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            staged.Select(freeze => freeze.UsedOnDate).Should().Equal(Dates);
            _user.StreakFreezesAccumulated.Should().Be(0);
            return Task.FromResult(3);
        });

        var result = await _handler.Handle(new(_user.Id, Dates), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(14);
        _user.CurrentStreak.Should().Be(14);
        _user.LastActiveDate.Should().Be(Dates[^1]);
        _user.StreakFreezesAccumulated.Should().Be(0);
        staged.Should().OnlyContain(freeze => freeze.UserId == _user.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        _user.UpdateStreak(Today);
        _user.AwardStreakFreezeIfEligible();

        _user.CurrentStreak.Should().Be(15);
        _user.StreakFreezesAccumulated.Should().Be(0);
    }

    [Fact]
    public async Task RepairedStreak_AwardsOnlyAtNextNewMilestone()
    {
        Bank(2);
        var result = await _handler.Handle(new(_user.Id, Dates), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        for (var offset = 0; offset < 6; offset++)
        {
            _user.UpdateStreak(Today.AddDays(offset));
            _user.AwardStreakFreezeIfEligible();
            _user.StreakFreezesAccumulated.Should().Be(0);
        }

        _user.UpdateStreak(Today.AddDays(6));
        _user.AwardStreakFreezeIfEligible().Should().BeTrue();
        _user.CurrentStreak.Should().Be(21);
        _user.StreakFreezesAccumulated.Should().Be(1);
    }

    [Fact]
    public async Task TwoDayGapWithOneFreeze_RefusesAndLeavesBankUnchanged()
    {
        Bank(1);

        var result = await _handler.Handle(new(_user.Id, Dates), CancellationToken.None);

        result.ErrorCode.Should().Be(DomainErrors.InsufficientStreakFreezes.Code);
        _user.StreakFreezesAccumulated.Should().Be(1);
        await AssertNoWrites();
    }

    /// <summary>
    /// Still refused, still without spending a freeze. The error code moved because the refusal moved:
    /// a calendar-sparse selection is well formed until the SCHEDULE is known, so it is now the
    /// schedule-aware evaluation that declines it rather than the domain factory. That relocation is
    /// what makes a genuine weekly or yearly gap repairable at all.
    /// </summary>
    [Fact]
    public async Task CalendarSparseSelectionWithNoMatchingSchedule_RefusesWithoutSpending()
    {
        Bank(2);
        var result = await _handler.Handle(new(_user.Id, [Today.AddDays(-3), Today.AddDays(-1)]), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.StreakGapRepairUnavailable);
        _user.StreakFreezesAccumulated.Should().Be(2);
        await AssertNoWrites();
    }

    [Fact]
    public async Task UnavailableGap_RefusesWithoutSpending()
    {
        Bank(2);
        _streakService.EvaluateGapRepairAsync(_user.Id, Today, Dates, Arg.Any<CancellationToken>())
            .Returns((UserStreakState?)null);

        var result = await _handler.Handle(new(_user.Id, Dates), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.StreakGapRepairUnavailable);
        _user.StreakFreezesAccumulated.Should().Be(2);
        await AssertNoWrites();
    }

    [Fact]
    public async Task FreeUserWithoutFlag_IsDeniedBeforeSpending()
    {
        Bank(2);
        _user.StartTrial(DateTime.UtcNow.AddDays(-1));

        var result = await _handler.Handle(new(_user.Id, Dates), CancellationToken.None);

        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        _user.StreakFreezesAccumulated.Should().Be(2);
        await AssertNoWrites();
    }

    [Fact]
    public async Task ConcurrencyConflict_PropagatesForFreshStateRetryWithoutSuccessResponse()
    {
        Bank(2);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new DbUpdateConcurrencyException("stale user")));

        var act = () => _handler.Handle(new(_user.Id, Dates), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        _unitOfWork.Received(1).ResetTracking();
        await _sender.DidNotReceive().Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>());
        new RepairStreakGapCommand(_user.Id, Dates).Should().BeAssignableTo<IConcurrencyRetryable>();
    }

    private void Bank(int count)
    {
        _user.SetStreakState(count * 7, count * 7, Today.AddDays(-3));
        _user.AwardStreakFreezeIfEligible();
        _user.SetStreakState(0, count * 7, null);
    }

    private async Task AssertNoWrites()
    {
        await _freezes.DidNotReceive().AddAsync(Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static StreakInfoResponse Response() => new(
        14, 14, Dates[^1], 2, 1, 3, false, Dates, 0, 3, 7, 0, true, false, null, 0);
}
