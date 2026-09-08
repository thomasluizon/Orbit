using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Subscriptions;

internal sealed class PlayBillingTestClient : IDisposable
{
    private readonly AndroidPublisherService _publisher;

    public IPlayBillingService Billing { get; }

    public PlayBillingTestClient(string subscriptionState, DateTime expiresAt, Guid userId,
        IOptions<GooglePlaySettings> settings)
    {
        var json = JsonSerializer.Serialize(new
        {
            subscriptionState,
            acknowledgementState = "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
            externalAccountIdentifiers = new { obfuscatedExternalAccountId = userId.ToString() },
            lineItems = new[]
            {
                new
                {
                    productId = "orbit_pro",
                    expiryTime = new SubscriptionPurchaseLineItem { ExpiryTimeDateTimeOffset = expiresAt }.ExpiryTimeRaw,
                    offerDetails = new { basePlanId = "monthly" },
                },
            },
        });
        _publisher = new AndroidPublisherService(new BaseClientService.Initializer
        {
            HttpClientFactory = new ResponseFactory(json),
            ApplicationName = "Orbit.Tests",
        });
        Billing = new GooglePlayBillingService(_publisher, settings);
    }

    public void Dispose() => _publisher.Dispose();

    private sealed class ResponseFactory(string json) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new ResponseHandler(json);
    }

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
