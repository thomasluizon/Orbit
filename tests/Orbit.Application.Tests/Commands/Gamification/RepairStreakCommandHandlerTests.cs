using System.Data.Common;
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
using Orbit.Application.Tests.Common;

namespace Orbit.Application.Tests.Commands.Gamification;

public class RepairStreakCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 8, 22);
    private static readonly DateOnly MissedDate = Today.AddDays(-1);

    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _freezeRepository = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly IProductAnalytics _productAnalytics = Substitute.For<IProductAnalytics>();
    private readonly RepairStreakCommandHandler _handler;

    public RepairStreakCommandHandlerTests()
    {
        _unitOfWork.PassThroughTransactions<Result>();
        _handler = new RepairStreakCommandHandler(
            _userRepository,
            _freezeRepository,
            _userDateService,
            _userStreakService,
            _featureFlagService,
            _unitOfWork,
            _sender,
            _productAnalytics,
            NullLogger<RepairStreakCommandHandler>.Instance);

        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _freezeRepository.AnyAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>()).Returns(false);
        _sender.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Response()));
    }

    [Fact]
    public async Task Handle_AvailableRepair_SpendsOneFreezeAndReturnsRecomputedBody()
    {
        var user = UserWithOneFreeze();
        ArrangeUser(user);
        _userStreakService.EvaluateRepairAsync(UserId, Today, MissedDate, Arg.Any<CancellationToken>())
            .Returns(StreakRepairEvaluation.Available(
                MissedDate,
                new UserStreakState(10, 12, MissedDate)));

        var result = await _handler.Handle(new RepairStreakCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.StreakFreezesAccumulated.Should().Be(0);
        user.CurrentStreak.Should().Be(10);
        user.LongestStreak.Should().Be(12);
        user.LastActiveDate.Should().Be(MissedDate);
        await _freezeRepository.Received(1).AddAsync(
            Arg.Is<StreakFreeze>(freeze =>
                freeze.UserId == UserId && freeze.UsedOnDate == MissedDate),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _productAnalytics.Received(1).CaptureUserEvent(
            UserId,
            "streak_repair_spent",
            user.Plan.ToString(),
            Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
                (string)properties["missed_date"] == "2026-08-21"
                && (int)properties["remaining_bank"] == 0));
    }

    [Fact]
    public async Task Handle_ExistingRepair_ReturnsSuccessWithoutSecondSpend()
    {
        var user = UserWithOneFreeze();
        ArrangeUser(user);
        _freezeRepository.AnyAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>()).Returns(true);

        var firstResultBody = Response() with { IsRepairAvailable = false };
        _sender.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(firstResultBody));

        var result = await _handler.Handle(new RepairStreakCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(firstResultBody);
        user.StreakFreezesAccumulated.Should().Be(1);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _freezeRepository.DidNotReceive().AddAsync(
            Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyBank_ReturnsStableConflictFailure()
    {
        var user = CreateUser();
        ArrangeUser(user);
        _userStreakService.EvaluateRepairAsync(UserId, Today, MissedDate, Arg.Any<CancellationToken>())
            .Returns(StreakRepairEvaluation.Unavailable(MissedDate));

        var result = await _handler.Handle(new RepairStreakCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.StreakRepairUnavailable);
        user.StreakFreezesAccumulated.Should().Be(0);
        await _freezeRepository.DidNotReceive().AddAsync(
            Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FreeUserWithoutFlag_ReturnsPayGateBeforeEvaluation()
    {
        var user = CreateUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        ArrangeUser(user);

        var result = await _handler.Handle(new RepairStreakCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _userStreakService.DidNotReceive().EvaluateRepairAsync(
            Arg.Any<Guid>(),
            Arg.Any<DateOnly>(),
            Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConcurrentDuplicate_ReturnsSuccessAndResetsTracking()
    {
        var user = UserWithOneFreeze();
        ArrangeUser(user);
        _userStreakService.EvaluateRepairAsync(UserId, Today, MissedDate, Arg.Any<CancellationToken>())
            .Returns(StreakRepairEvaluation.Available(
                MissedDate,
                new UserStreakState(10, 10, MissedDate)));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(
                new DbUpdateException("duplicate", new UniqueViolationDbException())));

        var result = await _handler.Handle(new RepairStreakCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _unitOfWork.Received(1).ResetTracking();
        _productAnalytics.DidNotReceive().CaptureUserEvent(
            Arg.Any<Guid>(),
            "streak_repair_spent",
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Fact]
    public void Command_IsConcurrencyRetryable()
    {
        new RepairStreakCommand(UserId).Should().BeAssignableTo<IConcurrencyRetryable>();
    }

    private void ArrangeUser(User user)
    {
        _userRepository.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>()).Returns(user);
    }

    private static User UserWithOneFreeze()
    {
        var user = CreateUser();
        user.SetStreakState(7, 12, Today.AddDays(-2));
        user.AwardStreakFreezeIfEligible();
        user.SetStreakState(10, 12, Today.AddDays(-2));
        return user;
    }

    private static User CreateUser()
    {
        var user = User.Create("Test", "test@example.com").Value;
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, UserId);
        return user;
    }

    private static StreakInfoResponse Response() => new(
        CurrentStreak: 10,
        LongestStreak: 12,
        LastActiveDate: MissedDate,
        FreezesUsedThisMonth: 1,
        FreezesAvailable: 0,
        MaxFreezesPerMonth: 3,
        IsFrozenToday: false,
        RecentFreezeDates: new[] { MissedDate },
        StreakFreezesAccumulated: 0,
        MaxStreakFreezesAccumulated: 3,
        DaysUntilNextFreeze: 4,
        FreezesAvailableToUse: 0,
        CanEarnMore: true,
        IsRepairAvailable: false,
        RepairDate: null,
        RepairsRemainingThisMonth: 0);

    private sealed class UniqueViolationDbException : DbException
    {
        public override string SqlState => "23505";
    }
}
