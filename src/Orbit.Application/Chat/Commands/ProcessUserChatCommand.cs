using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace Orbit.Application.Chat.Commands;

public record ProcessUserChatCommand(
    Guid UserId,
    string Message,
    byte[]? ImageData = null,
    string? ImageMimeType = null,
    List<ChatHistoryMessage>? History = null,
    AgentClientContext? ClientContext = null,
    string? ConfirmationToken = null,
    AgentAuthMethod AuthMethod = AgentAuthMethod.Jwt,
    IReadOnlyList<string>? GrantedScopes = null,
    bool IsReadOnlyCredential = false,
    string? CorrelationId = null,
    Func<ChatStreamEvent, Task>? StreamSink = null) : IRequest<Result<ChatResponse>>;

public record ChatResponse(
    string? AiMessage,
    IReadOnlyList<ActionResult> Actions,
    IReadOnlyList<AgentOperationResult>? Operations = null,
    IReadOnlyList<PendingAgentOperation>? PendingOperations = null,
    IReadOnlyList<AgentPolicyDenial>? PolicyDenials = null,
    string? CorrelationId = null,
    IReadOnlyList<string>? RelatedSurfaces = null,
    HabitListCard? HabitList = null,
    GoalListCard? GoalList = null,
    MetricsCard? MetricsCard = null);

public record ActionResult(
    string Type,
    ActionStatus Status,
    Guid? EntityId = null,
    string? EntityName = null,
    string? Error = null,
    string? Field = null,
    IReadOnlyList<AiAction>? SuggestedSubHabits = null,
    ClarificationRequest? ClarificationRequest = null);

public enum ActionStatus { Success, Failed, Suggestion, NeedsClarification }

/// <summary>
/// Groups AI-related dependencies to reduce constructor parameter count (S107).
/// </summary>
public record ChatAiDependencies(
    IAiIntentService IntentService,
    AiToolRegistry ToolRegistry,
    ISystemPromptBuilder PromptBuilder,
    IAgentCatalogService CatalogService);

/// <summary>
/// Groups data repository dependencies to reduce constructor parameter count (S107).
/// </summary>
public record ChatDataDependencies(
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<Goal> GoalRepository,
    IGenericRepository<User> UserRepository,
    IGenericRepository<UserFact> UserFactRepository,
    IGenericRepository<Tag> TagRepository,
    IGenericRepository<ChecklistTemplate> ChecklistTemplateRepository,
    IFeatureFlagService FeatureFlagService);

/// <summary>
/// Groups workflow services to reduce constructor parameter count (S107).
/// </summary>
public record ChatExecutionDependencies(
    IUserDateService UserDateService,
    IUserStreakService UserStreakService,
    IPayGateService PayGateService,
    IUnitOfWork UnitOfWork,
    IServiceScopeFactory ServiceScopeFactory,
    IAgentOperationExecutor OperationExecutor,
    IPendingClarificationStore PendingClarificationStore,
    IGoalProgressReadSyncer GoalProgressReadSyncer,
    IGamificationService GamificationService,
    IMediator Mediator,
    IProductAnalytics ProductAnalytics);

