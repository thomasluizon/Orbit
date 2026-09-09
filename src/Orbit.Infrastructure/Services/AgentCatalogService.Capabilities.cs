using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

#pragma warning disable S107 // Declarative catalog builders mirror the record shapes they populate.
#pragma warning disable S1192 // Catalog definitions intentionally reuse product vocabulary and JSON schema literals.
#pragma warning disable CA1861 // Static catalog schemas are evaluated once at startup and are not hot-path allocations.

public partial class AgentCatalogService
{
    private static IReadOnlyList<AgentCapability> BuildCapabilities()
    {
        return
        [
            .. ChatCapabilities(),
            .. CatalogCapabilities(),
            .. ConfigCapabilities(),
            .. HabitCoreCapabilities(),
            .. HabitBulkAndInsightCapabilities(),
            .. GoalCapabilities(),
            .. TagCapabilities(),
            .. ProfileCapabilities(),
            .. NotificationCapabilities(),
            .. CalendarCapabilities(),
            .. GamificationCapabilities(),
            .. ChecklistTemplateCapabilities(),
            .. UserFactCapabilities(),
            .. ReferralCapabilities(),
            .. SubscriptionCapabilities(),
            .. ApiKeyCapabilities(),
            .. SupportCapabilities(),
            .. SyncCapabilities(),
            .. AccountAndAuthCapabilities(),
            .. SocialCapabilities()
        ];
    }

