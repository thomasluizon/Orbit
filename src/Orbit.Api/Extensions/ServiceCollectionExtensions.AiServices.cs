using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Api.Extensions;

public static partial class ServiceCollectionExtensions
{
    private static void AddAiPlatformServices(WebApplicationBuilder builder)
    {
        builder.Services.Configure<AiSettings>(
            builder.Configuration.GetSection(AiSettings.SectionName));
        builder.Services.Configure<AgentPlatformSettings>(
            builder.Configuration.GetSection(AgentPlatformSettings.SectionName));
        builder.Services.AddSingleton<AiCompletionClient>();
        builder.Services.AddSingleton<IAiUsageRecorder, AiUsageRecorder>();
        builder.Services.AddSingleton<IAiBatchClient, AiBatchClient>();
        builder.Services.AddScoped<IAudioTranscriptionService, AudioTranscriptionService>();
        builder.Services.AddScoped<IAiIntentService, AiIntentService>();
        builder.Services.AddScoped<IFactExtractionService, AiFactExtractionService>();
        builder.Services.AddScoped<IHabitEmojiInferenceService, AiHabitEmojiInferenceService>();
        builder.Services.AddScoped<ISummaryService, AiSummaryService>();
        builder.Services.AddScoped<IRescheduleSuggestionService, AiRescheduleSuggestionService>();
        builder.Services.AddScoped<IHabitSuggestionService, AiHabitSuggestionService>();
        builder.Services.AddScoped<IRetrospectiveService, AiRetrospectiveService>();
        builder.Services.AddScoped<IGoalReviewService, AiGoalReviewService>();
        builder.Services.AddScoped<ITagSuggestionService, AiTagSuggestionService>();
        builder.Services.AddHttpClient<IContentModerationService, ContentModerationService>()
            .ConfigureHttpClient(client => client.Timeout = GetDefaultHttpTimeout(builder));
        builder.Services.AddScoped<IAgentCatalogService, AgentCatalogService>();
        builder.Services.AddScoped<IPendingAgentOperationStore, PendingAgentOperationStore>();
        builder.Services.AddScoped<IPendingClarificationStore, PendingClarificationStore>();
        builder.Services.AddScoped<Orbit.Api.Controllers.AgentPendingStores>();
        builder.Services.AddScoped<IAgentStepUpService, AgentStepUpService>();
        builder.Services.AddScoped<IAgentPolicyEvaluator, AgentPolicyEvaluator>();
        builder.Services.AddScoped<IAgentAuditService, AgentAuditService>();
        builder.Services.AddScoped<IAgentTargetOwnershipService, AgentTargetOwnershipService>();
        builder.Services.AddScoped<IAgentOperationExecutor, AgentOperationExecutor>();
        builder.Services.AddScoped<Orbit.Api.Mcp.McpExecutorBridge>();
    }

    private static void AddAiChatTools(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IAiTool, LogHabitTool>();
        builder.Services.AddScoped<IAiTool, SkipHabitTool>();
        builder.Services.AddScoped<IAiTool, CreateHabitTool>();
        builder.Services.AddScoped<IAiTool, UpdateHabitTool>();
        builder.Services.AddScoped<IAiTool, DeleteHabitTool>();
        builder.Services.AddScoped<IAiTool, CreateSubHabitTool>();
        builder.Services.AddScoped<IAiTool, AssignTagsTool>();
        builder.Services.AddScoped<IAiTool, SuggestBreakdownTool>();
        builder.Services.AddScoped<IAiTool, DuplicateHabitTool>();
        builder.Services.AddScoped<IAiTool, MoveHabitTool>();
        builder.Services.AddScoped<IAiTool, BulkUpdateHabitsTool>();
        builder.Services.AddScoped<IAiTool, BulkRescheduleHabitsTool>();
        builder.Services.AddScoped<IAiTool, BulkUpdateHabitEmojisTool>();
        builder.Services.AddScoped<IAiTool, BulkLogHabitsTool>();
        builder.Services.AddScoped<IAiTool, BulkSkipHabitsTool>();
        builder.Services.AddScoped<IAiTool, QueryHabitsTool>();
        builder.Services.AddScoped<IAiTool, CreateGoalTool>();
        builder.Services.AddScoped<IAiTool, QueryGoalsTool>();
        builder.Services.AddScoped<IAiTool, UpdateGoalTool>();
        builder.Services.AddScoped<IAiTool, DeleteGoalTool>();
        builder.Services.AddScoped<IAiTool, UpdateGoalStatusTool>();
        builder.Services.AddScoped<IAiTool, UpdateGoalProgressTool>();
        builder.Services.AddScoped<IAiTool, LinkHabitsToGoalTool>();
        builder.Services.AddScoped<IAiTool, GoalReviewTool>();
        builder.Services.AddScoped<IAiTool, GetProfileTool>();
        builder.Services.AddScoped<IAiTool, UpdateProfilePreferencesTool>();
        builder.Services.AddScoped<IAiTool, SetColorSchemeTool>();
        builder.Services.AddScoped<IAiTool, SetAiMemoryTool>();
        builder.Services.AddScoped<IAiTool, SetAiSummaryTool>();
        builder.Services.AddScoped<IAiTool, GetNotificationsTool>();
        builder.Services.AddScoped<IAiTool, UpdateNotificationsTool>();
        builder.Services.AddScoped<IAiTool, DeleteNotificationsTool>();
        builder.Services.AddScoped<IAiTool, GetCalendarOverviewTool>();
        builder.Services.AddScoped<IAiTool, ManageCalendarSyncTool>();
        builder.Services.AddScoped<IAiTool, GetChecklistTemplatesTool>();
        builder.Services.AddScoped<IAiTool, CreateChecklistTemplateTool>();
        builder.Services.AddScoped<IAiTool, DeleteChecklistTemplateTool>();
        builder.Services.AddScoped<IAiTool, GetUserFactsTool>();
        builder.Services.AddScoped<IAiTool, DeleteUserFactsTool>();
        builder.Services.AddScoped<IAiTool, GetGamificationOverviewTool>();
        builder.Services.AddScoped<IAiTool, GetReferralOverviewTool>();
        builder.Services.AddScoped<IAiTool, GetSubscriptionOverviewTool>();
        builder.Services.AddScoped<IAiTool, ManageSubscriptionTool>();
        builder.Services.AddScoped<IAiTool, GetApiKeysTool>();
        builder.Services.AddScoped<IAiTool, ManageApiKeysTool>();
        builder.Services.AddScoped<IAiTool, SendSupportRequestTool>();
        builder.Services.AddScoped<IAiTool, ManageAccountTool>();
        builder.Services.AddScoped<IAiTool, UpdateChecklistTool>();
        builder.Services.AddScoped<IAiTool, ReorderHabitsTool>();
        builder.Services.AddScoped<IAiTool, MoveHabitParentTool>();
        builder.Services.AddScoped<IAiTool, LinkGoalsToHabitTool>();
        builder.Services.AddScoped<IAiTool, BulkCreateHabitsTool>();
        builder.Services.AddScoped<IAiTool, BulkDeleteHabitsTool>();
        builder.Services.AddScoped<IAiTool, GetDailySummaryTool>();
        builder.Services.AddScoped<IAiTool, GetRetrospectiveTool>();
        builder.Services.AddScoped<IAiTool, GetHabitMetricsTool>();
        builder.Services.AddScoped<IAiTool, DescribeFeatureTool>();
        builder.Services.AddScoped<IAiTool, ListTagsTool>();
        builder.Services.AddScoped<IAiTool, CreateTagTool>();
        builder.Services.AddScoped<IAiTool, UpdateTagTool>();
        builder.Services.AddScoped<IAiTool, DeleteTagTool>();
        builder.Services.AddScoped<IAiTool, ReorderGoalsTool>();
        builder.Services.AddScoped<IAiTool, GetReferralCodeTool>();
        builder.Services.AddScoped<AiToolRegistry>();
        builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        builder.Services.AddSingleton<IFeatureExplanationService, FeatureExplanationService>();
    }

