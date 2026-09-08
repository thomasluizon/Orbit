using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Persistence;

public class OrbitDbContext : DbContext
{
    private const string JsonbColumnType = "jsonb";
    private const string EmptyJsonArraySql = "'[]'::jsonb";
    private readonly IEncryptionService? _encryptionService;

    public OrbitDbContext(DbContextOptions<OrbitDbContext> options, IEncryptionService? encryptionService = null)
        : base(options)
    {
        _encryptionService = encryptionService;
    }

    /// <summary>
    /// Indicates whether this context was constructed with an encryption service.
    /// Used by tests to distinguish cached model variants.
    /// </summary>
    public bool HasEncryptionService => _encryptionService is not null;

    public DbSet<User> Users => Set<User>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitLog> HabitLogs => Set<HabitLog>();
    public DbSet<UserFact> UserFacts => Set<UserFact>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<SentReminder> SentReminders => Set<SentReminder>();
    public DbSet<SentSlipAlert> SentSlipAlerts => Set<SentSlipAlert>();
    public DbSet<SentProactiveCheckin> SentProactiveCheckins => Set<SentProactiveCheckin>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<GoalProgressLog> GoalProgressLogs => Set<GoalProgressLog>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<UserAchievement> UserAchievements => Set<UserAchievement>();
    public DbSet<StreakFreeze> StreakFreezes => Set<StreakFreeze>();
    public DbSet<XpAwardLog> XpAwardLogs => Set<XpAwardLog>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<PendingAgentOperationState> PendingAgentOperations => Set<PendingAgentOperationState>();
    public DbSet<PendingClarification> PendingClarifications => Set<PendingClarification>();
    public DbSet<AgentStepUpChallengeState> AgentStepUpChallenges => Set<AgentStepUpChallengeState>();
    public DbSet<AgentAuditLog> AgentAuditLogs => Set<AgentAuditLog>();
    public DbSet<DistributedRateLimitBucket> DistributedRateLimitBuckets => Set<DistributedRateLimitBucket>();
    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();
    public DbSet<AppFeatureFlag> AppFeatureFlags => Set<AppFeatureFlag>();
    public DbSet<ContentBlock> ContentBlocks => Set<ContentBlock>();
    public DbSet<GoogleCalendarSyncSuggestion> GoogleCalendarSyncSuggestions => Set<GoogleCalendarSyncSuggestion>();
    public DbSet<ProcessedPlayNotification> ProcessedPlayNotifications => Set<ProcessedPlayNotification>();
    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents => Set<ProcessedStripeEvent>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();
    public DbSet<AiFactExtractionBatch> AiFactExtractionBatches => Set<AiFactExtractionBatch>();
    public DbSet<AiUsageDaily> AiUsageDaily => Set<AiUsageDaily>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Cheer> Cheers => Set<Cheer>();
    public DbSet<BlockedUser> BlockedUsers => Set<BlockedUser>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<FriendFeedEvent> FriendFeedEvents => Set<FriendFeedEvent>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<ChallengeParticipant> ChallengeParticipants => Set<ChallengeParticipant>();
    public DbSet<ChallengeParticipantHabit> ChallengeParticipantHabits => Set<ChallengeParticipantHabit>();
    public DbSet<AccountabilityPair> AccountabilityPairs => Set<AccountabilityPair>();
    public DbSet<AccountabilityPairHabit> AccountabilityPairHabits => Set<AccountabilityPairHabit>();
    public DbSet<AccountabilityCheckIn> AccountabilityCheckIns => Set<AccountabilityCheckIn>();
    public DbSet<ClosedMonthRecap> ClosedMonthRecaps => Set<ClosedMonthRecap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var isPostgres = Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        EncryptionValueConverter? encConverter = null;
        NullableEncryptionValueConverter? nullableEncConverter = null;

        if (_encryptionService is not null)
        {
            encConverter = new EncryptionValueConverter(_encryptionService);
            nullableEncConverter = new NullableEncryptionValueConverter(_encryptionService);
        }
        ConfigureUserEntity(modelBuilder, nullableEncConverter);
        ConfigureHabitEntity(modelBuilder, isPostgres, encConverter, nullableEncConverter);

