using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Common;
using Stripe;
using Stripe.Checkout;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Stripe-backed implementation of <see cref="IBillingService"/>. All Stripe SDK calls
/// are isolated here so the Application layer stays SDK-free.
/// </summary>
/// <summary>Groups the Stripe SDK service clients the billing service uses to keep its constructor small.</summary>
public record StripeServiceClients(
    CustomerService Customers,
    Stripe.Checkout.SessionService CheckoutSessions,
    Stripe.BillingPortal.SessionService PortalSessions,
    SubscriptionService Subscriptions,
    InvoiceService Invoices,
    PriceService Prices,
    CouponService Coupons);

public sealed partial class StripeBillingService(
    StripeServiceClients clients,
    ILogger<StripeBillingService> logger) : IBillingService
{
    public async Task<string> CreateCustomerAsync(string email, string name, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var customer = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Customers.CreateAsync(new CustomerCreateOptions
                {
                    Email = email,
                    Name = name,
                    Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString() },
                }, new RequestOptions { IdempotencyKey = $"orbit-customer-create-{userId}" }, cancellationToken),
                cancellationToken);
            return customer.Id;
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to create customer", ex);
        }
    }

    public async Task<string> CreateCheckoutSessionAsync(
        string customerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        Guid userId,
        string? referralCouponId,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = new SessionCreateOptions
            {
                Customer = customerId,
                Mode = "subscription",
                LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString() },
            };

            if (!string.IsNullOrEmpty(referralCouponId))
            {
                options.Discounts = [new SessionDiscountOptions { Coupon = referralCouponId }];
            }
            else
            {
                options.AllowPromotionCodes = true;
            }

            var idempotencyKey = $"orbit-checkout-{userId}-{priceId}-{referralCouponId ?? "std"}";
            var session = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.CheckoutSessions.CreateAsync(
                    options, new RequestOptions { IdempotencyKey = idempotencyKey }, cancellationToken),
                cancellationToken);
            return session.Url;
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to create checkout session", ex);
        }
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken)
    {
        try
        {
            var idempotencyKey = $"orbit-portal-{customerId}";
            var session = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.PortalSessions.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
                {
                    Customer = customerId,
                    ReturnUrl = returnUrl,
                }, new RequestOptions { IdempotencyKey = idempotencyKey }, cancellationToken),
                cancellationToken);
            return session.Url;
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to create portal session", ex);
        }
    }

    public async Task<BillingSubscriptionDetails?> GetSubscriptionDetailsAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            var options = new SubscriptionGetOptions();
            options.AddExpand("default_payment_method");
            var subscription = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Subscriptions.GetAsync(subscriptionId, options, cancellationToken: cancellationToken),
                cancellationToken);

            BillingPaymentMethod? paymentMethod = null;
            if (subscription.DefaultPaymentMethod?.Card is not null)
            {
                var card = subscription.DefaultPaymentMethod.Card;
                paymentMethod = new BillingPaymentMethod(
                    card.Brand ?? "unknown",
                    card.Last4 ?? "****",
                    (int)card.ExpMonth,
                    (int)card.ExpYear);
            }

            var item = subscription.Items?.Data?.FirstOrDefault();
            var intervalString = item?.Price?.Recurring?.Interval;
            var interval = intervalString == "year" ? SubscriptionInterval.Yearly : SubscriptionInterval.Monthly;

            return new BillingSubscriptionDetails(
                subscription.Status,
#pragma warning disable ORBIT0004
                item?.CurrentPeriodEnd ?? DateTime.UtcNow,
#pragma warning restore ORBIT0004
                subscription.CancelAtPeriodEnd,
                interval,
                item?.Price?.UnitAmount ?? 0,
                subscription.Currency ?? "usd",
                paymentMethod);
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to fetch subscription", ex);
        }
    }

    public async Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(string customerId, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var invoices = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Invoices.ListAsync(new InvoiceListOptions
                {
                    Customer = customerId,
                    Limit = limit,
                }, cancellationToken: cancellationToken),
                cancellationToken);

            return invoices.Data.Select(inv => new BillingInvoice(
                inv.Id,
                inv.Created,
                inv.AmountPaid,
                inv.Currency ?? "usd",
                inv.Status ?? "unknown",
                inv.HostedInvoiceUrl,
                inv.InvoicePdf,
                inv.BillingReason ?? "unknown")).ToList();
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to list invoices", ex);
        }
    }

    public async Task<long> GetPriceUnitAmountAsync(string priceId, CancellationToken cancellationToken)
    {
        try
        {
            var price = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Prices.GetAsync(priceId, cancellationToken: cancellationToken),
                cancellationToken);
            return price.UnitAmount ?? 0;
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException($"Failed to fetch price {priceId}", ex);
        }
    }

    public async Task<int?> TryGetCouponPercentOffAsync(string couponId, CancellationToken cancellationToken)
    {
        try
        {
            var coupon = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Coupons.GetAsync(couponId, cancellationToken: cancellationToken),
                cancellationToken);
            return (int)(coupon.PercentOff ?? 0);
        }
        catch (StripeException ex)
        {
            LogCouponFetchFailed(logger, ex, couponId);
            return null;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch coupon {CouponId}")]
    private static partial void LogCouponFetchFailed(ILogger logger, Exception ex, string couponId);
}
