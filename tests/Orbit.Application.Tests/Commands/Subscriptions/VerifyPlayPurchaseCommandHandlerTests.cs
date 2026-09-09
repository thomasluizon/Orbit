using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class VerifyPlayPurchaseCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPlayBillingService _playBilling = Substitute.For<IPlayBillingService>();
    private readonly IPlayReferralCouponConsumer _referralConsumer = Substitute.For<IPlayReferralCouponConsumer>();
    private readonly VerifyPlayPurchaseCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly IOptions<GooglePlaySettings> Settings = Options.Create(
        new GooglePlaySettings { PackageName = "org.useorbit.app", ProductId = "orbit_pro", MonthlyBasePlanId = "monthly", YearlyBasePlanId = "yearly" });

    public VerifyPlayPurchaseCommandHandlerTests()
    {
        _handler = new VerifyPlayPurchaseCommandHandler(
            _userRepo, _unitOfWork, _playBilling, _referralConsumer, Settings,
            Substitute.For<ILogger<VerifyPlayPurchaseCommandHandler>>());
    }

    private void StubUser(User? user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    private void StubVerify(PlaySubscriptionState? state) =>
        _playBilling.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(state);

    private static PlaySubscriptionState ActiveState(bool acknowledged = false) =>
        new(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, acknowledged, "orbit_pro", null, UserId.ToString());

    private static VerifyPlayPurchaseCommand Command() => new(UserId, "orbit_pro", "play_token_123");

    [Theory]
    [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", SubscriptionLapseReason.PaymentFailed)]
    [InlineData("SUBSCRIPTION_STATE_ACTIVE", null)]
    public async Task Handle_VerifiedPaymentState_UpdatesReasonWithoutLosingAccess(
        string subscriptionState, SubscriptionLapseReason? expectedReason)
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        var expiresAt = DateTime.UtcNow.AddMonths(1);
        user.SetPlaySubscription("play_token_123", expiresAt, SubscriptionInterval.Monthly);
        user.RecordSubscriptionLapseReason(SubscriptionSource.GooglePlay, SubscriptionLapseReason.PaymentFailed);
        StubUser(user);
        using var play = new PlayBillingTestClient(subscriptionState, expiresAt, UserId, Settings);
        StubVerify(await play.Billing.VerifyAsync("orbit_pro", "play_token_123", CancellationToken.None));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.Source.Should().Be("play");
        user.SubscriptionLapseReason.Should().Be(expectedReason);
        user.SubscriptionEndedAtUtc.Should().BeNull();
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        StubUser(null);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_ActivePurchase_GrantsProAndAcknowledges()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(ActiveState());

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.Source.Should().Be("play");
        user.IsPro.Should().BeTrue();
        user.PlayPurchaseToken.Should().Be("play_token_123");
        await _playBilling.Received().AcknowledgeAsync("orbit_pro", "play_token_123", Arg.Any<CancellationToken>());
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyAcknowledged_DoesNotAcknowledgeAgain()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(ActiveState(acknowledged: true));

        await _handler.Handle(Command(), CancellationToken.None);

        await _playBilling.DidNotReceive().AcknowledgeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InactivePurchase_ReturnsFailureAndDoesNotGrant()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(false, DateTime.UtcNow.AddMonths(-1), null, false, "orbit_pro", null, null));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PlayPurchaseNotActive);
        user.IsPro.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NonConfiguredProduct_ReturnsFailureAndDoesNotGrant()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(
            true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, false, "different_product", null, UserId.ToString()));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PlayPurchaseNotActive);
        user.IsPro.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnrecognizedBasePlan_ReturnsFailureAndDoesNotGrant()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(
            true, DateTime.UtcNow.AddMonths(1), null, false, "orbit_pro", null, UserId.ToString()));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PlayPurchaseNotActive);
        user.IsPro.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AccountMismatch_ReturnsFailureAndDoesNotGrant()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(
            true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, false, "orbit_pro", null, Guid.NewGuid().ToString()));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PlayPurchaseAccountMismatch);
        user.IsPro.Should().BeFalse();
        await _playBilling.DidNotReceive().AcknowledgeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _referralConsumer.DidNotReceive().ConsumeOnNewPurchase(
            Arg.Any<User>(), Arg.Any<PlaySubscriptionState>(), Arg.Any<string>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ActivePurchase_InvokesCouponConsumerBeforeLinkingToken()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(ActiveState());
        string? tokenWhenConsumerRan = "unset";
        _referralConsumer
            .ConsumeOnNewPurchase(user, Arg.Any<PlaySubscriptionState>(), "play_token_123")
            .Returns(_ =>
            {
                tokenWhenConsumerRan = user.PlayPurchaseToken;
                return null;
            });

        await _handler.Handle(Command(), CancellationToken.None);

        tokenWhenConsumerRan.Should().BeNull();
        _referralConsumer.Received(1).ConsumeOnNewPurchase(
            user, Arg.Any<PlaySubscriptionState>(), "play_token_123");
    }

    [Fact]
    public async Task Handle_StripeCoversLaterPeriod_StillInvokesCouponConsumer()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddMonths(6));
        StubUser(user);
        StubVerify(ActiveState());

        await _handler.Handle(Command(), CancellationToken.None);

        _referralConsumer.Received(1).ConsumeOnNewPurchase(
            user, Arg.Any<PlaySubscriptionState>(), "play_token_123");
    }

    [Fact]
    public async Task Handle_NullState_ReturnsFailure()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(null);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PlayPurchaseNotActive);
    }

    [Fact]
    public async Task Handle_VerifyThrows_ReturnsFailure()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        _playBilling.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BillingProviderException("boom"));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TokenAlreadyOwnedByAnotherUser_ReturnsAccountMismatch()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(ActiveState());
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("duplicate key value violates unique constraint"));
        _userRepo.AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PlayPurchaseAccountMismatch);
    }

    [Fact]
    public async Task Handle_StripeCoversLaterPeriod_LinksTokenAndAcknowledgesWithoutShorteningExpiry()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        var stripeExpiry = DateTime.UtcNow.AddMonths(6);
        user.SetStripeSubscription("sub_123", stripeExpiry);
        StubUser(user);
        StubVerify(ActiveState());

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Source.Should().Be("stripe");
        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
        user.PlanExpiresAt.Should().Be(stripeExpiry);
        user.PlayPurchaseToken.Should().Be("play_token_123");
        await _playBilling.Received().AcknowledgeAsync("orbit_pro", "play_token_123", Arg.Any<CancellationToken>());
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReferralCoupon_CancelledOnlyAfterSaveSucceeds()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(ActiveState());
        _referralConsumer.ConsumeOnNewPurchase(user, Arg.Any<PlaySubscriptionState>(), "play_token_123")
            .Returns("coupon_abc");

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _referralConsumer.CancelConsumedCouponAsync(UserId, "coupon_abc", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Handle_ReferralCoupon_SaveFails_CouponNotCancelled()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(ActiveState());
        _referralConsumer.ConsumeOnNewPurchase(user, Arg.Any<PlaySubscriptionState>(), "play_token_123")
            .Returns("coupon_abc");
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("transient"));
        _userRepo.AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var act = () => _handler.Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        await _referralConsumer.DidNotReceive().CancelConsumedCouponAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
