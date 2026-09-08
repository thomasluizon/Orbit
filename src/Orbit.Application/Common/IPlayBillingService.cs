using Orbit.Domain.Enums;

namespace Orbit.Application.Common;

/// <summary>
/// Google Play Developer API abstraction for verifying and acknowledging subscription
/// purchases. The implementation lives in Infrastructure so the Application layer has no
/// compile-time dependency on the Google API client, mirroring the Stripe-side <see cref="IBillingService"/> seam.
/// </summary>
public interface IPlayBillingService
{
    /// <summary>
    /// Fetches authoritative subscription state from Google for a purchase token.
    /// Returns null when the purchase token is unknown to Google.
    /// </summary>
    Task<PlaySubscriptionState?> VerifyAsync(string productId, string purchaseToken, CancellationToken cancellationToken);

    /// <summary>Acknowledges a purchase within Google's 3-day auto-refund window. Caller must skip when already acknowledged.</summary>
    Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken cancellationToken);
}

/// <summary>Authoritative Play subscription state resolved from purchases.subscriptionsv2.get.</summary>
public sealed record PlaySubscriptionState(
    bool IsActive,
    DateTime ExpiresAt,
    SubscriptionInterval? Interval,
    bool IsAcknowledged,
    string ProductId,
    string? LinkedPurchaseToken,
    string? ObfuscatedAccountId,
    string? OfferId = null,
    bool IsInGracePeriod = false);
