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

    [Fact]
    public async Task NonContiguousSelection_RefusesWithoutSpending()
    {
        Bank(2);
        var result = await _handler.Handle(new(_user.Id, [Today.AddDays(-3), Today.AddDays(-1)]), CancellationToken.None);

        result.ErrorCode.Should().Be(DomainErrors.InvalidStreakGap.Code);
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
