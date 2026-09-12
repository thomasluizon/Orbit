using System.Text.Json;

namespace Orbit.Domain.Models;

public record AiToolCall(string Name, string Id, JsonElement Args);

/// <summary>
/// A single tool-enabled AI turn: the user message, system prompt, and tool declarations plus the
/// optional per-request routing (<see cref="UserId"/>), multimodal (<see cref="ImageData"/> /
/// <see cref="ImageMimeType"/>), and prior <see cref="History"/> inputs. Bundled so the streaming
/// sink and cancellation token stay as the only standalone arguments to SendWithToolsAsync.
/// </summary>
public sealed record AiToolRequest(
    string UserMessage,
    string SystemPrompt,
    IReadOnlyList<object> ToolDeclarations,
    Guid UserId = default,
    byte[]? ImageData = null,
    string? ImageMimeType = null,
    IReadOnlyList<ChatHistoryMessage>? History = null);

public record AiToolCallResult(
    string Name,
    string Id,
    bool Success,
    string? EntityId,
    string? EntityName,
    string? Error,
    object? Payload = null);

/// <summary>
/// Opaque conversation state passed between SendWithToolsAsync and ContinueWithToolResultsAsync.
/// Eliminates the need for mutable instance fields on the service.
/// </summary>
public sealed class AiConversationContext
{
    /// <summary>Opaque message list. Only consumed by AiIntentService.</summary>
    public object Messages { get; init; } = null!;
    /// <summary>Opaque options object. Only consumed by AiIntentService.</summary>
    public object Options { get; init; } = null!;
    /// <summary>User attributed to each model round in this conversation.</summary>
    public Guid? UserId { get; init; }
}

public record AiResponse
{
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }
    public string? TextMessage { get; init; }
    public int ReportedTokenCount { get; init; }
    public bool IsTruncated { get; init; }
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
    public AiConversationContext? ConversationContext { get; init; }
}
