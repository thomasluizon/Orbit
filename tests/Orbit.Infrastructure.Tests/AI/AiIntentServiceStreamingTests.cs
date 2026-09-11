using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI;
using OpenAI.Chat;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public class AiIntentServiceStreamingTests
{
    [Fact]
    public async Task SendWithToolsAsync_StreamingTextRound_EmitsDeltasAndReturnsFullText()
    {
        var body = RoleChunk() + ContentChunk("Hel") + ContentChunk("lo!") + FinishChunk("stop") + Done();
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("Hello!");
        result.Value.HasToolCalls.Should().BeFalse();
        sink.Events.Should().SatisfyRespectively(
            first => { first.Kind.Should().Be(AiStreamEventKind.Delta); first.Text.Should().Be("Hel"); },
            second => { second.Kind.Should().Be(AiStreamEventKind.Delta); second.Text.Should().Be("lo!"); });
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamingToolRound_AccumulatesToolCallAcrossChunks()
    {
        var body = RoleChunk()
            + ToolCallStartChunk(0, "call_1", "create_habit")
            + ToolCallArgsChunk(0, """{"title":""")
            + ToolCallArgsChunk(0, """ "Read more"}""")
            + FinishChunk("tool_calls")
            + Done();
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("create it", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasToolCalls.Should().BeTrue();
        result.Value.ConversationContext.Should().NotBeNull();
        var toolCall = result.Value.ToolCalls!.Single();
        toolCall.Name.Should().Be("create_habit");
        toolCall.Id.Should().Be("call_1");
        toolCall.Args.GetProperty("title").GetString().Should().Be("Read more");
        sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SendWithToolsAsync_ContentBeforeToolCalls_EmitsResetAfterDeltas()
    {
        var body = RoleChunk()
            + ContentChunk("Checking that for you")
            + ToolCallStartChunk(0, "call_1", "query_goals")
            + ToolCallArgsChunk(0, "{}")
            + FinishChunk("tool_calls")
            + Done();
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("check goals", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasToolCalls.Should().BeTrue();
        sink.Events.Should().HaveCount(2);
        sink.Events[0].Kind.Should().Be(AiStreamEventKind.Delta);
        sink.Events[^1].Kind.Should().Be(AiStreamEventKind.Reset);
    }

    [Fact]
    public async Task SendWithToolsAsync_MidStreamDrop_ReturnsFailure()
    {
        var prefix = RoleChunk() + ContentChunk("Hel");
        var (service, sink) = BuildService(new DroppingHandler(prefix));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streamSink: sink.Handle);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AI service temporarily unavailable");
    }

    [Fact]
    public async Task SendWithToolsAsync_NullSink_UsesBufferedCompletion()
    {
        const string completion = """
            {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"gpt-test",
             "choices":[{"index":0,"message":{"role":"assistant","content":"Hi there"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;
        var handler = new JsonHandler(completion);
        var (service, sink) = BuildService(handler);

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []));

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("Hi there");
        sink.Events.Should().BeEmpty();
        handler.LastRequestBody.Should().NotContain("\"stream\":true");
        handler.LastRequestBody.Should().NotContain("stream_options");
    }

    [Fact]
    public async Task SendWithToolsAsync_BufferedRound_RecordsUsageForUser()
    {
        var userId = Guid.NewGuid();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var (service, _) = BuildService(new JsonHandler(BufferedCompletion), usageRecorder);

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], userId));

        result.IsSuccess.Should().BeTrue();
        result.Value.ReportedTokenCount.Should().Be(18);
        await usageRecorder.Received(1).RecordAsync(
            "chat", "primary-test", 3, 11, 7, 18, Arg.Any<CancellationToken>(), userId);
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamingRound_RequestsAndRecordsUsageForUser()
    {
        var userId = Guid.NewGuid();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var handler = new SseHandler(
            RoleChunk() + ContentChunk("Hello") + FinishChunk("stop") + UsageChunk() + Done());
        var (service, sink) = BuildService(handler, usageRecorder);

        var result = await service.SendWithToolsAsync(
            new AiToolRequest("hello", "system", [], userId),
            streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReportedTokenCount.Should().Be(18);
        handler.LastRequestBody.Should().Contain("\"stream_options\":{\"include_usage\":true}");
        await usageRecorder.Received(1).RecordAsync(
            "chat", "primary-test", 3, 11, 7, 18, Arg.Any<CancellationToken>(), userId);
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamingRoundWithoutUsage_LogsAndDoesNotRecord()
    {
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var body = RoleChunk() + ContentChunk("Hello") + FinishChunk("stop") + Done();
        var (service, logger) = BuildServiceWithRecordingLogger(new SseHandler(body), usageRecorder);
        var sink = new CollectingSink();

        var result = await service.SendWithToolsAsync(
            new AiToolRequest("hello", "system", [], Guid.NewGuid()),
            streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        logger.WarningEventIds.Should().Contain(11);
        await usageRecorder.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default!, default, default, default, default, default, default);
    }

    [Fact]
    public async Task SendWithToolsAsync_RecorderFailure_DoesNotFailCompletedStream()
    {
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        usageRecorder.RecordAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(Task.FromException(new InvalidOperationException("recorder failed")));
        var body = RoleChunk() + ContentChunk("Hello") + FinishChunk("stop") + UsageChunk() + Done();
        var (service, logger) = BuildServiceWithRecordingLogger(new SseHandler(body), usageRecorder);
        var sink = new CollectingSink();

        var result = await service.SendWithToolsAsync(
            new AiToolRequest("hello", "system", [], Guid.NewGuid()),
            streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("Hello");
        sink.Events.Should().ContainSingle();
        logger.WarningEventIds.Should().Contain(12);
    }

    [Fact]
    public async Task ContinueWithToolResultsAsync_TwoRounds_RecordTwoCallsForSameUser()
    {
        var userId = Guid.NewGuid();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var handler = new SequenceSseHandler(
            RoleChunk()
                + ToolCallStartChunk(0, "call_1", "create_habit")
                + ToolCallArgsChunk(0, "{}")
                + FinishChunk("tool_calls")
                + UsageChunk()
                + Done(),
            RoleChunk() + ContentChunk("Done") + FinishChunk("stop") + UsageChunk() + Done());
        var (service, sink) = BuildService(handler, usageRecorder);

        var first = await service.SendWithToolsAsync(
            new AiToolRequest("create it", "system", [], userId),
            streamSink: sink.Handle);
        var second = await service.ContinueWithToolResultsAsync(
            first.Value.ConversationContext!,
            [new AiToolCallResult("create_habit", "call_1", true, null, null, null)],
            streamSink: sink.Handle);

        second.IsSuccess.Should().BeTrue();
        second.Value.TextMessage.Should().Be("Done");
        await usageRecorder.Received(2).RecordAsync(
            "chat", "primary-test", 3, 11, 7, 18, Arg.Any<CancellationToken>(), userId);
    }

    [Fact]
    public async Task ContinueWithToolResultsAsync_OversizedPayload_SendsValidDropMarker()
    {
        var handler = new SequenceSseHandler(
            RoleChunk()
                + ToolCallStartChunk(0, "call_1", "list_tags")
                + ToolCallArgsChunk(0, "{}")
                + FinishChunk("tool_calls")
                + UsageChunk()
                + Done(),
            RoleChunk() + ContentChunk("Use a narrower filter") + FinishChunk("stop") + UsageChunk() + Done());
        var (service, sink) = BuildService(handler);
        var first = await service.SendWithToolsAsync(
            new AiToolRequest("list tags", "system", [], Guid.NewGuid()),
            streamSink: sink.Handle);
        var oversizedPayload = Enumerable.Range(0, 2_000)
            .Select(index => new { id = Guid.NewGuid(), name = $"tag-{index:D4}-{new string('x', 20)}" })
            .ToList();

        var second = await service.ContinueWithToolResultsAsync(
            first.Value.ConversationContext!,
            [new AiToolCallResult("list_tags", "call_1", true, null, null, null, oversizedPayload)],
            streamSink: sink.Handle);

        second.IsSuccess.Should().BeTrue();
        handler.RequestBodies.Should().HaveCount(2);
        using var requestDocument = JsonDocument.Parse(handler.RequestBodies[1]);
        var toolMessage = requestDocument.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Single(message => message.GetProperty("role").GetString() == "tool");
        var toolContent = toolMessage.GetProperty("content").GetString();
        toolContent.Should().NotBeNull();
        toolContent!.Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        using var toolDocument = JsonDocument.Parse(toolContent);
        var payload = toolDocument.RootElement.GetProperty("payload");
        payload.GetProperty("dropped").GetBoolean().Should().BeTrue();
        payload.GetProperty("original_length").GetInt32().Should().BeGreaterThan(AppConstants.MaxAiToolPayloadJsonLength);
        payload.GetProperty("instruction").GetString().Should().Contain("narrower filter");
        toolContent.Should().NotContain("tag-1999");
    }

    [Fact]
    public async Task SendWithToolsAsync_BufferedLengthFinish_LogsTruncationWarningAndKeepsText()
    {
        const string completion = """
            {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"gpt-test",
             "choices":[{"index":0,"message":{"role":"assistant","content":"partial list"},"finish_reason":"length"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;
        var (service, logger) = BuildServiceWithRecordingLogger(new JsonHandler(completion));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []));

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("partial list");
        result.Value.IsTruncated.Should().BeTrue();
        logger.WarningEventIds.Should().Contain(8);
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamingLengthFinish_LogsTruncationWarningAndKeepsText()
    {
        var body = RoleChunk() + ContentChunk("partial") + FinishChunk("length") + Done();
        var (service, logger) = BuildServiceWithRecordingLogger(new SseHandler(body));
        var sink = new CollectingSink();

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("partial");
        result.Value.IsTruncated.Should().BeTrue();
        logger.WarningEventIds.Should().Contain(8);
    }

    [Fact]
    public async Task SendWithToolsAsync_WithUserId_SetsEndUserIdForCacheRouting()
    {
        var handler = new JsonHandler(BufferedCompletion);
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiIntentService(aiClient, usageRecorder, NullLogger<AiIntentService>.Instance);
        var userId = Guid.NewGuid();

        await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], userId));

        handler.LastRequestBody.Should().Contain(userId.ToString("N"));
    }

    [Fact]
    public async Task SendWithToolsAsync_HistoryWithinWindow_DoesNotSummarize()
    {
        var handler = new CountingJsonHandler(BufferedCompletion);
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiIntentService(aiClient, usageRecorder, NullLogger<AiIntentService>.Instance);

        await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], Guid.NewGuid(), History: BuildHistory(40)));

        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task SendWithToolsAsync_HistoryAboveControllerLimit_DoesNotIssueSummaryCall()
    {
        var handler = new CountingJsonHandler(BufferedCompletion);
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiIntentService(aiClient, usageRecorder, NullLogger<AiIntentService>.Instance);

        await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], Guid.NewGuid(), History: BuildHistory(50)));

        handler.RequestCount.Should().Be(1);
    }

    private const string BufferedCompletion = """
        {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"gpt-test",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hi there"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18,"prompt_tokens_details":{"cached_tokens":3}}}
        """;

    private static List<ChatHistoryMessage> BuildHistory(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "user" : "assistant", $"message {i}"))
            .ToList();

    [Fact]
    public async Task SendWithToolsAsync_Streaming_RequestsUsageInStreamOptions()
    {
        var handler = new SseHandler(RoleChunk() + ContentChunk("hi") + FinishChunk("stop") + Done());
        var (service, sink) = BuildService(handler);

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        using var request = JsonDocument.Parse(handler.LastRequestBody!);
        request.RootElement.TryGetProperty("stream_options", out var streamOptions).Should().BeTrue(
            "the SDK emits stream_options for a streamed round; if a future version stops, per-user token accounting silently records nothing");
        streamOptions.GetProperty("include_usage").GetBoolean().Should().BeTrue();
    }

    private static (AiIntentService Service, CollectingSink Sink) BuildService(
        HttpMessageHandler handler,
        IAiUsageRecorder? usageRecorder = null)
    {
        usageRecorder ??= Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiIntentService(aiClient, usageRecorder, NullLogger<AiIntentService>.Instance);
        return (service, new CollectingSink());
    }

    private static (AiIntentService Service, RecordingLogger Logger) BuildServiceWithRecordingLogger(
        HttpMessageHandler handler,
        IAiUsageRecorder? usageRecorder = null)
    {
        usageRecorder ??= Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var logger = new RecordingLogger();
        var service = new AiIntentService(aiClient, usageRecorder, logger);
        return (service, logger);
    }

    private static ChatClient BuildChatClient(HttpMessageHandler handler) =>
        new(
            model: "gpt-test",
            credential: new ApiKeyCredential("test-key"),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });

    private static string Chunk(string deltaJson, string finishReason = "null")
    {
        return "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
               $"\"model\":\"gpt-test\",\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{finishReason}}}]}}\n\n";
    }

    private static string RoleChunk() => Chunk("""{"role":"assistant","content":""}""");

    private static string ContentChunk(string text) => Chunk($"{{\"content\":{JsonSerializer.Serialize(text)}}}");

    private static string ToolCallStartChunk(int index, string id, string name)
    {
        return Chunk($"{{\"tool_calls\":[{{\"index\":{index},\"id\":\"{id}\",\"type\":\"function\"," +
                     $"\"function\":{{\"name\":\"{name}\",\"arguments\":\"\"}}}}]}}");
    }

    private static string ToolCallArgsChunk(int index, string argsFragment)
    {
        return Chunk($"{{\"tool_calls\":[{{\"index\":{index}," +
                     $"\"function\":{{\"arguments\":{JsonSerializer.Serialize(argsFragment)}}}}}]}}");
    }

    private static string FinishChunk(string reason) => Chunk("{}", $"\"{reason}\"");

    private static string UsageChunk() =>
        "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
        "\"model\":\"gpt-test\",\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7," +
        "\"total_tokens\":18,\"prompt_tokens_details\":{\"cached_tokens\":3}}}\n\n";

    private static string Done() => "data: [DONE]\n\n";

    private sealed class CollectingSink
    {
        public List<AiStreamEvent> Events { get; } = [];

        public Task Handle(AiStreamEvent streamEvent)
        {
            Events.Add(streamEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<AiIntentService>
    {
        public List<int> WarningEventIds { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                WarningEventIds.Add(eventId.Id);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
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

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class SequenceSseHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);
        public List<string> RequestBodies { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                RequestBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return BuildResponse(request);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return BuildResponse(request);
        }

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
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

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class CountingJsonHandler(string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Build(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(Build(request));
        }

        private HttpResponseMessage Build(HttpRequestMessage request)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class DroppingHandler(string prefix) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => BuildResponse(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(BuildResponse(request));

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StreamContent(new DroppingStream(Encoding.UTF8.GetBytes(prefix)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class DroppingStream(byte[] prefix) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served)
                throw new IOException("connection reset");

            _served = true;
            var copied = Math.Min(count, prefix.Length);
            Array.Copy(prefix, 0, buffer, offset, copied);
            return copied;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
