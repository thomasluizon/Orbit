using System.Text.Json;

namespace Orbit.Domain.Models;

public enum AgentRiskClass
{
    Low = 0,
    Destructive = 1,
    High = 2
}

public enum AgentConfirmationRequirement
{
    None = 0,
    FreshConfirmation = 1,
    StepUp = 2
}

public enum AgentPolicyDecisionStatus
{
    Allowed = 0,
    ConfirmationRequired = 1,
    Denied = 2
}

public enum AgentOperationStatus
{
    Succeeded = 0,
    Failed = 1,
    PendingConfirmation = 2,
    Denied = 3,
    UnsupportedByPolicy = 4
}

public enum AgentAuthMethod
{
    Unknown = 0,
    Jwt = 1,
    ApiKey = 2
}

public enum AgentExecutionSurface
{
    Chat = 0,
    Mcp = 1,
    Metadata = 2
}

public record AgentCapability(
    string Id,
    string DisplayName,
    string Description,
    string Domain,
    string Scope,
    AgentRiskClass RiskClass,
    bool IsMutation,
    bool IsPhaseOneReadOnly,
    AgentConfirmationRequirement ConfirmationRequirement,
    string? PlanRequirement = null,
    IReadOnlyList<string>? FeatureFlagKeys = null,
    IReadOnlyList<string>? ChatToolNames = null,
    IReadOnlyList<string>? McpToolNames = null,
    IReadOnlyList<string>? ControllerActionKeys = null);

public record AgentOperation(
    string Id,
    string DisplayName,
    string Description,
    string CapabilityId,
    AgentRiskClass RiskClass,
    AgentConfirmationRequirement ConfirmationRequirement,
    bool IsMutation,
    bool IsAgentExecutable,
    JsonElement RequestSchema,
    JsonElement ResponseSchema);

public record AppSurface(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<string> HowToSteps,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> RelatedCapabilityIds,
    IReadOnlyList<string> RelatedControllerActionKeys);

public record UserDataFieldDescriptor(
    string Name,
    string Meaning,
    bool AiReadable,
    bool AiMutableInPhaseOne);

public record UserDataCatalogEntry(
    string Id,
    string DisplayName,
    string Description,
    string Sensitivity,
    string RetentionNotes,
    bool AiReadable,
    bool AiMutableInPhaseOne,
    IReadOnlyList<UserDataFieldDescriptor> Fields);

public record AgentPolicyEvaluationContext(
    string CapabilityId,
    Guid UserId,
    AgentExecutionSurface Surface,
    AgentAuthMethod AuthMethod,
    IReadOnlyCollection<string> GrantedScopes,
    string SourceName,
    string OperationSummary,
    string? OperationFingerprint = null,
    string? OperationArgumentsJson = null,
    string? ConfirmationToken = null,
    bool StepUpSatisfied = false,
    bool IsReadOnlyCredential = false);

public record AgentPolicyDecision(
    AgentPolicyDecisionStatus Status,
    AgentCapability? Capability,
    string? Reason = null,
    PendingAgentOperation? PendingOperation = null,
    AgentPolicyDecisionStatus? ShadowStatus = null,
    string? ShadowReason = null);

public record PendingAgentOperation(
    Guid Id,
    string CapabilityId,
    string DisplayName,
    string Summary,
    AgentRiskClass RiskClass,
    AgentConfirmationRequirement ConfirmationRequirement,
    DateTime ExpiresAtUtc);

public record PendingAgentOperationConfirmation(
    Guid PendingOperationId,
    string ConfirmationToken,
    DateTime ExpiresAtUtc);

public record PendingAgentOperationExecution(
    Guid PendingOperationId,
    string CapabilityId,
    string OperationId,
    JsonElement Arguments,
    AgentExecutionSurface Surface,
    AgentConfirmationRequirement ConfirmationRequirement);

public record AgentStepUpChallenge(
    Guid ChallengeId,
    Guid PendingOperationId,
    DateTime ExpiresAtUtc);

public record AgentExecuteOperationRequest(
    Guid UserId,
    string OperationId,
    JsonElement Arguments,
    AgentExecutionSurface Surface,
    AgentAuthMethod AuthMethod,
    IReadOnlyList<string>? GrantedScopes = null,
    bool IsReadOnlyCredential = false,
    string? ConfirmationToken = null,
    string? CorrelationId = null);

public record AgentOperationResult(
    string OperationId,
    string SourceName,
    AgentRiskClass RiskClass,
    AgentConfirmationRequirement ConfirmationRequirement,
    AgentOperationStatus Status,
    string? Summary = null,
    string? TargetId = null,
    string? TargetName = null,
    string? PolicyReason = null,
    Guid? PendingOperationId = null,
    object? Payload = null);