        if (isPostgres)
            ConfigureConcurrencyTokens(modelBuilder);
        ConfigureHabitLogEntity(modelBuilder, nullableEncConverter);
        ConfigureUserFactEntity(modelBuilder, encConverter);
        ConfigureGoogleCalendarSyncSuggestionEntity(modelBuilder, encConverter, nullableEncConverter);
        ConfigureTagEntity(modelBuilder);
        ConfigurePushSubscriptionEntity(modelBuilder);
        ConfigureSentReminderEntity(modelBuilder);
        ConfigureSentSlipAlertEntity(modelBuilder);
        ConfigureSentProactiveCheckinEntity(modelBuilder);
        ConfigureProcessedPlayNotificationEntity(modelBuilder);
        ConfigureProcessedStripeEventEntity(modelBuilder);
        ConfigureProcessedRequestEntity(modelBuilder);
        ConfigureAiFactExtractionBatchEntity(modelBuilder);
        ConfigureAiUsageDailyEntity(modelBuilder);
        ConfigureNotificationEntity(modelBuilder);
        ConfigureGoalEntity(modelBuilder, encConverter, nullableEncConverter);
        ConfigureGoalProgressLogEntity(modelBuilder, nullableEncConverter);
        ConfigureReferralEntity(modelBuilder);
        ConfigureUserAchievementEntity(modelBuilder);
        ConfigureStreakFreezeEntity(modelBuilder);
        ConfigureXpAwardLogEntity(modelBuilder);
        ConfigureUserSessionEntity(modelBuilder);
        ConfigureApiKeyEntity(modelBuilder);
        ConfigurePendingAgentOperationEntity(modelBuilder);
        ConfigureAgentStepUpChallengeEntity(modelBuilder);
        ConfigurePendingClarificationEntity(modelBuilder);
        ConfigureAgentAuditLogEntity(modelBuilder);
        ConfigureDistributedRateLimitBucketEntity(modelBuilder);
        ConfigureAppConfigEntity(modelBuilder);
        ConfigureChecklistTemplateEntity(modelBuilder);
        ConfigureAppFeatureFlagEntity(modelBuilder);
        ConfigureContentBlockEntity(modelBuilder);
        ConfigureFriendshipEntity(modelBuilder);
        ConfigureCheerEntity(modelBuilder);
        ConfigureBlockedUserEntity(modelBuilder);
        ConfigureReportEntity(modelBuilder);
        ConfigureFriendFeedEventEntity(modelBuilder);
        ConfigureChallengeEntity(modelBuilder);
        ConfigureChallengeParticipantEntity(modelBuilder);
        ConfigureChallengeParticipantHabitEntity(modelBuilder);
        ConfigureAccountabilityPairEntity(modelBuilder);
        ConfigureAccountabilityPairHabitEntity(modelBuilder);
        ConfigureAccountabilityCheckInEntity(modelBuilder);
        ConfigureClosedMonthRecapEntity(modelBuilder);
    }

    private static void ConfigureClosedMonthRecapEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClosedMonthRecap>(entity =>
        {
            entity.HasIndex(recap => new { recap.UserId, recap.DateFrom, recap.DateTo }).IsUnique();
            entity.Property(recap => recap.ResponseJson).HasColumnType(JsonbColumnType).IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(recap => recap.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Maps the Postgres <c>xmin</c> system column as an optimistic-concurrency token on the
    /// entities with a read-modify-write path that loses updates under concurrency: <see cref="User"/>
    /// (ad-reward grant + AI message counter), <see cref="Goal"/> (progress accumulation),
    /// <see cref="Referral"/> (one-time reward-grant claim), and <see cref="UserSession"/> (refresh-token
    /// rotation — two concurrent refreshes of the same token must not both rotate). A conflicting
    /// concurrent write makes SaveChanges throw <c>DbUpdateConcurrencyException</c>, which command
    /// handlers retry against fresh state or treat as an already-claimed no-op. Uses the system column,
    /// so it adds no schema. Postgres-only because <c>xmin</c> does not exist on the in-memory/SQLite
    /// test providers.
    /// </summary>
    private static void ConfigureConcurrencyTokens(ModelBuilder modelBuilder)
    {
        MapXminToken(modelBuilder.Entity<User>());
        MapXminToken(modelBuilder.Entity<Goal>());
        MapXminToken(modelBuilder.Entity<Referral>());
        MapXminToken(modelBuilder.Entity<UserSession>());
    }

    private static void MapXminToken<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : class
    {
        entity.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }

    private static void ConfigureTagEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Name }).IsUnique().HasFilter("\"IsDeleted\" = FALSE");
            entity.HasIndex(t => new { t.UserId, t.IsDeleted });
            entity.HasIndex(t => new { t.UserId, t.UpdatedAtUtc });
            entity.HasQueryFilter(t => !t.IsDeleted);

            entity.Property(t => t.Name).IsRequired().HasMaxLength(50);
            entity.Property(t => t.Color).IsRequired().HasMaxLength(50);

            entity.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(t => t.Habits)
                .WithMany(h => h.Tags)
                .UsingEntity("HabitTags",
                    l => l.HasOne(typeof(Habit)).WithMany().HasForeignKey(nameof(Habit) + nameof(Habit.Id)).OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(Tag)).WithMany().HasForeignKey(nameof(Tag) + nameof(Tag.Id)).OnDelete(DeleteBehavior.Cascade));
        });
    }

    private static void ConfigurePushSubscriptionEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PushSubscription>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => s.Endpoint).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureSentReminderEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SentReminder>(entity =>
        {
            entity.HasIndex(r => new { r.HabitId, r.Date, r.MinutesBefore, r.ReminderTimeUtc, r.When })
                .IsUnique()
                .AreNullsDistinct(false);
        });
    }

    private static void ConfigureSentSlipAlertEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SentSlipAlert>(entity =>
        {
            entity.HasIndex(a => new { a.HabitId, a.WeekStart }).IsUnique();
        });
    }

    private static void ConfigureSentProactiveCheckinEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SentProactiveCheckin>(entity =>
        {
            entity.HasIndex(a => new { a.UserId, a.Date }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureProcessedPlayNotificationEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedPlayNotification>(entity =>
        {
            entity.HasIndex(p => p.MessageId).IsUnique();
            entity.Property(p => p.MessageId).IsRequired().HasMaxLength(255);
        });
    }

    private static void ConfigureProcessedStripeEventEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedStripeEvent>(entity =>
        {
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.Property(e => e.EventId).IsRequired().HasMaxLength(255);
        });
    }

    private static void ConfigureProcessedRequestEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedRequest>(entity =>
        {
            entity.HasIndex(request => new { request.UserId, request.IdempotencyKey, request.RequestType }).IsUnique();
            entity.HasIndex(request => request.CreatedAtUtc);
            entity.Property(request => request.IdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(request => request.RequestType).IsRequired().HasMaxLength(256);
            entity.HasOne<User>().WithMany().HasForeignKey(request => request.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAiFactExtractionBatchEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiFactExtractionBatch>(entity =>
        {
            entity.HasIndex(b => b.Status);
            entity.HasIndex(b => b.BatchId).IsUnique();
            entity.Property(b => b.BatchId).IsRequired().HasMaxLength(255);
            entity.Property(b => b.InputFileId).IsRequired().HasMaxLength(255);
            entity.Property(b => b.OutputFileId).HasMaxLength(255);
            entity.Property(b => b.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<User>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAiUsageDailyEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiUsageDaily>(entity =>
        {
            entity.HasIndex(u => new { u.Date, u.Model, u.Purpose, u.UserId })
                .IsUnique()
                .AreNullsDistinct(false);
            entity.Property(u => u.Model).HasMaxLength(64);
            entity.Property(u => u.Purpose).HasMaxLength(64);
            entity.Property(u => u.CostUsd).HasColumnType("numeric");
        });
    }

    private static void ConfigureNotificationEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasIndex(n => new { n.UserId, n.IsRead });
            entity.HasIndex(n => new { n.UserId, n.CreatedAtUtc }).IsDescending(false, true);
            entity.HasIndex(n => n.Url).HasFilter("\"Url\" IS NOT NULL");
            entity.HasIndex(n => n.DedupeKey).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
            entity.HasIndex(n => new { n.UserId, n.IsDeleted });
            entity.HasIndex(n => new { n.UserId, n.UpdatedAtUtc });
            entity.HasQueryFilter(n => !n.IsDeleted);
        });
    }

    private static void ConfigureReferralEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Referral>(entity =>
        {
            entity.HasIndex(r => r.ReferrerId);
            entity.HasIndex(r => r.ReferredUserId).IsUnique();
        });
    }

    private static void ConfigureUserAchievementEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAchievement>(entity =>
        {
            entity.HasIndex(ua => new { ua.UserId, ua.AchievementId }).IsUnique();
            entity.HasIndex(ua => ua.UserId);
            entity.Property(ua => ua.AchievementId).HasMaxLength(50);
        });
    }

    private static void ConfigureStreakFreezeEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StreakFreeze>(entity =>
        {
            entity.HasIndex(sf => new { sf.UserId, sf.UsedOnDate }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(sf => sf.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureXpAwardLogEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<XpAwardLog>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.AwardedAtUtc });
            entity.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureUserSessionEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasIndex(s => s.TokenHash).IsUnique();
            entity.HasIndex(s => s.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(s => s.TokenHash).HasMaxLength(128);
        });
    }

    private static void ConfigureApiKeyEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasIndex(k => k.KeyPrefix);
            entity.HasIndex(k => k.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(k => k.Name).HasMaxLength(50);
            entity.Property(k => k.KeyPrefix).HasMaxLength(12);
            entity.Property(k => k.Scopes)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<string>());
        });
    }

    private static void ConfigurePendingAgentOperationEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingAgentOperationState>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.CapabilityId });
            entity.HasIndex(item => new { item.UserId, item.OperationFingerprint });
            entity.Property(item => item.CapabilityId).HasMaxLength(100);
            entity.Property(item => item.OperationId).HasMaxLength(100);
            entity.Property(item => item.ArgumentsJson).HasColumnType(JsonbColumnType);
            entity.Property(item => item.DisplayName).HasMaxLength(200);
            entity.Property(item => item.Summary).HasMaxLength(500);
            entity.Property(item => item.OperationFingerprint).HasMaxLength(256);
            entity.Property(item => item.ConfirmationTokenHash).HasMaxLength(64);
            entity.Property(item => item.Surface).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.RiskClass).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ConfirmationRequirement).HasConversion<string>().HasMaxLength(32);
        });
    }

    private static void ConfigureAgentStepUpChallengeEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentStepUpChallengeState>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.PendingOperationId, item.CreatedAtUtc });
            entity.Property(item => item.CodeHash).HasMaxLength(64);
        });
    }

    private static void ConfigurePendingClarificationEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PendingClarification>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.CreatedAtUtc });
            entity.HasIndex(item => item.ExpiresAtUtc);
            entity.Property(item => item.ToolName).HasMaxLength(100);
            entity.Property(item => item.MissingArgumentKey).HasMaxLength(100);
            entity.Property(item => item.Question).HasMaxLength(500);
            entity.Property(item => item.PartialArgumentsJson).HasColumnType(JsonbColumnType);
            entity.Property(item => item.QuickActionsJson).HasColumnType(JsonbColumnType);
            entity.Property(item => item.ResolvedAtUtc).HasField("_resolvedAtUtc");
            entity.HasOne<User>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAgentAuditLogEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentAuditLog>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.CreatedAtUtc });
            entity.HasIndex(item => new { item.CapabilityId, item.CreatedAtUtc });
            entity.Property(item => item.CapabilityId).HasMaxLength(100);
            entity.Property(item => item.SourceName).HasMaxLength(100);
            entity.Property(item => item.CorrelationId).HasMaxLength(100);
            entity.Property(item => item.TargetId).HasMaxLength(100);
            entity.Property(item => item.TargetName).HasMaxLength(200);
            entity.Property(item => item.RedactedArguments).HasMaxLength(4000);
            entity.Property(item => item.Error).HasMaxLength(500);
            entity.Property(item => item.ShadowReason).HasMaxLength(500);
            entity.Property(item => item.Surface).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.AuthMethod).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.RiskClass).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.PolicyDecision).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.OutcomeStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ShadowPolicyDecision).HasConversion<string>().HasMaxLength(32);
        });
    }

    private static void ConfigureDistributedRateLimitBucketEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DistributedRateLimitBucket>(entity =>
        {
            entity.HasIndex(item => new { item.PolicyName, item.PartitionKey, item.WindowStartUtc }).IsUnique();
            entity.Property(item => item.PolicyName).HasMaxLength(64);
            entity.Property(item => item.PartitionKey).HasMaxLength(256);
        });
    }

    private static void ConfigureAppConfigEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppConfig>(entity =>
        {
            entity.HasKey(c => c.Key);
            entity.Property(c => c.Key).HasMaxLength(100);
            entity.Property(c => c.Value).HasMaxLength(500).IsRequired();
            entity.Property(c => c.Description).HasMaxLength(500);

            entity.HasData(
                AppConfig.Create("MaxUserFacts", "50", "Maximum number of facts the AI can remember per user"),
                AppConfig.Create("MaxHabitDepth", "5", "Maximum nesting depth for sub-habits"),
                AppConfig.Create("MaxTagsPerHabit", "5", "Maximum number of tags per habit"),
                AppConfig.Create("ReferralRewardDays", "10", "Days of Pro added per successful referral"),
                AppConfig.Create("MaxReferrals", "10", "Maximum successful referrals per user"),
                AppConfig.Create("MinSupportedVersion", "1.3.11", "Minimum supported client app version; clients below this receive HTTP 426"),
                AppConfig.Create("RequireApiKeyCreationStepUp", "false", "Turn on once a client build carrying the API key creation challenge flow is live in the Play fleet"));
        });
    }

    private static void ConfigureChecklistTemplateEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChecklistTemplate>(entity =>
        {
            entity.HasIndex(ct => ct.UserId);
            entity.HasIndex(ct => new { ct.UserId, ct.IsDeleted });
            entity.HasIndex(ct => new { ct.UserId, ct.UpdatedAtUtc });
            entity.HasQueryFilter(ct => !ct.IsDeleted);
            entity.HasOne<User>().WithMany().HasForeignKey(ct => ct.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(ct => ct.Name).HasMaxLength(100);
            entity.Property(ct => ct.Items)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(
                    new ValueComparer<IReadOnlyList<string>>(
                        (l1, l2) => JsonSerializer.Serialize(l1, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(l2, (JsonSerializerOptions?)null),
                        c => JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
                        c => JsonSerializer.Deserialize<List<string>>(JsonSerializer.Serialize(c, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!));
        });
    }

    private static void ConfigureAppFeatureFlagEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppFeatureFlag>(entity =>
        {
            entity.HasKey(f => f.Key);
            entity.Property(f => f.Key).HasMaxLength(100);
            entity.Property(f => f.Description).HasMaxLength(500);
            entity.Property(f => f.PlanRequirement).HasMaxLength(50);

            entity.HasData(
                new { Key = "offline_mode", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Enable offline mode with background sync", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_chat", Enabled = true, PlanRequirement = (string?)"Free", Description = (string?)"AI chat assistant", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_summary", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"AI daily summary", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_retrospective", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"AI retrospective analysis", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "sub_habits", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Sub-habit nesting", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "goal_tracking", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Goal tracking with progress", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "push_notifications", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Push notification reminders", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "scheduled_reminders", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Custom scheduled reminders", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "slip_alerts", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Slip detection alerts", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "checklist_templates", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Reusable checklist templates", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "habit_duplication", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Duplicate habits", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "bulk_operations", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Bulk create/delete/log habits", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "calendar_integration", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Google Calendar integration", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "api_keys", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Personal API keys", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) });
        });
    }

    private static void ConfigureContentBlockEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContentBlock>(entity =>
        {
            entity.HasIndex(cb => new { cb.Key, cb.Locale }).IsUnique();
            entity.Property(cb => cb.Key).HasMaxLength(100);
            entity.Property(cb => cb.Locale).HasMaxLength(10);
            entity.Property(cb => cb.Category).HasMaxLength(50);
        });
    }

    private static void ConfigureFriendshipEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Friendship>(entity =>
        {
            entity.HasIndex(f => f.RequesterId);
            entity.HasIndex(f => f.AddresseeId);
            entity.Property(f => f.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<User>().WithMany().HasForeignKey(f => f.RequesterId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(f => f.AddresseeId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureCheerEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Cheer>(entity =>
        {
            entity.HasIndex(c => c.RecipientId);
            entity.HasIndex(c => new { c.SenderId, c.CreatedAtUtc });
            entity.HasIndex(c => c.HabitId);
            entity.Property(c => c.Note).HasMaxLength(DomainConstants.MaxCheerNoteLength);
            entity.HasOne<User>().WithMany().HasForeignKey(c => c.SenderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(c => c.RecipientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Habit>().WithMany().HasForeignKey(c => c.HabitId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureBlockedUserEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BlockedUser>(entity =>
        {
            entity.HasIndex(b => new { b.BlockerId, b.BlockedId }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(b => b.BlockerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(b => b.BlockedId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureReportEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Report>(entity =>
        {
            entity.HasIndex(r => r.ReportedUserId);
            entity.HasIndex(r => r.Status);
            entity.Property(r => r.Reason).HasConversion<string>().HasMaxLength(32);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(r => r.Details).HasMaxLength(DomainConstants.MaxReportDetailsLength);
            entity.Property(r => r.ReviewedAtUtc).HasField("_reviewedAtUtc");
            entity.HasOne<User>().WithMany().HasForeignKey(r => r.ReporterId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(r => r.ReportedUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Cheer>().WithMany().HasForeignKey(r => r.CheerId).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureAccountabilityPairEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountabilityPair>(entity =>
        {
            entity.HasIndex(p => p.RequesterId);
            entity.HasIndex(p => p.AddresseeId);
            entity.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(p => p.Cadence).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<User>().WithMany().HasForeignKey(p => p.RequesterId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(p => p.AddresseeId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAccountabilityPairHabitEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountabilityPairHabit>(entity =>
        {
            entity.HasIndex(ph => new { ph.PairId, ph.UserId, ph.HabitId }).IsUnique();
            entity.HasIndex(ph => ph.HabitId);
            entity.HasOne<AccountabilityPair>().WithMany().HasForeignKey(ph => ph.PairId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Habit>().WithMany().HasForeignKey(ph => ph.HabitId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAccountabilityCheckInEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountabilityCheckIn>(entity =>
        {
            entity.HasIndex(c => new { c.PairId, c.UserId, c.Date }).IsUnique();
            entity.HasIndex(c => new { c.PairId, c.CreatedAtUtc });
            entity.Property(c => c.Note).HasMaxLength(DomainConstants.MaxAccountabilityNoteLength);
            entity.HasOne<AccountabilityPair>().WithMany().HasForeignKey(c => c.PairId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureFriendFeedEventEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FriendFeedEvent>(entity =>
        {
            entity.HasIndex(e => new { e.ActorUserId, e.CreatedAtUtc, e.Id })
                .IsDescending(false, true, true);
            entity.HasIndex(e => new { e.ActorUserId, e.AchievementId })
                .IsUnique()
                .HasFilter("\"AchievementId\" IS NOT NULL");
            entity.HasIndex(e => new { e.ActorUserId, e.Type, e.Value })
                .IsUnique()
                .HasFilter("\"AchievementId\" IS NULL");
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.AchievementId).HasMaxLength(50);
            entity.HasOne<User>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureChallengeEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Challenge>(entity =>
        {
            entity.HasIndex(c => c.CreatorId);
            entity.HasIndex(c => c.JoinCode).IsUnique();
            entity.HasQueryFilter(c => !c.IsDeleted);
            entity.Property(c => c.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(c => c.JoinCode).HasMaxLength(16);
            entity.Property(c => c.Title).HasMaxLength(Orbit.Application.Common.AppConstants.MaxChallengeTitleLength);
            entity.Property(c => c.Description).HasMaxLength(Orbit.Application.Common.AppConstants.MaxChallengeDescriptionLength);
            entity.HasOne<User>().WithMany().HasForeignKey(c => c.CreatorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(c => c.Participants)
                .WithOne()
                .HasForeignKey(p => p.ChallengeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureChallengeParticipantEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChallengeParticipant>(entity =>
        {
            entity.HasIndex(p => new { p.ChallengeId, p.UserId })
                .IsUnique()
                .HasFilter("\"LeftAtUtc\" IS NULL");
            entity.HasIndex(p => p.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(p => p.LinkedHabits)
                .WithOne()
                .HasForeignKey(h => h.ChallengeParticipantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureChallengeParticipantHabitEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChallengeParticipantHabit>(entity =>
        {
            entity.HasIndex(h => new { h.ChallengeParticipantId, h.HabitId }).IsUnique();
            entity.HasIndex(h => h.HabitId);
            entity.HasOne<Habit>().WithMany().HasForeignKey(h => h.HabitId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureUserEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasQueryFilter(u => !u.IsDeactivated);

            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasIndex(u => u.ReferralCode).IsUnique().HasFilter("\"ReferralCode\" IS NOT NULL");
            entity.HasIndex(u => u.PlayPurchaseToken).IsUnique().HasFilter("\"PlayPurchaseToken\" IS NOT NULL");
            entity.HasIndex(u => u.PublicProfileSlug).IsUnique().HasFilter("\"PublicProfileSlug\" IS NOT NULL");

            entity.Property(u => u.Handle).HasMaxLength(DomainConstants.HandleMaxLength);
            entity.Property(u => u.PublicProfileSlug).HasMaxLength(32);

            entity.Property(u => u.ColorScheme).HasMaxLength(50);
            entity.Property(u => u.Language).HasMaxLength(10);
            entity.Property(u => u.ThemePreference).HasMaxLength(10);
            entity.Property(u => u.TimeZone).HasMaxLength(100);

            entity.Property(u => u.GoogleCalendarAutoSyncStatus)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(u => u.SubscriptionLapseReason)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(u => u.GoogleCalendarLastSyncError).HasMaxLength(500);

            entity.Property(u => u.GoogleCalendarSelectedIds).HasColumnType("text");

            entity.HasIndex(u => new { u.GoogleCalendarAutoSyncEnabled, u.GoogleCalendarLastSyncedAt })
                .HasFilter("\"GoogleCalendarAutoSyncEnabled\" = TRUE");

            if (nullableEncConverter is null)
                return;

            entity.Property(u => u.GoogleAccessToken).HasConversion(nullableEncConverter).HasColumnType("text");
            entity.Property(u => u.GoogleRefreshToken).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureGoogleCalendarSyncSuggestionEntity(
        ModelBuilder modelBuilder,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<GoogleCalendarSyncSuggestion>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => new { s.UserId, s.GoogleEventId }).IsUnique();
            entity.HasIndex(s => new { s.UserId, s.DismissedAtUtc, s.ImportedAtUtc });

            entity.Property(s => s.GoogleEventId).HasMaxLength(1024).IsRequired();

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(s => s.Title).HasConversion(encConverter).HasColumnType("text");
            entity.Property(s => s.RawEventJson).HasConversion(encConverter).HasColumnType("text");
        });
    }

    private static void ConfigureHabitEntity(
        ModelBuilder modelBuilder,
        bool usePostgresArrayColumns,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<Habit>(entity =>
        {
            entity.HasIndex(h => h.UserId);
            entity.HasIndex(h => new { h.UserId, h.IsDeleted });
            entity.HasIndex(h => new { h.UserId, h.UpdatedAtUtc });
            entity.HasQueryFilter(h => !h.IsDeleted);

            entity.HasOne<User>().WithMany().HasForeignKey(h => h.UserId).OnDelete(DeleteBehavior.Cascade);

            entity.Property(h => h.GoogleEventId).HasMaxLength(1024);
            entity.Property(h => h.IntervalWeeks).HasColumnType("integer");
            entity.HasIndex(h => new { h.UserId, h.GoogleEventId })
                .HasFilter("\"GoogleEventId\" IS NOT NULL AND \"IsDeleted\" = FALSE")
                .IsUnique();

            entity.HasMany(h => h.Logs)
                .WithOne()
                .HasForeignKey(l => l.HabitId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(h => h.Children)
                .WithOne()
                .HasForeignKey(h => h.ParentHabitId)
                .OnDelete(DeleteBehavior.Cascade);

            ConfigureHabitDaysProperty(entity, usePostgresArrayColumns);

            entity.Property(h => h.ChecklistItems)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<ChecklistItem>()))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<ChecklistItem>());

            entity.Property(h => h.ReminderTimes)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<int> { 15 }))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql("'[15]'::jsonb")
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<int>());

            entity.Property(h => h.ScheduledReminders)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<ScheduledReminderTime>()))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<ScheduledReminderTime>());

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(h => h.Title).HasConversion(encConverter).HasColumnType("text");
            entity.Property(h => h.Description).HasConversion(nullableEncConverter).HasColumnType("text");
            entity.Property(h => h.Emoji).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureHabitDaysProperty(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Habit> entity,
        bool usePostgresArrayColumns)
    {
        var property = entity.Property(h => h.Days);

        if (usePostgresArrayColumns)
        {
            property.HasConversion(
                    v => v.ToList(),
                    v => v.ToList())
                .HasColumnType("text[]");
        }
        else
        {
            property.HasConversion(
                    v => SerializeDays(v),
                    v => DeserializeDays(v))
                .HasColumnType("text");
        }

        property.Metadata.SetValueComparer(CreateDayOfWeekCollectionComparer());
    }

    private static void ConfigureHabitLogEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<HabitLog>(entity =>
        {
            entity.HasIndex(l => new { l.HabitId, l.Date }, "IX_HabitLogs_HabitId_Date");

            entity.HasIndex(l => new { l.HabitId, l.Date }, "IX_HabitLogs_HabitId_Date_Completed")
                .HasFilter("\"Value\" > 0 AND NOT \"IsDeleted\"")
                .IsUnique();

            entity.HasIndex(l => new { l.HabitId, l.UpdatedAtUtc });

            entity.HasQueryFilter(l => !l.IsDeleted);

            if (nullableEncConverter is null)
                return;

            entity.Property(l => l.Note).HasConversion(nullableEncConverter).HasColumnType("text").HasMaxLength(1000);
        });
    }

    private static void ConfigureUserFactEntity(ModelBuilder modelBuilder, EncryptionValueConverter? encConverter)
    {
        modelBuilder.Entity<UserFact>(entity =>
        {
            entity.HasIndex(f => new { f.UserId, f.IsDeleted });
            entity.HasQueryFilter(f => !f.IsDeleted);

            if (encConverter is null)
                return;

            entity.Property(f => f.FactText).HasConversion(encConverter).HasColumnType("text");
        });
    }

    private static void ConfigureGoalEntity(
        ModelBuilder modelBuilder,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<Goal>(entity =>
        {
            entity.Ignore(g => g.HasActiveLinkedHabits);
            entity.Ignore(g => g.IsProgressDerived);
            entity.HasIndex(g => g.UserId);
            entity.HasIndex(g => new { g.UserId, g.IsDeleted });
            entity.HasIndex(g => new { g.UserId, g.UpdatedAtUtc });
            entity.HasQueryFilter(g => !g.IsDeleted);

            entity.Property(g => g.Unit).HasMaxLength(50);

            entity.HasMany(g => g.ProgressLogs)
                .WithOne()
                .HasForeignKey(l => l.GoalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(g => g.Habits)
                .WithMany(h => h.Goals)
                .UsingEntity("HabitGoals",
                    l => l.HasOne(typeof(Habit)).WithMany().HasForeignKey(nameof(Habit) + nameof(Habit.Id)).OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(Goal)).WithMany().HasForeignKey(nameof(Goal) + nameof(Goal.Id)).OnDelete(DeleteBehavior.Cascade));

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(g => g.Title).HasConversion(encConverter).HasColumnType("text").HasMaxLength(200);
            entity.Property(g => g.Description).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureGoalProgressLogEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<GoalProgressLog>(entity =>
        {
            entity.HasIndex(l => l.GoalId);
            entity.HasIndex(l => new { l.GoalId, l.IsDeleted });
            entity.HasIndex(l => new { l.GoalId, l.UpdatedAtUtc });
            entity.HasQueryFilter(l => !l.IsDeleted);

            if (nullableEncConverter is null)
                return;

            entity.Property(l => l.Note).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
#pragma warning disable ORBIT0004
        var now = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        foreach (var entry in ChangeTracker.Entries<ITimestamped>()
            .Where(e => e.State is EntityState.Modified or EntityState.Added))
        {
            entry.Entity.UpdatedAtUtc = now;
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    private static string SerializeDays(ICollection<System.DayOfWeek> days)
    {
        return JsonSerializer.Serialize(days.Select(day => (int)day).ToList(), (JsonSerializerOptions?)null);
    }

    private static string SerializeJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, (JsonSerializerOptions?)null);
    }

    private static List<T> DeserializeJson<T>(string value, List<T> fallback)
    {
        return JsonSerializer.Deserialize<List<T>>(value, (JsonSerializerOptions?)null) ?? fallback;
    }

    private static List<System.DayOfWeek> DeserializeDays(string value)
    {
        var days = JsonSerializer.Deserialize<List<int>>(value, (JsonSerializerOptions?)null);
        return days?.Select(day => (System.DayOfWeek)day).ToList() ?? new List<System.DayOfWeek>();
    }

    private static ValueComparer<ICollection<System.DayOfWeek>> CreateDayOfWeekCollectionComparer()
    {
        return new ValueComparer<ICollection<System.DayOfWeek>>(
            (left, right) => left!.SequenceEqual(right!),
            collection => collection.Aggregate(0, (hash, day) => HashCode.Combine(hash, day.GetHashCode())),
            collection => collection.ToList());
    }

    private static ValueComparer<IReadOnlyList<T>> CreateReadOnlyListComparer<T>()
    {
        return new ValueComparer<IReadOnlyList<T>>(
            (left, right) => SerializeJson(left) == SerializeJson(right),
            collection => SerializeJson(collection).GetHashCode(),
            collection => DeserializeJson(SerializeJson(collection), new List<T>()));
    }
}