    private static AgentCapability[] ChatCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.ChatInteract,
                "Chat Interaction",
                "Accepts a user chat request and coordinates safe Orbit operations.",
                "chat",
                AgentScopes.ChatInteract,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                mcpTools: ["execute_agent_operation_v2", "confirm_agent_operation_v2", "step_up_agent_operation_v2", "verify_step_up_agent_operation_v2"],
                controllerActions:
                [
                    "ChatController.ProcessChat",
                    "ChatController.ProcessChatStream",
                    "ChatController.Transcribe",
                    "AiController.ConfirmPendingOperation",
                    "AiController.MarkPendingOperationStepUp",
                    "AiController.VerifyPendingOperationStepUp",
                    "AiController.ExecutePendingOperation",
                    "AiController.ResolveClarification"
                ])
        ];
    }

    private static AgentCapability[] CatalogCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.CatalogCapabilitiesRead,
                "Read Capability Catalog",
                "Lists the Orbit capability catalog available to AI clients.",
                "catalog",
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                mcpTools: ["list_agent_capabilities_v2", "list_agent_operations_v2"],
                controllerActions: ["AiController.GetCapabilitiesMetadata", "AiController.GetOperationsMetadata"]),

            CreateCapability(
                AgentCapabilityIds.CatalogDataRead,
                "Read User Data Catalog",
                "Lists the catalog of user-data classes, sensitivity, and AI mutability.",
                "catalog",
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                mcpTools: ["list_user_data_catalog_v2"],
                controllerActions: ["AiController.GetUserDataCatalog"]),

            CreateCapability(
                AgentCapabilityIds.CatalogSurfacesRead,
                "Read App Surface Catalog",
                "Lists Orbit product surfaces and how-to guidance.",
                "catalog",
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                mcpTools: ["list_app_surfaces_v2"],
                controllerActions: ["AiController.GetAppSurfaces"]),

            CreateCapability(
                AgentCapabilityIds.DescribeFeature,
                "Describe Feature",
                "Returns an authoritative explanation of an Orbit feature's mechanics.",
                "catalog",
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["describe_feature"],
                mcpTools: ["describe_feature"])
        ];
    }

    private static AgentCapability[] ConfigCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.ConfigRead,
                "Read App Config",
                "Reads frontend-visible configuration needed by Orbit clients.",
                "config",
                AgentScopes.ReadConfig,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                controllerActions: ["ConfigController.GetConfig"])
        ];
    }

    private static AgentCapability[] HabitCoreCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.HabitsRead,
                "Read Habits",
                "Reads habits, logs, and schedule state.",
                "habits",
                AgentScopes.ReadHabits,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["query_habits"],
                mcpTools: ["list_habits", "get_habit", "get_habit_logs", "get_all_habit_logs"],
                controllerActions:
                [
                    "HabitsController.GetHabits",
                    "HabitsController.GetHabitCount",
                    "HabitsController.GetHabitWidget",
                    "HabitsController.GetCalendarMonth",
                    "HabitsController.GetHabitById",
                    "HabitsController.GetHabitDetail",
                    "HabitsController.GetRescheduleSuggestion",
                    "HabitsController.GetLogs",
                    "HabitsController.GetTrends"
                ]),

            CreateCapability(
                AgentCapabilityIds.HabitsWrite,
                "Write Habits",
                "Creates and updates habits, checklists, and hierarchy links.",
                "habits",
                AgentScopes.WriteHabits,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["create_habit", "update_habit", "bulk_update_habit_emojis", "create_sub_habit", "duplicate_habit", "move_habit", "move_habit_parent", "log_habit", "skip_habit", "suggest_breakdown", "update_checklist", "reorder_habits", "link_goals_to_habit"],
                mcpTools:
                [
                    "create_habit",
                    "update_habit",
                    "log_habit",
                    "skip_habit",
                    "update_checklist",
                    "create_sub_habit",
                    "duplicate_habit",
                    "reorder_habits",
                    "move_habit_parent",
                    "link_goals_to_habit"
                ],
                controllerActions:
                [
                    "HabitsController.CreateHabit",
                    "HabitsController.SuggestSetup",
                    "HabitsController.LogHabit",
                    "HabitsController.SkipHabit",
                    "HabitsController.UpdateHabit",
                    "HabitsController.UpdateChecklist",
                    "HabitsController.ReorderHabits",
                    "HabitsController.MoveHabitParent",
                    "HabitsController.DuplicateHabit",
                    "HabitsController.CreateSubHabit",
                    "HabitsController.LinkGoals",
                    "HabitsController.RestoreHabit"
                ]),

            CreateCapability(
                AgentCapabilityIds.HabitsDelete,
                "Delete Habits",
                "Deletes individual habits.",
                "habits",
                AgentScopes.DeleteHabits,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                chatTools: ["delete_habit"],
                mcpTools: ["delete_habit"],
                controllerActions: ["HabitsController.DeleteHabit"])
        ];
    }

    private static AgentCapability[] HabitBulkAndInsightCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.HabitsBulkWrite,
                "Bulk Habit Updates",
                "Runs bulk create, log, and skip habit operations.",
                "habits",
                AgentScopes.WriteHabits,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                chatTools: ["bulk_log_habits", "bulk_skip_habits", "bulk_create_habits"],
                mcpTools: ["bulk_create_habits", "bulk_log_habits", "bulk_skip_habits"],
                controllerActions: ["HabitsController.BulkCreate", "HabitsController.BulkLog", "HabitsController.BulkSkip"]),

            CreateCapability(
                AgentCapabilityIds.HabitsBulkDelete,
                "Bulk Habit Delete",
                "Deletes multiple habits in one operation.",
                "habits",
                AgentScopes.DeleteHabits,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                chatTools: ["bulk_delete_habits"],
                mcpTools: ["bulk_delete_habits"],
                controllerActions: ["HabitsController.BulkDelete"]),

            CreateCapability(
                AgentCapabilityIds.HabitMetricsRead,
                "Read Habit Metrics",
                "Reads non-premium habit metrics.",
                "habits",
                AgentScopes.ReadHabits,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["get_habit_metrics"],
                mcpTools: ["get_habit_metrics"],
                controllerActions: ["HabitsController.GetMetrics"]),

            CreateCapability(
                AgentCapabilityIds.DailySummaryRead,
                "Read Daily Summary",
                "Reads the premium AI daily summary.",
                "habits",
                AgentScopes.ReadHabits,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                featureFlagKeys: ["ai_summary"],
                chatTools: ["get_daily_summary"],
                mcpTools: ["get_daily_summary"],
                controllerActions: ["HabitsController.GetDailySummary"]),

            CreateCapability(
                AgentCapabilityIds.RetrospectiveRead,
                "Read Retrospective",
                "Reads the Pro retrospective.",
                "habits",
                AgentScopes.ReadHabits,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                featureFlagKeys: ["ai_retrospective"],
                chatTools: ["get_retrospective"],
                mcpTools: ["get_retrospective"],
                controllerActions: ["HabitsController.GetRetrospective"])
        ];
    }

    private static AgentCapability[] GoalCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.GoalsRead,
                "Read Goals",
                "Reads goals, reviews, metrics, and linked habit progress.",
                "goals",
                AgentScopes.ReadGoals,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["query_goals", "review_goals"],
                mcpTools: ["list_goals", "get_goal", "get_goal_metrics", "get_goal_review"],
                controllerActions:
                [
                    "GoalsController.GetGoals",
                    "GoalsController.GetGoalById",
                    "GoalsController.GetGoalDetail",
                    "GoalsController.GetGoalMetrics",
                    "GoalsController.GetGoalReview",
                    "GoalsController.GetGoalProgressHistory"
                ]),

            CreateCapability(
                AgentCapabilityIds.GoalsWrite,
                "Write Goals",
                "Creates and updates goals, progress, ordering, and habit links.",
                "goals",
                AgentScopes.WriteGoals,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["create_goal", "update_goal", "update_goal_status", "update_goal_progress", "link_habits_to_goal", "reorder_goals"],
                mcpTools: ["create_goal", "update_goal", "update_goal_progress", "update_goal_status", "reorder_goals", "link_habits_to_goal"],
                controllerActions:
                [
                    "GoalsController.CreateGoal",
                    "GoalsController.UpdateGoal",
                    "GoalsController.UpdateProgress",
                    "GoalsController.UpdateStatus",
                    "GoalsController.ReorderGoals",
                    "GoalsController.LinkHabits",
                    "GoalsController.RestoreGoal"
                ]),

            CreateCapability(
                AgentCapabilityIds.GoalsDelete,
                "Delete Goals",
                "Deletes goals.",
                "goals",
                AgentScopes.DeleteGoals,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                chatTools: ["delete_goal"],
                mcpTools: ["delete_goal"],
                controllerActions: ["GoalsController.DeleteGoal"])
        ];
    }

    private static AgentCapability[] TagCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.TagsRead,
                "Read Tags",
                "Reads user tags and habit tag assignments.",
                "tags",
                AgentScopes.ReadTags,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["list_tags"],
                mcpTools: ["list_tags"],
                controllerActions: ["TagsController.GetTags"]),

            CreateCapability(
                AgentCapabilityIds.TagsWrite,
                "Write Tags",
                "Creates, updates, and assigns tags.",
                "tags",
                AgentScopes.WriteTags,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["assign_tags", "create_tag", "update_tag"],
                mcpTools: ["create_tag", "update_tag", "assign_tags"],
                controllerActions: ["TagsController.CreateTag", "TagsController.UpdateTag", "TagsController.AssignTags", "TagsController.SuggestTags", "TagsController.RestoreTag"]),

            CreateCapability(
                AgentCapabilityIds.TagsDelete,
                "Delete Tags",
                "Deletes tags.",
                "tags",
                AgentScopes.DeleteTags,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                chatTools: ["delete_tag"],
                mcpTools: ["delete_tag"],
                controllerActions: ["TagsController.DeleteTag"])
        ];
    }

    private static AgentCapability[] ProfileCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.ProfileReadBasic,
                "Read Profile",
                "Reads user profile and preference state without exposing secrets.",
                "profile",
                AgentScopes.ReadProfileBasic,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["get_profile"],
                mcpTools: ["get_profile"],
                controllerActions: ["ProfileController.GetProfile"]),

            CreateCapability(
                AgentCapabilityIds.ProfilePreferencesWrite,
                "Write Preferences",
                "Writes display name, timezone, language, theme, onboarding, and week-start preferences.",
                "profile",
                AgentScopes.WriteProfilePreferences,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["update_profile_preferences"],
                mcpTools: ["set_timezone", "set_language", "set_week_start_day"],
                controllerActions:
                [
                    "ProfileController.SetName",
                    "ProfileController.SetTimezone",
                    "ProfileController.SetLanguage",
                    "ProfileController.SetWeekStartDay",
                    "ProfileController.SetThemePreference",
                    "ProfileController.CompleteOnboarding",
                    "ProfileController.ApplyOnboarding",
                    "ProfileController.DismissImportPrompt",
                    "ProfileController.CompleteTour",
                    "ProfileController.ResetTour",
                    "ProfileController.UpdateMarketingConsent"
                ]),

            CreateCapability(
                AgentCapabilityIds.ProfilePremiumAppearanceWrite,
                "Write Premium Appearance",
                "Updates premium color-scheme preferences.",
                "profile",
                AgentScopes.WriteProfilePreferences,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                chatTools: ["set_color_scheme"],
                mcpTools: ["set_color_scheme"],
                controllerActions: ["ProfileController.SetColorScheme"]),

            CreateCapability(
                AgentCapabilityIds.ProfileAiMemoryWrite,
                "Write AI Memory Settings",
                "Updates AI memory preferences.",
                "profile",
                AgentScopes.WriteAiSettings,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                chatTools: ["set_ai_memory"],
                mcpTools: ["set_ai_memory"],
                controllerActions: ["ProfileController.SetAiMemory"]),

            CreateCapability(
                AgentCapabilityIds.ProfileAiSummaryWrite,
                "Write AI Summary Settings",
                "Updates AI summary preferences.",
                "profile",
                AgentScopes.WriteAiSettings,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                featureFlagKeys: ["ai_summary"],
                chatTools: ["set_ai_summary"],
                mcpTools: ["set_ai_summary"],
                controllerActions: ["ProfileController.SetAiSummary"]),

            CreateCapability(
                AgentCapabilityIds.ProfileProactiveAstraWrite,
                "Write Proactive Astra Settings",
                "Updates whether Astra sends proactive check-in pushes when the user falls behind.",
                "profile",
                AgentScopes.WriteAiSettings,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                controllerActions: ["ProfileController.SetProactiveAstra"])
        ];
    }

    private static AgentCapability[] NotificationCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.NotificationsRead,
                "Read Notifications",
                "Reads in-app notifications.",
                "notifications",
                AgentScopes.ReadNotifications,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["get_notifications"],
                mcpTools: ["get_notifications"],
                controllerActions: ["NotificationController.GetNotifications"]),

            CreateCapability(
                AgentCapabilityIds.NotificationsWrite,
                "Write Notifications",
                "Marks notifications as read and manages push subscriptions.",
                "notifications",
                AgentScopes.WriteNotifications,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["update_notifications"],
                mcpTools: ["mark_notification_read", "mark_all_notifications_read", "subscribe_push", "unsubscribe_push", "test_push"],
                controllerActions:
                [
                    "NotificationController.MarkAsRead",
                    "NotificationController.MarkAllAsRead",
                    "NotificationController.Subscribe",
                    "NotificationController.Unsubscribe",
                    "NotificationController.TestPush"
                ]),

            CreateCapability(
                AgentCapabilityIds.NotificationsDelete,
                "Delete Notifications",
                "Deletes one or all notifications.",
                "notifications",
                AgentScopes.DeleteNotifications,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                chatTools: ["delete_notifications"],
                mcpTools: ["delete_notification"],
                controllerActions: ["NotificationController.Delete", "NotificationController.DeleteAll"])
        ];
    }

    private static AgentCapability[] CalendarCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.CalendarRead,
                "Read Calendar",
                "Reads imported events and calendar sync suggestions.",
                "calendar",
                AgentScopes.ReadCalendar,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                featureFlagKeys: ["calendar_integration"],
                chatTools: ["get_calendar_overview"],
                mcpTools: ["get_calendar_events"],
                controllerActions:
                [
                    "CalendarController.GetEvents",
                    "CalendarController.GetSuggestions",
                    "CalendarController.GetCalendars"
                ]),

            CreateCapability(
                AgentCapabilityIds.CalendarSyncManage,
                "Manage Calendar Sync",
                "Dismisses suggestions and manages Google Calendar sync state.",
                "calendar",
                AgentScopes.ManageCalendarSync,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                featureFlagKeys: ["calendar_integration"],
                chatTools: ["manage_calendar_sync"],
                mcpTools: ["manage_calendar_sync"],
                controllerActions:
                [
                    "CalendarController.GetAutoSyncState",
                    "CalendarController.DismissImport",
                    "CalendarController.SetAutoSync",
                    "CalendarController.DismissSuggestion",
                    "CalendarController.RunSyncNow",
                    "CalendarController.SetSelectedCalendars"
                ])
        ];
    }

    private static AgentCapability[] GamificationCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.GamificationRead,
                "Read Gamification",
                "Reads streaks, levels, achievements, and referral rewards context.",
                "gamification",
                AgentScopes.ReadGamification,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                chatTools: ["get_gamification_overview"],
                mcpTools: ["get_gamification_profile", "get_achievements", "get_streak_info"],
                controllerActions:
                [
                    "GamificationController.GetProfile",
                    "GamificationController.GetAchievements",
                    "GamificationController.GetStreakInfo",
                    "GamificationController.GetRecap",
                    "GamificationController.GetStreakHistory",
                    "GamificationController.GetXpHistory",
                    "AchievementsController.ReportEvent"
                ]),

            CreateCapability(
                AgentCapabilityIds.GamificationRepair,
                "Repair Streak",
                "Spends one banked freeze to repair the eligible missed streak day.",
                "gamification",
                AgentScopes.WriteGamification,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                controllerActions: ["GamificationController.RepairStreak"]),

            CreateCapability(
                AgentCapabilityIds.GamificationRepairGap,
                "Repair Streak Gap",
                "Spends banked freezes to repair an entire contiguous streak gap atomically.",
                "gamification",
                AgentScopes.WriteGamification,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                controllerActions: ["StreakGapController.RepairGap"])
        ];
    }

    private static AgentCapability[] ChecklistTemplateCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.ChecklistTemplatesRead,
                "Read Checklist Templates",
                "Reads reusable checklist templates.",
                "checklists",
                AgentScopes.ReadChecklistTemplates,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                featureFlagKeys: ["checklist_templates"],
                chatTools: ["get_checklist_templates"],
                mcpTools: ["get_checklist_templates"],
                controllerActions: ["ChecklistTemplatesController.GetTemplates"]),

            CreateCapability(
                AgentCapabilityIds.ChecklistTemplatesWrite,
                "Write Checklist Templates",
                "Creates and deletes checklist templates.",
                "checklists",
                AgentScopes.WriteChecklistTemplates,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                featureFlagKeys: ["checklist_templates"],
                chatTools: ["create_checklist_template", "delete_checklist_template"],
                mcpTools: ["create_checklist_template", "delete_checklist_template"],
                controllerActions: ["ChecklistTemplatesController.CreateTemplate", "ChecklistTemplatesController.DeleteTemplate"])
        ];
    }

    private static AgentCapability[] UserFactCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.UserFactsRead,
                "Read User Facts",
                "Reads AI memory facts saved for the user.",
                "memory",
                AgentScopes.ReadUserFacts,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                chatTools: ["get_user_facts"],
                mcpTools: ["get_user_facts"],
                controllerActions: ["UserFactsController.GetUserFacts"]),

            CreateCapability(
                AgentCapabilityIds.UserFactsDelete,
                "Delete User Facts",
                "Deletes saved AI memory facts.",
                "memory",
                AgentScopes.DeleteUserFacts,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                planRequirement: "Pro",
                chatTools: ["delete_user_facts"],
                mcpTools: ["delete_user_fact"],
                controllerActions: ["UserFactsController.DeleteUserFact", "UserFactsController.BulkDeleteUserFacts"])
        ];
    }

    private static AgentCapability[] ReferralCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.ReferralsRead,
                "Read Referrals",
                "Reads referral code, stats, and dashboard state.",
                "referrals",
                AgentScopes.ReadReferrals,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["get_referral_overview"],
                mcpTools: ["get_referral_stats"],
                controllerActions:
                [
                    "ReferralController.GetStats",
                    "ReferralController.GetDashboard"
                ]),

            CreateCapability(
                AgentCapabilityIds.ReferralsWrite,
                "Write Referrals",
                "Generates the user's referral code on demand.",
                "referrals",
                AgentScopes.WriteReferrals,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["get_referral_code"],
                mcpTools: ["get_referral_code"],
                controllerActions: ["ReferralController.GetOrCreateCode"])
        ];
    }

    private static AgentCapability[] SubscriptionCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.SubscriptionsRead,
                "Read Subscription State",
                "Reads plans, billing status, and entitlement state.",
                "subscriptions",
                AgentScopes.ReadSubscriptions,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["get_subscription_overview"],
                mcpTools: ["get_subscription_status"],
                controllerActions:
                [
                    "SubscriptionController.GetStatus",
                    "SubscriptionController.GetBillingDetails",
                    "SubscriptionController.GetPlans"
                ]),

            CreateCapability(
                AgentCapabilityIds.SubscriptionsManage,
                "Manage Subscription",
                "Creates checkout and billing portal sessions or claims ad rewards.",
                "subscriptions",
                AgentScopes.ManageSubscriptions,
                AgentRiskClass.High,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.StepUp,
                chatTools: ["manage_subscription"],
                mcpTools: ["manage_subscription"],
                controllerActions:
                [
                    "SubscriptionController.CreateCheckout",
                    "SubscriptionController.CreatePortal",
                    "SubscriptionController.ClaimAdReward",
                    "SubscriptionController.HandleWebhook",
                    "SubscriptionController.VerifyPlayPurchase",
                    "SubscriptionController.HandlePlayNotification"
                ])
        ];
    }

    private static AgentCapability[] ApiKeyCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.ApiKeysRead,
                "Read API Keys",
                "Reads API-key metadata without exposing key material.",
                "api-keys",
                AgentScopes.ReadApiKeys,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                planRequirement: "Pro",
                featureFlagKeys: ["api_keys"],
                chatTools: ["get_api_keys"],
                mcpTools: ["get_api_keys"],
                controllerActions: ["ApiKeysController.GetApiKeys"]),

            CreateCapability(
                AgentCapabilityIds.ApiKeysManage,
                "Manage API Keys",
                "Creates and revokes scoped API keys.",
                "api-keys",
                AgentScopes.ManageApiKeys,
                AgentRiskClass.High,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.StepUp,
                planRequirement: "Pro",
                featureFlagKeys: ["api_keys"],
                chatTools: ["manage_api_keys"],
                mcpTools: ["manage_api_keys"],
                controllerActions:
                [
                    "ApiKeysController.CreateApiKey",
                    "ApiKeysController.RequestCreationChallenge",
                    "ApiKeysController.ConfirmCreationChallenge",
                    "ApiKeysController.RevokeApiKey"
                ])
        ];
    }

    private static AgentCapability[] SupportCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.SupportWrite,
                "Send Support Request",
                "Submits a support request on behalf of the user.",
                "support",
                AgentScopes.WriteSupport,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                chatTools: ["send_support_request"],
                mcpTools: ["send_support_request"],
                controllerActions: ["SupportController.SendSupport"])
        ];
    }

    private static AgentCapability[] SyncCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.SyncRead,
                "Read Sync Data",
                "Reads versioned sync payloads for first-party clients.",
                "sync",
                AgentScopes.ReadSync,
                AgentRiskClass.Low,
                isMutation: false,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                controllerActions: ["SyncController.GetChangesV2"]),

            CreateCapability(
                AgentCapabilityIds.SyncWrite,
                "Write Sync Data",
                "Processes sync mutations from first-party clients.",
                "sync",
                AgentScopes.WriteSync,
                AgentRiskClass.Destructive,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                controllerActions: ["SyncController.ProcessBatch"])
        ];
    }

    private static AgentCapability[] SocialCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.SocialManage,
                "Manage Social",
                "Manages friendships, cheers, the friend feed, handles, blocking, reporting, accountability buddies, and cooperative challenges. Cataloged but not exposed to the agent in this phase.",
                "social",
                AgentScopes.ManageSocial,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                controllerActions:
                [
                    "FriendsController.GetFriends",
                    "FriendsController.GetFriendProfile",
                    "FriendsController.GetInvitePreview",
                    "FriendsController.GetFeed",
                    "FriendsController.GetCheers",
                    "FriendsController.SendRequest",
                    "FriendsController.AcceptRequest",
                    "FriendsController.RemoveFriend",
                    "FriendsController.SendCheer",
                    "FriendsController.Block",
                    "FriendsController.Unblock",
                    "FriendsController.Report",
                    "ChallengesController.Create",
                    "ChallengesController.Join",
                    "ChallengesController.Leave",
                    "ChallengesController.GetDetail",
                    "ChallengesController.GetMine",
                    "ChallengesController.SetHabits",
                    "ProfileController.SetHandle",
                    "ProfileController.SetSocialOptIn",
                    "ProfileController.UpdatePublicProfile",
                    "PublicProfileController.GetPublicProfile",
                    "AccountabilityController.GetPairs",
                    "AccountabilityController.Invite",
                    "AccountabilityController.Accept",
                    "AccountabilityController.End",
                    "AccountabilityController.SetHabits",
                    "AccountabilityController.GetCheckIns",
                    "AccountabilityController.CheckIn"
                ])
        ];
    }

    private static AgentCapability[] AccountAndAuthCapabilities()
    {
        return
        [
            CreateCapability(
                AgentCapabilityIds.AccountManage,
                "Manage Account",
                "Resets account state or handles account deletion lifecycle.",
                "account",
                AgentScopes.ManageAccount,
                AgentRiskClass.High,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.StepUp,
                chatTools: ["manage_account"],
                mcpTools: ["manage_account"],
                controllerActions:
                [
                    "AuthController.RequestDeletion",
                    "AuthController.ConfirmDeletion",
                    "ProfileController.ResetAccount",
                    "ProfileController.ExportUserData"
                ]),

            CreateCapability(
                AgentCapabilityIds.AuthManage,
                "Manage Authentication",
                "Sends codes, verifies sessions, refreshes, logs out, and runs OAuth exchange.",
                "auth",
                AgentScopes.ManageAuth,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                controllerActions:
                [
                    "AuthController.SendCode",
                    "AuthController.SendCodeOperation",
                    "AuthController.VerifyCode",
                    "AuthController.VerifyCodeOperation",
                    "AuthController.GoogleAuth",
                    "AuthController.GoogleAuthOperation",
                    "AuthController.Refresh",
                    "AuthController.RefreshOperation",
                    "AuthController.Logout",
                    "AuthController.LogoutOperation",
                    "AuthController.LogoutAll",
                    "OAuthController.GetMetadata",
                    "OAuthController.Register",
                    "OAuthController.GetProtectedResourceMetadata",
                    "OAuthController.Authorize",
                    "OAuthController.SendCode",
                    "OAuthController.VerifyCode",
                    "OAuthController.GoogleAuth",
                    "OAuthController.Token",
                    "WaitlistController.Join",
                    "WaitlistController.Confirm"
                ]),

            CreateCapability(
                AgentCapabilityIds.MediaUpload,
                "Upload Media",
                "Issues short-lived signed URLs so the authenticated user can upload images directly to object storage.",
                "media",
                AgentScopes.UploadMedia,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                controllerActions: ["UploadsController.SignUpload"]),

            CreateCapability(
                AgentCapabilityIds.MarketingManage,
                "Manage Marketing",
                "Sends admin marketing broadcasts and handles public one-click unsubscribe. Operator/anonymous endpoints, not agent-invocable.",
                "marketing",
                AgentScopes.ManageMarketing,
                AgentRiskClass.Low,
                isMutation: true,
                isPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                controllerActions:
                [
                    "AdminController.SendMarketingBroadcast",
                    "MarketingController.Unsubscribe",
                    "MarketingController.UnsubscribeOneClick"
                ])
        ];
    }
}

#pragma warning restore CA1861
#pragma warning restore S1192
#pragma warning restore S107
