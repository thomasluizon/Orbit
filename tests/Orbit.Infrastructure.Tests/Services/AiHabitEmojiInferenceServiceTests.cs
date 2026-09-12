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
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AiHabitEmojiInferenceServiceTests
{
    [Fact]
    public async Task InferAsync_UsesRealJsonResponseAndAttributesUsageToUser()
    {
        var userId = Guid.NewGuid();
        var habitId = Guid.NewGuid();
        var body = $$$"""
            {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"gpt-test",
             "choices":[{"index":0,"message":{"role":"assistant","content":"{\"emojis\":{\"{{{habitId}}}\":\"💊\"}}"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;
        var handler = new JsonHandler(body);
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiHabitEmojiInferenceService(client, NullLogger<AiHabitEmojiInferenceService>.Instance);

        var result = await service.InferAsync(
            userId,
            [new HabitEmojiInferenceInput(habitId, "Rosuvastatina 10 mg", null)]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain(habitId, "💊");
        handler.LastRequestBody.Should().Contain("Rosuvastatina 10 mg");
        handler.LastRequestBody.Should().Contain("json_object");
        handler.LastRequestBody.Should().NotContain("temperature");
        await usageRecorder.Received(1).RecordAsync(
            "habit_emoji_inference", "subtask-test", 0, 1, 2, 3, Arg.Any<CancellationToken>(), userId);
    }

    private static ChatClient BuildChatClient(HttpMessageHandler handler) =>
        new(
            model: "gpt-test",
            credential: new ApiKeyCredential("test-key"),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler))
            });

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
