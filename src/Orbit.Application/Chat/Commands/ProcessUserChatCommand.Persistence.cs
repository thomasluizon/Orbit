using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task PersistExecutionResultsAsync(
        Guid userId,
        IReadOnlyList<ActionResult> actionResults,
        CancellationToken cancellationToken)
    {
        // Every habit tool stages its mutation against the shared change tracker and this is the one
        // place those stagings commit, so this is where the chat path joins HabitCeilingLock. Skip,
        // log, delete and schedule edits made through Astra are inputs a streak repair reads.
        await HabitCeilingLock.ExecuteAsync(execution.UnitOfWork, userId, async transactionToken =>
        {
            await execution.UnitOfWork.SaveChangesAsync(transactionToken);
            if (RequiresStreakRecalculation(actionResults))
            {
                await ConcurrencyRetry.SaveWithRetryAsync(
                    execution.UnitOfWork,
                    ct => execution.UserStreakService.RecalculateAsync(userId, cancellationToken: ct),
                    transactionToken);
            }
        }, cancellationToken);

        await ProcessOnboardingChecklistSafeAsync(userId, OnboardingChecklistSignal.AstraUsed, cancellationToken);
    }

    private async Task ProcessOnboardingChecklistSafeAsync(
        Guid userId, OnboardingChecklistSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            await execution.GamificationService.ProcessOnboardingChecklistAsync(userId, signal, cancellationToken);
        }
        catch (Exception ex)
        {
            LogOnboardingChecklistFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 27, Level = LogLevel.Warning, Message = "Onboarding checklist processing failed during chat turn")]
    private static partial void LogOnboardingChecklistFailed(ILogger logger, Exception ex);

    private static bool RequiresStreakRecalculation(IEnumerable<ActionResult> actionResults)
    {
        return actionResults.Any(action => action.Status == ActionStatus.Success && action.Type is "LogHabit" or "BulkLogHabits" or "DeleteHabit");
    }

    /// <summary>
    /// Fires off background work for fact extraction.
    /// Runs in a separate DI scope so it doesn't block the response.
    /// </summary>
    private void RunBackgroundPostResponseWork(
        Guid userId,
        string userMessage,
        string? aiMessage,
        bool shouldExtractFacts,
        IReadOnlyList<UserFact> existingFacts)
    {
        if (!shouldExtractFacts)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = execution.ServiceScopeFactory.CreateScope();
                await SubmitFactExtractionBatchAsync(scope, userId, userMessage, aiMessage, existingFacts);
            }
            catch (Exception ex)
            {
                LogBackgroundPostResponseFailed(logger, ex);
            }
        }, CancellationToken.None);
    }

    private static async Task SubmitFactExtractionBatchAsync(
        IServiceScope scope,
        Guid userId,
        string userMessage,
        string? aiMessage,
        IReadOnlyList<UserFact> existingFacts)
    {
        var bgFactService = scope.ServiceProvider.GetRequiredService<IFactExtractionService>();
        await bgFactService.SubmitBatchAsync(userMessage: userMessage, aiResponse: aiMessage,
            existingFacts: existingFacts, userId: userId, cancellationToken: CancellationToken.None);
    }
}