public partial class ProcessUserChatCommandHandler(
    ChatDataDependencies data,
    ChatAiDependencies ai,
    ChatExecutionDependencies execution,
    ILogger<ProcessUserChatCommandHandler> logger) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    private const int MaxToolIterations = 5;
    private const string UnsupportedByPolicyReason = "unsupported_by_policy";
    private const string DescribeFeatureToolName = "describe_feature";
    private const string EnglishToolFailureMessage = "I couldn't complete that. Please try again.";
    private const string PortugueseToolFailureMessage = "Não consegui concluir isso. Tente novamente.";
    private const string EnglishTruncationMessage = "This response was cut off before completion, so the result is partial.";
    private const string PortugueseTruncationMessage = "Esta resposta foi interrompida antes da conclusão, então o resultado é parcial.";

    private const int MaxSupportMessageLength = 5000;

    public async Task<Result<ChatResponse>> Handle(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        LogProcessingChatMessage(logger, request.Message);

        var contextResult = await LoadChatContextAsync(request, cancellationToken);
        if (contextResult.IsFailure)
            return contextResult.PropagateError<ChatResponse>();

        var context = contextResult.Value;
        var userLanguage = GetUserLanguage(context.User);
        var aiStreamFilter = BuildAiStreamFilter(request.StreamSink);
        Func<AiStreamEvent, Task>? aiStreamSink = aiStreamFilter is null
            ? null
            : aiStreamFilter.HandleAsync;

        var faqMatch = ChatFaqCache.TryMatchFaqKey(request.Message);
        if (faqMatch is { } faqHit && ChatFaqCache.TryGetAnswer(faqHit.Key, faqHit.Locale, out var cachedFaqAnswer))
            return Result.Success(new ChatResponse(cachedFaqAnswer, [], CorrelationId: request.CorrelationId));

        var skipTools = request.ImageData is null
            && request.ConfirmationToken is null
            && (request.History is null || request.History.Count == 0)
            && ChatIntentRouter.IsNoToolTurn(request.Message);

        var aiStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await RequestInitialAiResponseAsync(request, context, aiStreamSink, skipTools, cancellationToken);
        aiStopwatch.Stop();
        LogAiIntentServiceCompleted(logger, aiStopwatch.ElapsedMilliseconds);

        if (response.IsFailure)
            return response.PropagateError<ChatResponse>();

        var executionResults = new ToolExecutionAccumulator();
        var actionsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var toolLoopResult = await RunToolCallLoopAsync(
            response.Value,
            request,
            executionResults,
            userLanguage,
            aiStreamSink,
            cancellationToken);
        var aiResponse = toolLoopResult.FinalResponse;
        var iterations = toolLoopResult.Iterations;
        if (toolLoopResult.HadToolFailure)
            executionResults.SanitizeFailedActions(ToolFailureMessage(userLanguage));
        if (aiStreamFilter is not null)
            await aiStreamFilter.FlushAsync();
        actionsStopwatch.Stop();
        LogToolExecutionCompleted(logger, actionsStopwatch.ElapsedMilliseconds, iterations, executionResults.ActionResults.Count);

        LogSavingChanges(logger);
        var saveStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await PersistExecutionResultsAsync(request.UserId, executionResults.ActionResults, cancellationToken);
        saveStopwatch.Stop();
        LogChangesSaved(logger, saveStopwatch.ElapsedMilliseconds);

        if (toolLoopResult.TokenBudgetExceeded && string.IsNullOrWhiteSpace(aiResponse.TextMessage))
            return Result.Failure<ChatResponse>(ErrorMessages.AiUnavailable);

        var responseText = StripJsonWrapper(aiResponse.TextMessage);
        if (aiResponse.IsTruncated)
            responseText = AppendTruncationNotice(responseText, userLanguage);
        var (aiMessage, habitList, goalList, metricsCard) = await BuildResponseCardsAsync(
            responseText, request, context, cancellationToken);

        if (faqMatch is { } faqToCache
            && !string.IsNullOrWhiteSpace(aiMessage)
            && IsShareableFaqTurn(executionResults, metricsCard))
        {
            ChatFaqCache.StoreAnswer(faqToCache.Key, faqToCache.Locale, aiMessage);
        }

        if (metricsCard is not null && context.User is not null)
        {
            AnalyticsCapture.SafeCaptureUserEvent(
                execution.ProductAnalytics,
                logger,
                request.UserId,
                context.User.Plan.ToString(),
                "chat_metrics_card_emitted",
                new Dictionary<string, object>
                {
                    ["platform"] = request.ClientContext?.Platform ?? "unknown",
                    ["chip_present"] = !string.IsNullOrWhiteSpace(metricsCard.SurfaceId)
                });
        }

        RunBackgroundPostResponseWork(
            request.UserId,
            request.Message,
            aiMessage,
            shouldExtractFacts: context.AiMemoryEnabled && context.User is not null && context.User.HasProAccess,
            existingFacts: context.UserFacts);

        totalStopwatch.Stop();
        LogTotalRequestProcessingTime(logger, totalStopwatch.ElapsedMilliseconds);
        LogContextLoadingTime(logger, context.ContextLoadMilliseconds);
        LogAiServiceTime(logger, aiStopwatch.ElapsedMilliseconds);
        LogToolExecutionTime(logger, actionsStopwatch.ElapsedMilliseconds, iterations);
        LogSaveChangesTime(logger, saveStopwatch.ElapsedMilliseconds);

        return Result.Success(new ChatResponse(
            aiMessage,
            executionResults.ActionResults,
            executionResults.OperationResults,
            executionResults.PendingOperations,
            executionResults.PolicyDenials,
            request.CorrelationId,
            executionResults.RelatedSurfaces.Count > 0 ? executionResults.RelatedSurfaces : null,
            habitList,
            goalList,
            metricsCard));
    }

    private static string? GetUserLanguage(User? user) => user?.Language;

    private static string AppendTruncationNotice(string? text, string? language)
    {
        var notice = LocaleHelper.IsPortuguese(language)
            ? PortugueseTruncationMessage
            : EnglishTruncationMessage;
        return string.IsNullOrWhiteSpace(text) ? notice : $"{text}\n\n{notice}";
    }

    private async Task<(string? AiMessage, HabitListCard? HabitList, GoalListCard? GoalList, MetricsCard? MetricsCard)> BuildResponseCardsAsync(
        string? aiMessage,
        ProcessUserChatCommand request,
        ChatContext context,
        CancellationToken cancellationToken)
    {
        HabitListCard? habitList = null;
        if (HabitListCardBuilder.TryExtractScope(aiMessage, out var habitListScope, out var strippedMessage))
        {
            aiMessage = strippedMessage;
            if (request.ClientContext?.SupportsHabitListCard == true)
                habitList = HabitListCardBuilder.Build(context.ActiveHabits, context.UserToday, habitListScope);
        }

        GoalListCard? goalList = null;
        if (GoalListCardBuilder.TryExtractDirective(aiMessage, out var strippedGoalMessage))
        {
            aiMessage = strippedGoalMessage;
            if (request.ClientContext?.SupportsGoalListCard == true)
                goalList = GoalListCardBuilder.Build(context.ActiveGoals);
        }

        MetricsCard? metricsCard = null;
        if (MetricsCardBuilder.TryExtractDirective(aiMessage, out var strippedMetricsMessage))
        {
            aiMessage = strippedMetricsMessage;
            if (request.ClientContext?.SupportsMetricsCard == true)
                metricsCard = await TryBuildMetricsCardAsync(request.UserId, context.UserToday, cancellationToken);
        }

        return (aiMessage, habitList, goalList, metricsCard);
    }

    private async Task<MetricsCard?> TryBuildMetricsCardAsync(
        Guid userId,
        DateOnly userToday,
        CancellationToken cancellationToken)
    {
        try
        {
            var weekStartDay = await execution.UserDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
            var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve("week", userToday, weekStartDay);
            var recapResult = await execution.Mediator.Send(
                new GetRecapQuery(userId, dateFrom, dateTo, "week"),
                cancellationToken);

            if (recapResult.IsFailure)
            {
                LogMetricsCardBuildFailed(logger, recapResult.Error);
                return null;
            }

            return MetricsCardBuilder.Build(recapResult.Value.Period, recapResult.Value.Metrics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMetricsCardBuildThrew(logger, ex);
            return null;
        }
    }

    /// <summary>
    /// A FAQ answer is safe to cache and share across users only when the turn raised no pending
    /// confirmation and every tool it ran (if any) was a successful describe_feature — a static,
    /// user-data-free lookup. Any write or user-specific read makes the answer unshareable.
    /// </summary>
    internal static bool IsShareableFaqTurn(
        ToolExecutionAccumulator results,
        MetricsCard? metricsCard = null) =>
        metricsCard is null
        && results.PendingOperations.Count == 0
        && results.CalledToolNames.All(name => name == DescribeFeatureToolName)
        && results.OperationResults.All(operation => operation.Status == AgentOperationStatus.Succeeded);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Processing chat message: '{Message}'")]
    private static partial void LogProcessingChatMessage(ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Context loaded in {ElapsedMs}ms (Habits: {HabitCount}, Facts: {FactCount})")]
    private static partial void LogContextLoaded(ILogger logger, long elapsedMs, int habitCount, int factCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Calling AI intent service with {ToolCount} tools...")]
    private static partial void LogCallingAiIntentService(ILogger logger, int toolCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "AI intent service completed in {ElapsedMs}ms")]
    private static partial void LogAiIntentServiceCompleted(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Tool-calling iteration {Iteration}, {CallCount} calls")]
    private static partial void LogToolCallingIteration(ILogger logger, int iteration, int callCount);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Unknown tool requested: {Name}")]
    private static partial void LogUnknownToolRequested(ILogger logger, string name);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Tool {Name} succeeded: {EntityName}")]
    private static partial void LogToolSucceeded(ILogger logger, string name, string? entityName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Tool {Name} failed: {Error}")]
    private static partial void LogToolFailed(ILogger logger, string name, string? error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Tool {Name} threw an exception")]
    private static partial void LogToolThrewException(ILogger logger, Exception ex, string name);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "ContinueWithToolResultsAsync failed: {Error}")]
    private static partial void LogContinueWithToolResultsFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "Tool execution completed in {ElapsedMs}ms ({Iterations} iterations, {ActionCount} actions)")]
    private static partial void LogToolExecutionCompleted(ILogger logger, long elapsedMs, int iterations, int actionCount);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "Changes saved in {ElapsedMs}ms")]
    private static partial void LogChangesSaved(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "TOTAL request processing time: {ElapsedMs}ms")]
    private static partial void LogTotalRequestProcessingTime(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug, Message = "   Context loading: {DbMs}ms")]
    private static partial void LogContextLoadingTime(ILogger logger, long dbMs);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug, Message = "   AI service: {AiMs}ms")]
    private static partial void LogAiServiceTime(ILogger logger, long aiMs);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug, Message = "   Tool execution: {ActionsMs}ms ({Iterations} iterations)")]
    private static partial void LogToolExecutionTime(ILogger logger, long actionsMs, int iterations);

    [LoggerMessage(EventId = 18, Level = LogLevel.Debug, Message = "   Save changes: {SaveMs}ms")]
    private static partial void LogSaveChangesTime(ILogger logger, long saveMs);

    [LoggerMessage(EventId = 19, Level = LogLevel.Debug, Message = "Fetching context from database...")]
    private static partial void LogFetchingContext(ILogger logger);

    [LoggerMessage(EventId = 20, Level = LogLevel.Debug, Message = "Saving changes to database...")]
    private static partial void LogSavingChanges(ILogger logger);

    [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "Tool {Name} requested clarification (operationId={OperationId}, missing={MissingKey})")]
    private static partial void LogClarificationRequested(ILogger logger, string name, Guid operationId, string missingKey);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Tool {Name} emitted a clarification payload on a Failed/Denied result and it was dropped: {Reason}")]
    private static partial void LogClarificationDroppedOnFailedTool(ILogger logger, string name, string? reason);

    [LoggerMessage(EventId = 26, Level = LogLevel.Warning, Message = "Tool {Name} requested clarification with oversized partial args ({Length} chars) — dropped without stashing")]
    private static partial void LogClarificationArgsTooLarge(ILogger logger, string name, int length);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Background post-response work failed")]
    private static partial void LogBackgroundPostResponseFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 27, Level = LogLevel.Warning, Message = "Prompt habit index truncated. OriginalEntryCount={OriginalEntryCount} RetainedEntryCount={RetainedEntryCount} MaxEntries={MaxEntries}")]
    private static partial void LogPromptHabitIndexTruncated(
        ILogger logger,
        int originalEntryCount,
        int retainedEntryCount,
        int maxEntries);

    [LoggerMessage(EventId = 28, Level = LogLevel.Warning, Message = "AI request token ceiling reached. TotalReportedTokens={TotalReportedTokens} MaxTokens={MaxTokens} ToolIterations={ToolIterations}")]
    private static partial void LogAiRequestTokenCeilingReached(
        ILogger logger,
        long totalReportedTokens,
        int maxTokens,
        int toolIterations);

    [LoggerMessage(EventId = 29, Level = LogLevel.Warning, Message = "Metrics card could not be built: {Error}")]
    private static partial void LogMetricsCardBuildFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Metrics card computation failed")]
    private static partial void LogMetricsCardBuildThrew(ILogger logger, Exception ex);
}
