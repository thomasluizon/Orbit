using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI;
using OpenAI.Chat;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Tests.AI;

public class AiCompletionClientTests
{
    [Fact]
    public async Task CompleteJsonAsync_SubTaskTier_OmitsTemperature()
    {
        var handler = new CapturingHandler();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);

        await client.CompleteJsonAsync<Probe>("You are a helpful assistant. Respond only with valid JSON.", "extract facts", purpose: "fact_extraction", tier: AiModelTier.SubTask);

        handler.LastRequestBody.Should().NotBeNull();
        handler.LastRequestBody.Should().NotContain("temperature");
        await usageRecorder.Received(1).RecordAsync(
            "fact_extraction", "subtask-test", 0, 1, 2, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteJsonAsync_PrimaryTier_SendsTemperature()
    {
        var handler = new CapturingHandler();
        var client = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());

        await client.CompleteJsonAsync<Probe>("You are a helpful assistant. Respond only with valid JSON.", "extract facts", temperature: 0.1, purpose: "json", tier: AiModelTier.Primary);

        handler.LastRequestBody.Should().Contain("temperature");
    }

    [Fact]
    public async Task CompleteJsonAsync_WithUserId_AttributesRecordedUsage()
    {
        var handler = new CapturingHandler();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var userId = Guid.NewGuid();

        await client.CompleteJsonAsync<Probe>("system", "user", userId: userId);

        await usageRecorder.Received(1).RecordAsync(
            "json", "primary-test", 0, 1, 2, 3, Arg.Any<CancellationToken>(), userId);
    }

    [Fact]
    public void ResolveSubTaskModel_EmptyConfig_FallsBackToPrimary()
    {
        AiCompletionClient.ResolveSubTaskModel("", "gpt-4.1-mini").Should().Be("gpt-4.1-mini");
        AiCompletionClient.ResolveSubTaskModel("   ", "gpt-4.1-mini").Should().Be("gpt-4.1-mini");
    }

    [Fact]
    public void ResolveSubTaskModel_ConfiguredModel_IsUsed()
    {
        AiCompletionClient.ResolveSubTaskModel("gpt-5.4-nano", "gpt-4.1-mini").Should().Be("gpt-5.4-nano");
    }

    [Theory]
    [InlineData(AiModelTier.Primary, "gpt-4.1-mini", "gpt-5.4-nano", true)]
    [InlineData(AiModelTier.SubTask, "gpt-4.1-mini", "gpt-5.4-nano", false)]
    [InlineData(AiModelTier.SubTask, "gpt-4.1-mini", "gpt-4.1-mini", true)]
    public void ShouldApplyTemperature_SuppressedOnlyForDistinctSubTaskModel(
        AiModelTier tier, string primary, string subTask, bool expected)
        => AiCompletionClient.ShouldApplyTemperature(tier, primary, subTask).Should().Be(expected);

    private static ChatClient BuildChatClient(HttpMessageHandler handler) =>
        new(
            model: "gpt-test",
            credential: new ApiKeyCredential("test-key"),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });

    private sealed record Probe(string Value);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string Body = """
            {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"gpt-test",
             "choices":[{"index":0,"message":{"role":"assistant","content":"{\"value\":\"ok\"}"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;

        public string? LastRequestBody { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return BuildResponse(request);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return BuildResponse(request);
        }

        private static HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(Body, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }
}