public record AgentExecuteOperationResponse(
    AgentOperationResult Operation,
    PendingAgentOperation? PendingOperation = null,
    AgentPolicyDenial? PolicyDenial = null);

public record AgentPolicyDenial(
    string OperationId,
    string SourceName,
    AgentRiskClass RiskClass,
    AgentConfirmationRequirement ConfirmationRequirement,
    string Reason,
    Guid? PendingOperationId = null);

public record AgentClientContext(
    string? Platform = null,
    string? Locale = null,
    string? TimeFormat = null,
    string? CurrentAppArea = null,
    bool? ShowGeneralOnToday = null,
    bool? SupportsHabitListCard = null,
    bool? SupportsGoalListCard = null,
    bool? SupportsMetricsCard = null);

public record AgentContextSnapshot(
    string Plan,
    string? Language,
    string? TimeZone,
    bool AiMemoryEnabled,
    bool AiSummaryEnabled,
    int WeekStartDay,
    string? ThemePreference,
    string? ColorScheme,
    bool HasGoogleConnection,
    bool GoogleCalendarAutoSyncEnabled,
    string GoogleCalendarAutoSyncStatus,
    IReadOnlyList<string>? FeatureFlags = null,
    IReadOnlyList<string>? TagNames = null,
    IReadOnlyList<string>? ChecklistTemplateNames = null,
    IReadOnlyList<string>? RecentHabitTitles = null,
    IReadOnlyList<string>? RecentGoalTitles = null,
    AgentClientContext? ClientContext = null);

public record AgentAuditEntry(
    Guid UserId,
    string CapabilityId,
    string SourceName,
    AgentExecutionSurface Surface,
    AgentAuthMethod AuthMethod,
    AgentRiskClass RiskClass,
    AgentPolicyDecisionStatus PolicyDecision,
    AgentOperationStatus OutcomeStatus,
    string? CorrelationId = null,
    string? Summary = null,
    string? TargetId = null,
    string? TargetName = null,
    string? RedactedArguments = null,
    string? Error = null,
    AgentPolicyDecisionStatus? ShadowPolicyDecision = null,
    string? ShadowReason = null);

public static class AgentCapabilityIds
{
    public const string ChatInteract = "chat.interact";
    public const string CatalogCapabilitiesRead = "catalog.capabilities.read";
    public const string CatalogDataRead = "catalog.data.read";
    public const string CatalogSurfacesRead = "catalog.surfaces.read";
    public const string DescribeFeature = "feature.describe";
    public const string ConfigRead = "config.read";
    public const string HabitsRead = "habits.read";
    public const string HabitsWrite = "habits.write";
    public const string HabitsDelete = "habits.delete";
    public const string HabitsBulkWrite = "habits.bulk.write";
    public const string HabitsBulkDelete = "habits.bulk.delete";
    public const string HabitsInsightsRead = "habits.insights.read";
    public const string HabitMetricsRead = "habits.metrics.read";
    public const string DailySummaryRead = "habits.daily-summary.read";
    public const string RetrospectiveRead = "habits.retrospective.read";
    public const string GoalsRead = "goals.read";
    public const string GoalsWrite = "goals.write";
    public const string GoalsDelete = "goals.delete";
    public const string TagsRead = "tags.read";
    public const string TagsWrite = "tags.write";
    public const string TagsDelete = "tags.delete";
    public const string ProfileReadBasic = "profile.read.basic";
    public const string ProfileReadSensitive = "profile.read.sensitive";
    public const string ProfilePreferencesWrite = "profile.preferences.write";
    public const string ProfilePremiumAppearanceWrite = "profile.premium-appearance.write";
    public const string ProfileAiSettingsWrite = "profile.ai-settings.write";
    public const string ProfileAiMemoryWrite = "profile.ai-memory.write";
    public const string ProfileAiSummaryWrite = "profile.ai-summary.write";
    public const string ProfileProactiveAstraWrite = "profile.proactive-astra.write";
    public const string NotificationsRead = "notifications.read";
    public const string NotificationsWrite = "notifications.write";
    public const string NotificationsDelete = "notifications.delete";
    public const string CalendarRead = "calendar.read";
    public const string CalendarSyncManage = "calendar.sync.manage";
    public const string GamificationRead = "gamification.read";
    public const string GamificationRepair = "gamification.repair";
    public const string GamificationRepairGap = "gamification.repair_gap";
    public const string ChecklistTemplatesRead = "checklist-templates.read";
    public const string ChecklistTemplatesWrite = "checklist-templates.write";
    public const string UserFactsRead = "user-facts.read";
    public const string UserFactsDelete = "user-facts.delete";
    public const string ReferralsRead = "referrals.read";
    public const string ReferralsWrite = "referrals.write";
    public const string SubscriptionsRead = "subscriptions.read";
    public const string SubscriptionsManage = "subscriptions.manage";
    public const string ApiKeysRead = "api-keys.read";
    public const string ApiKeysManage = "api-keys.manage";
    public const string SupportWrite = "support.write";
    public const string SyncRead = "sync.read";
    public const string SyncWrite = "sync.write";
    public const string AccountManage = "account.manage";
    public const string AuthManage = "auth.manage";
    public const string SocialManage = "social.manage";
    public const string MediaUpload = "media.upload";
    public const string MarketingManage = "marketing.manage";
}