    private static void AddHabitCommandDependencies(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<Orbit.Application.Habits.Commands.LogHabitRepositories>(sp =>
            new Orbit.Application.Habits.Commands.LogHabitRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>()));
        builder.Services.AddScoped<Orbit.Application.Habits.Commands.LogHabitServices>(sp =>
            new Orbit.Application.Habits.Commands.LogHabitServices(
                sp.GetRequiredService<IUserDateService>(),
                sp.GetRequiredService<IUserStreakService>(),
                sp.GetRequiredService<IGamificationService>(),
                sp.GetRequiredService<IGoalCompletionService>(),
                sp.GetRequiredService<Orbit.Application.Challenges.Services.IChallengeProgressService>(),
                sp.GetRequiredService<MediatR.IMediator>(),
                sp.GetRequiredService<IPayGateService>()));
        builder.Services.AddScoped<Orbit.Application.Habits.Commands.BulkLogServices>(sp =>
            new Orbit.Application.Habits.Commands.BulkLogServices(
                sp.GetRequiredService<IUserDateService>(),
                sp.GetRequiredService<IUserStreakService>(),
                sp.GetRequiredService<IGamificationService>()));

        builder.Services.AddScoped<Orbit.Application.Habits.Commands.CreateHabitRepositories>(sp =>
            new Orbit.Application.Habits.Commands.CreateHabitRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Tag>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>()));
    }

    private static void AddCalendarCommandDependencies(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<Orbit.Application.Calendar.Commands.CalendarAutoSyncDependencies>(sp =>
            new Orbit.Application.Calendar.Commands.CalendarAutoSyncDependencies(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.GoogleCalendarSyncSuggestion>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Notification>>(),
                sp.GetRequiredService<IGoogleTokenService>(),
                sp.GetRequiredService<Orbit.Application.Calendar.Services.ICalendarEventFetcher>(),
                sp.GetRequiredService<IUnitOfWork>()));
    }

    private static void AddChatCommandDependencies(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<Orbit.Application.Chat.Commands.ChatAiDependencies>(sp =>
            new Orbit.Application.Chat.Commands.ChatAiDependencies(
                sp.GetRequiredService<IAiIntentService>(),
                sp.GetRequiredService<AiToolRegistry>(),
                sp.GetRequiredService<ISystemPromptBuilder>(),
                sp.GetRequiredService<IAgentCatalogService>()));
        builder.Services.AddScoped<Orbit.Application.Chat.Commands.ChatDataDependencies>(sp =>
            new Orbit.Application.Chat.Commands.ChatDataDependencies(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Goal>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.UserFact>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Tag>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.ChecklistTemplate>>(),
                sp.GetRequiredService<IFeatureFlagService>()));
        builder.Services.AddScoped<Orbit.Application.Chat.Commands.ChatExecutionDependencies>(sp =>
            new Orbit.Application.Chat.Commands.ChatExecutionDependencies(
                sp.GetRequiredService<IUserDateService>(),
                sp.GetRequiredService<IUserStreakService>(),
                sp.GetRequiredService<IPayGateService>(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IAgentOperationExecutor>(),
                sp.GetRequiredService<IPendingClarificationStore>(),
                sp.GetRequiredService<IGoalProgressReadSyncer>(),
                sp.GetRequiredService<IGamificationService>(),
                sp.GetRequiredService<MediatR.IMediator>(),
                sp.GetRequiredService<IProductAnalytics>()));
    }
}
