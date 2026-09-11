using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

public sealed partial class AiCompletionClient
{
    private readonly ChatClient _chatClient;
    private readonly ChatClient _subTaskChatClient;
    private readonly string _primaryModel;
    private readonly string _subTaskModel;
    private readonly ILogger<AiCompletionClient> _logger;
    private readonly IAiUsageRecorder _usageRecorder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AiCompletionClient(
        IOptions<AiSettings> options,
        ILogger<AiCompletionClient> logger,
        IAiUsageRecorder usageRecorder)
    {
        _logger = logger;
        _usageRecorder = usageRecorder;
        var settings = options.Value;

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(settings.BaseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(settings.NetworkTimeoutSeconds),
            RetryPolicy = new AiRetryLoggingPolicy(settings.MaxRetries, logger)
        };
        var credential = new ApiKeyCredential(settings.ApiKey);

        _primaryModel = settings.Model;
        _chatClient = new ChatClient(model: settings.Model, credential: credential, options: clientOptions);

        _subTaskModel = ResolveSubTaskModel(settings.SubTaskModel, settings.Model);
        _subTaskChatClient = _subTaskModel == settings.Model
            ? _chatClient
            : new ChatClient(model: _subTaskModel, credential: credential, options: clientOptions);
    }

    internal static string ResolveSubTaskModel(string subTaskModel, string primaryModel)
        => string.IsNullOrWhiteSpace(subTaskModel) ? primaryModel : subTaskModel;

    /// <summary>
    /// The sub-task temperature is suppressed only when the sub-task tier actually routes to a distinct
    /// reasoning model (which rejects a custom temperature). When the kill-switch aliases the sub-task
    /// tier back to the primary model, the configured temperature must still be sent.
    /// </summary>
    internal static bool ShouldApplyTemperature(AiModelTier tier, string primaryModel, string subTaskModel)
        => tier == AiModelTier.Primary || subTaskModel == primaryModel;

    internal AiCompletionClient(
        ChatClient chatClient,
        ILogger<AiCompletionClient> logger,
        IAiUsageRecorder usageRecorder)
    {
        _chatClient = chatClient;
        _subTaskChatClient = chatClient;
        _primaryModel = "primary-test";
        _subTaskModel = "subtask-test";
        _logger = logger;
        _usageRecorder = usageRecorder;
    }

    /// <summary>
    /// Direct access to the underlying ChatClient for advanced scenarios (tool calling, multi-turn).
    /// </summary>
    public ChatClient ChatClient => _chatClient;

    internal string ChatModel => _primaryModel;

    /// <summary>
    /// Requests a plain-text chat completion from the configured model tier.
    /// </summary>
    /// <remarks>
    /// Error-handling contract: propagates exceptions on API, network, or cancellation failures - the
    /// caller is the trust boundary and must handle them. Returns <c>null</c> only when the model
    /// yields no content, never to signal a failure.
    /// </remarks>
    public async Task<string?> CompleteTextAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.7,
        int? maxOutputTokens = null,
        string purpose = "text",
        AiModelTier tier = AiModelTier.Primary,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions();
        if (ShouldApplyTemperature(tier, _primaryModel, _subTaskModel))
            options.Temperature = (float)temperature;

        if (maxOutputTokens is int max)
            options.MaxOutputTokenCount = max;

        LogCallingTextCompletion(_logger);

        var client = tier == AiModelTier.SubTask ? _subTaskChatClient : _chatClient;
        var model = tier == AiModelTier.SubTask ? _subTaskModel : _primaryModel;
        var completion = await client.CompleteChatAsync(messages, options, cancellationToken);
        await RecordUsageAsync(completion.Value.Usage, purpose, model, cancellationToken);
        var text = completion.Value.Content.FirstOrDefault()?.Text;

        if (!string.IsNullOrWhiteSpace(text))
            LogTextCompletionSuccessful(_logger, text.Length);

        return text;
    }

    /// <summary>
    /// Requests a JSON chat completion and deserializes it into <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Error-handling contract: propagates exceptions on API, network, cancellation, or JSON
    /// deserialization failures - the caller is the trust boundary and must handle them. Returns
    /// <c>default</c> only when the model yields no content, never to signal a failure.
    /// </remarks>
    public async Task<T?> CompleteJsonAsync<T>(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.1,
        int? maxOutputTokens = null,
        string purpose = "json",
        AiModelTier tier = AiModelTier.Primary,
        Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };
        if (ShouldApplyTemperature(tier, _primaryModel, _subTaskModel))
            options.Temperature = (float)temperature;

        if (maxOutputTokens is int max)
            options.MaxOutputTokenCount = max;

        LogCallingJsonCompletion(_logger);

        var client = tier == AiModelTier.SubTask ? _subTaskChatClient : _chatClient;
        var model = tier == AiModelTier.SubTask ? _subTaskModel : _primaryModel;
        var completion = await client.CompleteChatAsync(messages, options, cancellationToken);
        await RecordUsageAsync(completion.Value.Usage, purpose, model, cancellationToken, userId);
        var text = completion.Value.Content.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            LogEmptyJsonResponse(_logger);
            return default;
        }

        LogJsonCompletionSuccessful(_logger, text.Length);

        return JsonSerializer.Deserialize<T>(text, JsonOptions);
    }

    private async Task RecordUsageAsync(
        ChatTokenUsage? usage,
        string purpose,
        string model,
        CancellationToken cancellationToken,
        Guid? userId = null)
    {
        if (usage is null)
            return;

        var cachedTokens = usage.InputTokenDetails?.CachedTokenCount ?? 0;

        LogAiTokenUsage(
            _logger,
            purpose,
            model,
            cachedTokens,
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount);

        await _usageRecorder.RecordAsync(
            purpose,
            model,
            cachedTokens,
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount,
            cancellationToken,
            userId);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Calling AI API for text completion...")]
    private static partial void LogCallingTextCompletion(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "AI text completion successful ({Length} chars)")]
    private static partial void LogTextCompletionSuccessful(ILogger logger, int length);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Calling AI API for JSON completion...")]
    private static partial void LogCallingJsonCompletion(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "AI returned empty response for JSON completion")]
    private static partial void LogEmptyJsonResponse(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "AI JSON completion successful ({Length} chars)")]
    private static partial void LogJsonCompletionSuccessful(ILogger logger, int length);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "AI token usage ({Purpose} via {Model}): cached={CachedTokens}, prompt={PromptTokens}, completion={CompletionTokens}, total={TotalTokens}")]
    private static partial void LogAiTokenUsage(ILogger logger, string purpose, string model, int cachedTokens, int promptTokens, int completionTokens, int totalTokens);

}
