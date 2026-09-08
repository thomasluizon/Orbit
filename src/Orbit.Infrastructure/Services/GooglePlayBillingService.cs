using Google;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Google Play-backed implementation of <see cref="IPlayBillingService"/>. All Google
/// Android Publisher API calls are isolated here so the Application layer stays SDK-free.
/// </summary>
public sealed class GooglePlayBillingService(
    AndroidPublisherService androidPublisher,
    IOptions<GooglePlaySettings> settings) : IPlayBillingService
{
    private static readonly HashSet<string> EntitledStates = new(StringComparer.Ordinal)
    {
        "SUBSCRIPTION_STATE_ACTIVE",
        "SUBSCRIPTION_STATE_IN_GRACE_PERIOD",
        "SUBSCRIPTION_STATE_CANCELED",
    };

    private readonly GooglePlaySettings _settings = settings.Value;

    public async Task<PlaySubscriptionState?> VerifyAsync(string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        SubscriptionPurchaseV2 purchase;
        try
        {
            purchase = await androidPublisher.Purchases.Subscriptionsv2
                .Get(_settings.PackageName, purchaseToken)
                .ExecuteAsync(cancellationToken);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BillingProviderException("Failed to verify Play subscription", ex);
        }

        var lineItem = purchase.LineItems?.FirstOrDefault(item => item.ExpiryTimeDateTimeOffset is not null)
            ?? purchase.LineItems?.FirstOrDefault();

#pragma warning disable ORBIT0004
        var expiresAt = lineItem?.ExpiryTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow;
#pragma warning restore ORBIT0004
        var resolvedProductId = lineItem?.ProductId ?? productId;
        var interval = _settings.IntervalForBasePlan(lineItem?.OfferDetails?.BasePlanId);

        var isActive = purchase.SubscriptionState is not null
            && EntitledStates.Contains(purchase.SubscriptionState)
#pragma warning disable ORBIT0004
            && expiresAt > DateTime.UtcNow;
#pragma warning restore ORBIT0004

        var isAcknowledged = purchase.AcknowledgementState == "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED";

        return new PlaySubscriptionState(
            isActive,
            expiresAt,
            interval,
            isAcknowledged,
            resolvedProductId,
            purchase.LinkedPurchaseToken,
            purchase.ExternalAccountIdentifiers?.ObfuscatedExternalAccountId,
            lineItem?.OfferDetails?.OfferId,
            purchase.SubscriptionState == "SUBSCRIPTION_STATE_IN_GRACE_PERIOD");
    }

    public async Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        try
        {
            await androidPublisher.Purchases.Subscriptions
                .Acknowledge(new SubscriptionPurchasesAcknowledgeRequest(), _settings.PackageName, productId, purchaseToken)
                .ExecuteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BillingProviderException("Failed to acknowledge Play subscription", ex);
        }
    }
}
