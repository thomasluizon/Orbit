using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetStreakInfoQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IProductAnalytics _productAnalytics = Substitute.For<IProductAnalytics>();
    private readonly GetStreakInfoQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetStreakInfoQueryHandlerTests()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _handler = new GetStreakInfoQueryHandler(
            _userRepo,
            _streakFreezeRepo,
            _userDateService,
            _userStreakService,
            _featureFlagService,
            _productAnalytics,
            NullLogger<GetStreakInfoQueryHandler>.Instance);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _userStreakService.EvaluateRepairAsync(
            UserId,
            Today,
            Today.AddDays(-1),
            Arg.Any<CancellationToken>())
            .Returns(StreakRepairEvaluation.Unavailable(Today.AddDays(-1)));
        _userStreakService.GetRepairableGapDatesAsync(
            UserId,
            Today,
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DateOnly>());
    }

    private static User CreateTestUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, UserId);
        return user;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, UserId);
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private void EnableFreeTierFlag()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.GamificationFreeTier });
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsStreakInfo()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(0);
        result.Value.LongestStreak.Should().Be(0);
        result.Value.FreezesUsedThisMonth.Should().Be(0);
        result.Value.FreezesAvailable.Should().Be(0);
        result.Value.FreezesAvailableToUse.Should().Be(0);
        result.Value.MaxFreezesPerMonth.Should().Be(3);
        result.Value.StreakFreezesAccumulated.Should().Be(0);
        result.Value.MaxStreakFreezesAccumulated.Should().Be(3);
        result.Value.CanEarnMore.Should().BeTrue();
        result.Value.IsFrozenToday.Should().BeFalse();
        result.Value.RecentFreezeDates.Should().BeEmpty();
        result.Value.IsRepairAvailable.Should().BeFalse();
        result.Value.RepairDate.Should().BeNull();
        result.Value.RepairsRemainingThisMonth.Should().Be(0);
        result.Value.LastFreezeCoveredDate.Should().BeNull();
        result.Value.FreezeBankRemaining.Should().BeNull();
        result.Value.RepairableGapDates.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_CalculatesStreakWithoutMutation_AndReturnsFreshValues()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _userStreakService.CalculateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(11, 43, Today));

        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(11);
        result.Value.LongestStreak.Should().Be(43);
        result.Value.LastActiveDate.Should().Be(Today);
        await _userStreakService.Received(1).CalculateAsync(
            UserId,
            Arg.Any<CancellationToken>());
        await _userStreakService.DidNotReceive().RecalculateAsync(
            Arg.Any<Guid>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WithRecentFreezes_ReportsLatestCoveredDateAndRemainingBank()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1)),
            StreakFreeze.Create(UserId, Today.AddDays(-3))
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FreezesUsedThisMonth.Should().Be(2);
        result.Value.FreezesAvailable.Should().Be(0);
        result.Value.RecentFreezeDates.Should().HaveCount(2);
        result.Value.IsFrozenToday.Should().BeFalse();
        result.Value.LastFreezeCoveredDate.Should().Be(Today.AddDays(-1));
        result.Value.FreezeBankRemaining.Should().Be(0);
    }

    [Fact]
    public async Task Handle_FrozenToday_ReportsCorrectly()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today)
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFrozenToday.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_AllFreezesUsed_ReturnsZeroAvailable()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1)),
            StreakFreeze.Create(UserId, Today.AddDays(-5)),
            StreakFreeze.Create(UserId, Today.AddDays(-10))
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FreezesAvailable.Should().Be(0);
        result.Value.FreezesUsedThisMonth.Should().Be(3);
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOff_ReturnsPayGate()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOn_ReturnsStreakInfo()
    {
        var user = CreateFreeUser();
        EnableFreeTierFlag();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_RepairAvailable_AppendsRepairFieldsAndCapturesOffer()
    {
        var user = CreateTestUser();
        user.SetStreakState(7, 7, Today.AddDays(-2));
        user.AwardStreakFreezeIfEligible();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());
        _userStreakService.EvaluateRepairAsync(
            UserId,
            Today,
            Today.AddDays(-1),
            Arg.Any<CancellationToken>())
            .Returns(StreakRepairEvaluation.Available(
                Today.AddDays(-1),
                new UserStreakState(7, 7, Today.AddDays(-1))));

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsRepairAvailable.Should().BeTrue();
        result.Value.RepairDate.Should().Be(Today.AddDays(-1));
        result.Value.RepairsRemainingThisMonth.Should().Be(1);
        _productAnalytics.Received(1).CaptureUserEvent(
            UserId,
            "streak_repair_offered",
            user.Plan.ToString(),
            Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
                (string)properties["missed_date"] == "2026-04-02"
                && (int)properties["remaining_bank"] == 1));
    }

    [Fact]
    public async Task Handle_AppendsRepairableGapDates()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());
        var dates = new[] { Today.AddDays(-8), Today.AddDays(-1) };
        _userStreakService.GetRepairableGapDatesAsync(
            UserId,
            Today,
            Arg.Any<CancellationToken>())
            .Returns(dates);

        var result = await _handler.Handle(new GetStreakInfoQuery(UserId), CancellationToken.None);

        result.Value.RepairableGapDates.Should().Equal(dates);
    }
}
