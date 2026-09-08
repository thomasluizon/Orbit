using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Application.Subscriptions.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Subscriptions;

public class HandlePlayNotificationCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPlayBillingService _playBilling = Substitute.For<IPlayBillingService>();
    private readonly IPlayReferralCouponConsumer _referralConsumer = Substitute.For<IPlayReferralCouponConsumer>();
    private readonly IGenericRepository<ProcessedPlayNotification> _processedRepo = Substitute.For<IGenericRepository<ProcessedPlayNotification>>();
    private readonly HandlePlayNotificationCommandHandler _handler;

    private static readonly IOptions<GooglePlaySettings> Settings = Options.Create(
        new GooglePlaySettings { PackageName = "org.useorbit.app", ProductId = "orbit_pro", MonthlyBasePlanId = "monthly", YearlyBasePlanId = "yearly" });

    public HandlePlayNotificationCommandHandlerTests()
    {
        _handler = new HandlePlayNotificationCommandHandler(
            _userRepo, _unitOfWork, _playBilling, _referralConsumer, _processedRepo, Settings,
            Substitute.For<ILogger<HandlePlayNotificationCommandHandler>>());
    }

    private void StubUser(User? user) =>
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    private void StubVerify(PlaySubscriptionState? state) =>
        _playBilling.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(state);

    private static string BuildPushBody(int notificationType, string purchaseToken, string subscriptionId)
    {
        var developerNotification = JsonSerializer.Serialize(new
        {
            version = "1.0",
            packageName = "org.useorbit.app",
            eventTimeMillis = "1700000000000",
            subscriptionNotification = new
            {
                version = "1.0",
                notificationType,
                purchaseToken,
                subscriptionId,
            },
        });
        return WrapEnvelope(developerNotification);
    }

    private static string WrapEnvelope(string developerNotificationJson)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(developerNotificationJson));
        return JsonSerializer.Serialize(new
        {
            message = new { data, messageId = "1" },
            subscription = "projects/x/subscriptions/y",
        });
    }

    [Fact]
    public async Task Handle_MalformedBody_ReturnsSuccessWithoutSaving()
    {
        var result = await _handler.Handle(new HandlePlayNotificationCommand("not-json"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoSubscriptionNotification_ReturnsSuccessWithoutVerifying()
    {
        var body = WrapEnvelope("""{"version":"1.0","testNotification":{"version":"1.0"}}""");

        var result = await _handler.Handle(new HandlePlayNotificationCommand(body), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _playBilling.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("SUBSCRIPTION_STATE_ACTIVE", 2, null)]
    [InlineData("SUBSCRIPTION_STATE_ACTIVE", 6, null)]
    [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", 6, SubscriptionLapseReason.PaymentFailed)]
    [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", 2, SubscriptionLapseReason.PaymentFailed)]
    [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", 3, SubscriptionLapseReason.PaymentFailed)]
    public async Task Handle_ActiveState_GrantsPro(string subscriptionState, int notificationType,
        SubscriptionLapseReason? expectedReason)
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        var expiresAt = DateTime.UtcNow.AddMonths(1);
        user.SetPlaySubscription("tok_renew", expiresAt, SubscriptionInterval.Monthly);
        user.RecordSubscriptionLapseReason(SubscriptionSource.GooglePlay, SubscriptionLapseReason.PaymentFailed);
        StubUser(user);
        using var play = new PlayBillingTestClient(subscriptionState, expiresAt, user.Id, Settings);
        StubVerify(await play.Billing.VerifyAsync("orbit_pro", "tok_renew", CancellationToken.None));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(notificationType, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.IsPro.Should().BeTrue();
        user.HasProAccess.Should().BeTrue();
        user.SubscriptionLapseReason.Should().Be(expectedReason);
        user.SubscriptionEndedAtUtc.Should().BeNull();
        user.PlayPurchaseToken.Should().Be("tok_renew");
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());

        _userRepo.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 9, 6));
        var status = await new GetSubscriptionStatusQueryHandler(_userRepo, Substitute.For<IPayGateService>(), dates)
            .Handle(new GetSubscriptionStatusQuery(user.Id), CancellationToken.None);

        status.IsSuccess.Should().BeTrue();
        status.Value.HasProAccess.Should().BeTrue();
        status.Value.Source.Should().Be("play");
        status.Value.LapseReason.Should().Be(expectedReason == SubscriptionLapseReason.PaymentFailed ? "payment_failed" : null);
        status.Value.SubscriptionEndedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("SUBSCRIPTION_STATE_ON_HOLD", 5, SubscriptionLapseReason.PaymentFailed)]
    [InlineData("SUBSCRIPTION_STATE_PAUSED", 10, SubscriptionLapseReason.Expired)]
    public async Task Handle_VerifiedNonEntitledState_RemovesAccessWithExistingReason(
        string subscriptionState, int notificationType, SubscriptionLapseReason expectedReason)
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        StubUser(user);
        using var play = new PlayBillingTestClient(subscriptionState, DateTime.UtcNow.AddMonths(1), user.Id, Settings);
        StubVerify(await play.Billing.VerifyAsync("orbit_pro", "tok_old", CancellationToken.None));

        var result = await _handler.Handle(
            new HandlePlayNotificationCommand(BuildPushBody(notificationType, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.IsPro.Should().BeFalse();
        user.HasProAccess.Should().BeFalse();
        user.SubscriptionSource.Should().BeNull();
        user.SubscriptionLapseReason.Should().Be(expectedReason);
        user.SubscriptionEndedAtUtc.Should().NotBeNull();
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PlayGraceWhileStripeOwnsEntitlement_PreservesStripeReason()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        var stripeExpiry = DateTime.UtcNow.AddMonths(2);
        user.SetStripeSubscription("sub_test", stripeExpiry);
        user.RecordSubscriptionLapseReason(SubscriptionSource.Stripe, SubscriptionLapseReason.Canceled);
        user.LinkPlayPurchaseToken("tok_old");
        StubUser(user);
        using var play = new PlayBillingTestClient("SUBSCRIPTION_STATE_IN_GRACE_PERIOD",
            DateTime.UtcNow.AddMonths(1), user.Id, Settings);
        StubVerify(await play.Billing.VerifyAsync("orbit_pro", "tok_old", CancellationToken.None));

        var result = await _handler.Handle(
            new HandlePlayNotificationCommand(BuildPushBody(6, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
        user.PlanExpiresAt.Should().Be(stripeExpiry);
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.Canceled);
        user.SubscriptionEndedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handle_InactiveState_CancelsSubscription()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(false, DateTime.UtcNow.AddDays(-1), null, false, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(13, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Plan.Should().Be(UserPlan.Free);
        user.PlayPurchaseToken.Should().BeNull();
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.Expired);
        user.SubscriptionEndedAtUtc.Should().NotBeNull();
        _referralConsumer.DidNotReceive().ConsumeOnNewPurchase(
            Arg.Any<User>(), Arg.Any<PlaySubscriptionState>(), Arg.Any<string>());
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AccountHold_RecordsPaymentFailureReason()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(false, DateTime.UtcNow, null, true, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(5, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.PaymentFailed);
        user.SubscriptionEndedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_CanceledWhileEntitled_RecordsPendingCanceledReason()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(3, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.IsPro.Should().BeTrue();
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.Canceled);
        user.SubscriptionEndedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handle_PlayCanceledWhileStripeOwnsEntitlement_DoesNotRecordPlayReason()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddMonths(2));
        user.LinkPlayPurchaseToken("tok_old");
        StubUser(user);
        StubVerify(new PlaySubscriptionState(
            true,
            DateTime.UtcNow.AddMonths(1),
            SubscriptionInterval.Monthly,
            true,
            "orbit_pro",
            null,
            null));

        var result = await _handler.Handle(
            new HandlePlayNotificationCommand(BuildPushBody(3, "tok_old", "orbit_pro")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
        user.SubscriptionLapseReason.Should().BeNull();
        user.SubscriptionEndedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handle_CanceledAfterPaymentFailure_PreservesPaymentFailureReason()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        user.RecordSubscriptionLapseReason(
            SubscriptionSource.GooglePlay, SubscriptionLapseReason.PaymentFailed);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(false, DateTime.UtcNow, null, true, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(3, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.PaymentFailed);
    }

    [Fact]
    public async Task Handle_ExpiredAfterCancellation_PreservesCanceledReason()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        user.RecordSubscriptionLapseReason(
            SubscriptionSource.GooglePlay, SubscriptionLapseReason.Canceled);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(false, DateTime.UtcNow, null, true, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(13, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.Canceled);
    }

    [Fact]
    public async Task Handle_RevokedSubscription_RecordsExpiredReason()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(false, DateTime.UtcNow, null, true, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(12, "tok_old", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.Expired);
    }

    [Fact]
    public async Task Handle_PurchaseGrantingPro_InvokesCouponConsumerBeforeTokenOverwrite()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(
            true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null, "referral10"));
        string? tokenWhenConsumerRan = "unset";
        _referralConsumer
            .ConsumeOnNewPurchase(user, Arg.Any<PlaySubscriptionState>(), "tok_purchase")
            .Returns(_ =>
            {
                tokenWhenConsumerRan = user.PlayPurchaseToken;
                return null;
            });

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(4, "tok_purchase", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        tokenWhenConsumerRan.Should().BeNull();
        _referralConsumer.Received(1).ConsumeOnNewPurchase(
            user, Arg.Any<PlaySubscriptionState>(), "tok_purchase");
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsSuccessWithoutSaving()
    {
        StubUser(null);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(4, "tok_unknown", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_LinkedPurchaseToken_FindsUserByLinkedTokenAndRepoints()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok_old", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        _userRepo.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null, user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Yearly, true, "orbit_pro", "tok_old", null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(7, "tok_new", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.PlayPurchaseToken.Should().Be("tok_new");
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_VerifyThrows_ReturnsFailureSoPubSubRetries()
    {
        StubUser(User.Create("Thomas", "test@example.com").Value);
        _playBilling.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BillingProviderException("boom"));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NullState_ReturnsSuccessWithoutSaving()
    {
        StubVerify(null);

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DuplicateMessageId_SkipsProcessingWithoutVerifying()
    {
        _processedRepo.AnyAsync(
            Arg.Any<Expression<Func<ProcessedPlayNotification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _playBilling.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnrecognizedBasePlan_DoesNotGrantPro()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), null, true, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.IsPro.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_NotificationAccountMismatch_SkipsWithoutMutating()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        user.SetPlaySubscription("tok", DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, Guid.NewGuid().ToString()));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.IsPro.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConcurrentDuplicate_SaveConflictButAlreadyRecorded_ReturnsSuccess()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null));
        _processedRepo.AnyAsync(
            Arg.Any<Expression<Func<ProcessedPlayNotification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(false, true);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("duplicate key value violates unique constraint"));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_StripeCoversLaterPeriod_LinksTokenWithoutDowngradingStripeEntitlement()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        var stripeExpiry = DateTime.UtcNow.AddMonths(6);
        user.SetStripeSubscription("sub_123", stripeExpiry);
        StubUser(user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null));

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(4, "tok_play", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
        user.PlanExpiresAt.Should().Be(stripeExpiry);
        user.PlayPurchaseToken.Should().Be("tok_play");
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReferralCoupon_CancelledOnlyAfterSaveSucceeds()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(
            true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null, "referral10"));
        _referralConsumer.ConsumeOnNewPurchase(user, Arg.Any<PlaySubscriptionState>(), "tok_purchase")
            .Returns("coupon_abc");

        var result = await _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(4, "tok_purchase", "orbit_pro")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _referralConsumer.CancelConsumedCouponAsync(user.Id, "coupon_abc", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Handle_ReferralCoupon_SaveFails_CouponNotCancelled()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(
            true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null, "referral10"));
        _referralConsumer.ConsumeOnNewPurchase(user, Arg.Any<PlaySubscriptionState>(), "tok_purchase")
            .Returns("coupon_abc");
        _processedRepo.AnyAsync(
            Arg.Any<Expression<Func<ProcessedPlayNotification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(false);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("transient"));

        var act = () => _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(4, "tok_purchase", "orbit_pro")), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        await _referralConsumer.DidNotReceive().CancelConsumedCouponAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Command_IsMarkedConcurrencyRetryable() =>
        typeof(HandlePlayNotificationCommand).Should().BeAssignableTo<IConcurrencyRetryable>();

    [Fact]
    public async Task Handle_SaveThrowsConcurrencyConflict_PropagatesWithoutTreatingAsDuplicate()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("stale xmin token"));

        var act = () => _handler.Handle(new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro")), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await _processedRepo.Received(1).AnyAsync(
            Arg.Any<Expression<Func<ProcessedPlayNotification, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ThroughRetryBehavior_ConcurrencyConflictThenSuccess_GrantsProAndRetries()
    {
        var user = User.Create("Thomas", "test@example.com").Value;
        StubUser(user);
        StubVerify(new PlaySubscriptionState(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, true, "orbit_pro", null, null));

        var saveAttempts = 0;
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => saveAttempts++ == 0
                ? throw new DbUpdateConcurrencyException("stale xmin token")
                : 1);

        var command = new HandlePlayNotificationCommand(BuildPushBody(2, "tok_renew", "orbit_pro"));
        var behavior = new ConcurrencyRetryBehavior<HandlePlayNotificationCommand, Result>(_unitOfWork);
        RequestHandlerDelegate<Result> next = cancellationToken => _handler.Handle(command, cancellationToken);

        var result = await behavior.Handle(command, next, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        saveAttempts.Should().Be(2);
        user.PlayPurchaseToken.Should().Be("tok_renew");
        _unitOfWork.Received(1).ResetTracking();
    }
}