public static class AgentScopes
{
    public const string ChatInteract = "chat_interact";
    public const string CatalogRead = "catalog_read";
    public const string ReadConfig = "read_config";
    public const string ReadHabits = "read_habits";
    public const string WriteHabits = "write_habits";
    public const string DeleteHabits = "delete_habits";
    public const string ReadGoals = "read_goals";
    public const string WriteGoals = "write_goals";
    public const string DeleteGoals = "delete_goals";
    public const string ReadTags = "read_tags";
    public const string WriteTags = "write_tags";
    public const string DeleteTags = "delete_tags";
    public const string ReadProfileBasic = "read_profile_basic";
    public const string ReadProfileSensitive = "read_profile_sensitive";
    public const string WriteProfilePreferences = "write_profile_preferences";
    public const string WriteAiSettings = "write_ai_settings";
    public const string ReadNotifications = "read_notifications";
    public const string WriteNotifications = "write_notifications";
    public const string DeleteNotifications = "delete_notifications";
    public const string ReadCalendar = "read_calendar";
    public const string ManageCalendarSync = "manage_calendar_sync";
    public const string ReadGamification = "read_gamification";
    public const string WriteGamification = "write_gamification";
    public const string ReadChecklistTemplates = "read_checklist_templates";
    public const string WriteChecklistTemplates = "write_checklist_templates";
    public const string ReadUserFacts = "read_user_facts";
    public const string DeleteUserFacts = "delete_user_facts";
    public const string ReadReferrals = "read_referrals";
    public const string WriteReferrals = "write_referrals";
    public const string ReadSubscriptions = "read_subscriptions";
    public const string ManageSubscriptions = "manage_subscriptions";
    public const string ReadApiKeys = "read_api_keys";
    public const string ManageApiKeys = "manage_api_keys";
    public const string WriteSupport = "write_support";
    public const string ReadSync = "read_sync";
    public const string WriteSync = "write_sync";
    public const string ManageAccount = "manage_account";
    public const string ManageAuth = "manage_auth";
    public const string ManageSocial = "manage_social";
    public const string UploadMedia = "upload_media";
    public const string ManageMarketing = "manage_marketing";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ChatInteract,
        CatalogRead,
        ReadConfig,
        ReadHabits,
        WriteHabits,
        DeleteHabits,
        ReadGoals,
        WriteGoals,
        DeleteGoals,
        ReadTags,
        WriteTags,
        DeleteTags,
        ReadProfileBasic,
        ReadProfileSensitive,
        WriteProfilePreferences,
        WriteAiSettings,
        ReadNotifications,
        WriteNotifications,
        DeleteNotifications,
        ReadCalendar,
        ManageCalendarSync,
        ReadGamification,
        WriteGamification,
        ReadChecklistTemplates,
        WriteChecklistTemplates,
        ReadUserFacts,
        DeleteUserFacts,
        ReadReferrals,
        WriteReferrals,
        ReadSubscriptions,
        ManageSubscriptions,
        ReadApiKeys,
        ManageApiKeys,
        WriteSupport,
        ReadSync,
        WriteSync,
        ManageAccount,
        ManageAuth,
        ManageSocial,
        UploadMedia,
        ManageMarketing
    };

    public static readonly IReadOnlyList<string> ClaudeDefaultScopes =
    [
        ChatInteract,
        CatalogRead,
        ReadConfig,
        ReadHabits,
        WriteHabits,
        DeleteHabits,
        ReadGoals,
        WriteGoals,
        DeleteGoals,
        ReadTags,
        WriteTags,
        DeleteTags,
        ReadProfileBasic,
        WriteProfilePreferences,
        WriteAiSettings,
        ReadNotifications,
        WriteNotifications,
        DeleteNotifications,
        ReadCalendar,
        ManageCalendarSync,
        ReadGamification,
        ReadChecklistTemplates,
        WriteChecklistTemplates,
        ReadUserFacts,
        DeleteUserFacts,
        ReadReferrals,
        WriteReferrals,
        ReadSubscriptions
    ];
}
