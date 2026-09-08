using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public partial class User : Entity
{
    private const int DefaultStreakDaysPerFreeze = 7;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,20}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HandleRegex();

    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string? TimeZone { get; private set; }
    public bool AiMemoryEnabled { get; private set; } = true;
    public bool AiSummaryEnabled { get; private set; } = true;
    public bool ProactiveAstraEnabled { get; private set; } = false;
    public bool HasCompletedOnboarding { get; private set; } = false;
    public bool HasCompletedTour { get; private set; } = false;
    public bool HasCreatedFirstHabit { get; private set; } = false;
    public bool HasLoggedFirstHabit { get; private set; } = false;
    public bool HasTriedAstra { get; private set; } = false;
    public bool HasCompletedOnboardingChecklist { get; private set; } = false;
    public string? Language { get; private set; }
    public UserPlan Plan { get; private set; } = UserPlan.Free;
    public string? StripeCustomerId { get; private set; }
    public string? StripeSubscriptionId { get; private set; }
    public DateTime? PlanExpiresAt { get; private set; }
    public DateTime? TrialEndsAt { get; private set; }
    public bool IsLifetimePro { get; private set; } = false;
    public int AiMessagesUsedToday { get; private set; } = 0;
    public DateOnly? AiMessagesLocalDate { get; private set; }
    public SubscriptionInterval? SubscriptionInterval { get; private set; }
    public SubscriptionSource? SubscriptionSource { get; private set; }
    public SubscriptionLapseReason? SubscriptionLapseReason { get; private set; }
    public DateTime? SubscriptionEndedAtUtc { get; private set; }
    public DateTime? StripeSubscriptionEventCreatedAtUtc { get; private set; }
    public string? PlayPurchaseToken { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool HasImportedCalendar { get; private set; } = false;
    public bool HasSeenImportPrompt { get; private set; } = false;
    public string? GoogleAccessToken { get; private set; }
    public string? GoogleRefreshToken { get; private set; }
    public bool GoogleCalendarAutoSyncEnabled { get; private set; }
    public string? GoogleCalendarSelectedIds { get; private set; }
    public GoogleCalendarAutoSyncStatus? GoogleCalendarAutoSyncStatus { get; private set; }
    public DateTime? GoogleCalendarLastSyncedAt { get; private set; }
    public string? GoogleCalendarLastSyncError { get; private set; }
    public DateTime? GoogleCalendarSyncReconciledAt { get; private set; }
    public bool IsDeactivated { get; private set; }
    public DateTime? DeactivatedAt { get; private set; }
    public DateTime? ScheduledDeletionAt { get; private set; }
    public int WeekStartDay { get; private set; } = 1;
    public string? ReferralCode { get; private set; }
    public Guid? ReferredByUserId { get; private set; }
    public string? Handle { get; private set; }
    public bool SocialOptIn { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool? MarketingEmailConsent { get; private set; }
    public DateTime? MarketingConsentUpdatedAtUtc { get; private set; }
    public int TotalXp { get; private set; } = 0;
    public int Level { get; private set; } = 1;
    public string? ReferralCouponId { get; private set; }
    public int AdRewardBonusMessages { get; private set; } = 0;
    public DateTime? LastAdRewardAt { get; private set; }
    public DateOnly? LastAdRewardLocalDate { get; private set; }
    public int AdRewardsClaimedToday { get; private set; } = 0;
    public int CurrentStreak { get; private set; } = 0;
    public int LongestStreak { get; private set; } = 0;
    public DateOnly? LastActiveDate { get; private set; }
    public int StreakFreezesAccumulated { get; private set; } = 0;
    public int LastFreezeAwardStreak { get; private set; } = 0;
    public int? PreGapFreezeAwardStreak { get; private set; }
    public DateOnly? PreGapLastActiveDate { get; private set; }
    public string? ThemePreference { get; private set; }
    public string? ColorScheme { get; private set; }
    public string? PublicProfileSlug { get; private set; }
    public bool PublicProfileShowStreak { get; private set; } = true;
    public bool PublicProfileShowLevel { get; private set; } = true;
    public bool PublicProfileShowAchievements { get; private set; } = true;
    public bool PublicProfileShowTopHabits { get; private set; } = false;

    [NotMapped]
    public bool IsPro => IsLifetimePro || (Plan == UserPlan.Pro && PlanExpiresAt.HasValue && PlanExpiresAt.Value > DateTime.UtcNow);

    [NotMapped]
    public bool IsTrialActive => !IsPro && TrialEndsAt.HasValue && TrialEndsAt.Value > DateTime.UtcNow;

    [NotMapped]
    public bool HasProAccess => IsPro || IsTrialActive;

    private User() { }

    public static Result<User> Create(string name, string email)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<User>(DomainErrors.NameRequired);

        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<User>(DomainErrors.EmailRequired);

        var trimmedEmail = email.Trim();
        if (!EmailRegex().IsMatch(trimmedEmail))
            return Result.Failure<User>(DomainErrors.InvalidEmailFormat);

        var now = DateTime.UtcNow;
        return Result.Success(new User
        {
            Name = name.Trim(),
            Email = trimmedEmail.ToLowerInvariant(),
            CreatedAtUtc = now,
            TrialEndsAt = now.AddDays(7)
        });
    }

    public void UpdateProfile(string name, string email)
    {
        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
    }

    public Result SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(DomainErrors.NameRequired);

        var trimmedName = name.Trim();
        if (trimmedName.Length > DomainConstants.MaxUserNameLength)
            return Result.Failure(DomainErrors.NameTooLong.Format(DomainConstants.MaxUserNameLength));

        Name = trimmedName;
        return Result.Success();
    }

    public Result SetTimeZone(string ianaTimeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
            TimeZone = ianaTimeZoneId;
            return Result.Success();
        }
        catch (TimeZoneNotFoundException)
        {
            return Result.Failure(DomainErrors.InvalidTimezone.Format(ianaTimeZoneId));
        }
    }

    public void SetAiMemory(bool enabled) => AiMemoryEnabled = enabled;

    public void SetAiSummary(bool enabled) => AiSummaryEnabled = enabled;

    public void SetProactiveAstraEnabled(bool enabled) => ProactiveAstraEnabled = enabled;

    public void SetLanguage(string? language) => Language = language;

    /// <summary>
    /// Persists the user's Google Calendar selection as a JSON array of calendar ids.
    /// A null <see cref="GoogleCalendarSelectedIds"/> means "all owned calendars" (the
    /// default); an empty input list clears the selection back to that default.
    /// </summary>
    public void SetSelectedCalendars(IReadOnlyCollection<string> calendarIds)
    {
        var normalized = calendarIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        GoogleCalendarSelectedIds = normalized.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(normalized);
    }

    /// <summary>
    /// Deserializes <see cref="GoogleCalendarSelectedIds"/> into the user's chosen calendar
    /// ids, or null when no explicit selection exists (callers then fall back to all owned
    /// calendars). Malformed stored JSON is treated as "no selection".
    /// </summary>
    public IReadOnlyList<string>? GetSelectedCalendarIds()
    {
        if (string.IsNullOrWhiteSpace(GoogleCalendarSelectedIds))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(GoogleCalendarSelectedIds);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public Result SetThemePreference(string? preference)
    {
        if (preference is not null && preference is not ("dark" or "light"))
            return Result.Failure(DomainErrors.InvalidThemePreference);
        ThemePreference = preference;
        return Result.Success();
    }

    public Result SetColorScheme(string? colorScheme)
    {
        string[] valid = ["purple", "blue", "green", "rose", "orange", "cyan"];
        if (colorScheme is not null && !valid.Contains(colorScheme))
            return Result.Failure(DomainErrors.InvalidColorScheme);
        ColorScheme = colorScheme;
        return Result.Success();
    }

    public void CompleteOnboarding() => HasCompletedOnboarding = true;

    public void MarkFirstHabitCreated() => HasCreatedFirstHabit = true;

    public void MarkFirstHabitLogged() => HasLoggedFirstHabit = true;

    public void MarkAstraUsed() => HasTriedAstra = true;

    public void CompleteOnboardingChecklist() => HasCompletedOnboardingChecklist = true;

    public void CompleteTour() => HasCompletedTour = true;

    public void ResetTour() => HasCompletedTour = false;

    public void SetStripeCustomerId(string customerId) => StripeCustomerId = customerId;

    public void SetStripeSubscription(
        string subscriptionId,
        DateTime expiresAt,
        SubscriptionInterval? interval = null,
        DateTime? eventCreatedAtUtc = null)
    {
        if (!TryAcceptStripeEvent(eventCreatedAtUtc, acceptEqualTimestamp: true))
            return;

        StripeSubscriptionId = subscriptionId;
        PlanExpiresAt = expiresAt;
        Plan = UserPlan.Pro;
        SubscriptionSource = Enums.SubscriptionSource.Stripe;
        SubscriptionLapseReason = null;
        SubscriptionEndedAtUtc = null;
        PlayPurchaseToken = null;
        if (interval.HasValue)
            SubscriptionInterval = interval.Value;
    }

    public void SetPlaySubscription(string purchaseToken, DateTime expiresAt,
        SubscriptionInterval? interval = null, bool isInGracePeriod = false)
    {
        PlayPurchaseToken = purchaseToken;
        PlanExpiresAt = expiresAt;
        Plan = UserPlan.Pro;
        SubscriptionSource = Enums.SubscriptionSource.GooglePlay;
        SubscriptionLapseReason = isInGracePeriod ? Enums.SubscriptionLapseReason.PaymentFailed : null;
        SubscriptionEndedAtUtc = null;
        if (interval.HasValue)
            SubscriptionInterval = interval.Value;
    }

    public void LinkPlayPurchaseToken(string purchaseToken) => PlayPurchaseToken = purchaseToken;

    public void CancelStripeSubscription(
        SubscriptionLapseReason reason,
        DateTime? eventCreatedAtUtc = null)
    {
        EnsureValidLapseReason(reason);
        if (!TryAcceptStripeEvent(eventCreatedAtUtc, acceptEqualTimestamp: true))
            return;

        StripeSubscriptionId = null;
        if (SubscriptionSource == Enums.SubscriptionSource.Stripe)
            ClearEntitlement(reason);
    }

    public void CancelPlaySubscription(SubscriptionLapseReason reason)
    {
        EnsureValidLapseReason(reason);
        PlayPurchaseToken = null;
        if (SubscriptionSource == Enums.SubscriptionSource.GooglePlay)
            ClearEntitlement(reason);
    }

    public void RecordSubscriptionLapseReason(
        SubscriptionSource source,
        SubscriptionLapseReason reason,
        DateTime? eventCreatedAtUtc = null)
    {
        EnsureValidSubscriptionSource(source);
        EnsureValidLapseReason(reason);

        if (SubscriptionSource != source)
            return;

        if (source == Enums.SubscriptionSource.Stripe
            && !TryAcceptStripeEvent(eventCreatedAtUtc, acceptEqualTimestamp: false))
            return;

        SubscriptionLapseReason = reason;
    }

    private void ClearEntitlement(SubscriptionLapseReason reason)
    {
        Plan = UserPlan.Free;
        PlanExpiresAt = null;
        SubscriptionInterval = null;
        SubscriptionSource = null;
        SubscriptionLapseReason = reason;
        SubscriptionEndedAtUtc = DateTime.UtcNow;
    }

    private static void EnsureValidLapseReason(SubscriptionLapseReason reason)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
    }

    private static void EnsureValidSubscriptionSource(SubscriptionSource source)
    {
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
    }

    private bool TryAcceptStripeEvent(DateTime? eventCreatedAtUtc, bool acceptEqualTimestamp)
    {
        if (!eventCreatedAtUtc.HasValue)
            return true;

        if (eventCreatedAtUtc.Value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Stripe event time must be UTC.", nameof(eventCreatedAtUtc));

        if (StripeSubscriptionEventCreatedAtUtc.HasValue
            && (acceptEqualTimestamp
                ? eventCreatedAtUtc.Value < StripeSubscriptionEventCreatedAtUtc.Value
                : eventCreatedAtUtc.Value <= StripeSubscriptionEventCreatedAtUtc.Value))
            return false;

        StripeSubscriptionEventCreatedAtUtc = eventCreatedAtUtc.Value;
        return true;
    }

    public void StartTrial(DateTime endsAt) => TrialEndsAt = endsAt;

    public void IncrementAiMessageCount(DateOnly userToday)
    {
        if (!AiMessagesLocalDate.HasValue || AiMessagesLocalDate.Value < userToday)
        {
            AiMessagesUsedToday = 0;
            AdRewardBonusMessages = 0;
            AiMessagesLocalDate = userToday;
        }
        AiMessagesUsedToday++;
    }

    public int GetAiMessagesUsedToday(DateOnly userToday) =>
        AiMessagesLocalDate == userToday ? AiMessagesUsedToday : 0;

    public int GetAiMessagesUsedForQuota(DateOnly userToday) =>
        AiMessagesLocalDate >= userToday ? AiMessagesUsedToday : 0;

    public Result GrantAdReward(DateOnly userToday, int bonusMessages = 5, int dailyCap = 3)
    {
        if (HasProAccess)
            return Result.Failure(DomainErrors.ProUsersDoNotSeeAds);

        if (!LastAdRewardLocalDate.HasValue || LastAdRewardLocalDate.Value < userToday)
            AdRewardsClaimedToday = 0;

        if (AdRewardsClaimedToday >= dailyCap)
            return Result.Failure(DomainErrors.AdRewardLimitReached);

        AdRewardBonusMessages += bonusMessages;
        AdRewardsClaimedToday++;
        LastAdRewardAt = DateTime.UtcNow;
        LastAdRewardLocalDate = userToday;
        return Result.Success();
    }

    public void SetGoogleTokens(string accessToken, string? refreshToken)
    {
        GoogleAccessToken = accessToken;
        if (refreshToken is not null)
            GoogleRefreshToken = refreshToken;
    }

    public void MarkCalendarImported() => HasImportedCalendar = true;

    public void MarkImportPromptSeen() => HasSeenImportPrompt = true;

    public Result EnableCalendarAutoSync()
    {
        if (!HasProAccess)
            return Result.Failure(DomainErrors.CalendarAutoSyncProRequired);

        if (GoogleAccessToken is null)
            return Result.Failure(DomainErrors.CalendarAutoSyncNotConnected);

        GoogleCalendarAutoSyncEnabled = true;
        GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.Idle;
        GoogleCalendarLastSyncError = null;
        return Result.Success();
    }

    public void DisableCalendarAutoSync()
    {
        GoogleCalendarAutoSyncEnabled = false;
        GoogleCalendarLastSyncError = null;
    }

    public void MarkCalendarSyncReconnectRequired(string error)
    {
        GoogleCalendarAutoSyncEnabled = false;
        GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.ReconnectRequired;
        GoogleCalendarLastSyncError = error;
        GoogleAccessToken = null;
        GoogleRefreshToken = null;
    }

    public void MarkCalendarSyncSuccess(DateTime utcNow)
    {
        GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.Idle;
        GoogleCalendarLastSyncedAt = utcNow;
        GoogleCalendarLastSyncError = null;
    }

    public void MarkCalendarSyncTransientError(string error)
    {
        GoogleCalendarAutoSyncStatus = Enums.GoogleCalendarAutoSyncStatus.TransientError;
        GoogleCalendarLastSyncError = error;
    }

    public void MarkCalendarSyncReconciled(DateTime utcNow)
    {
        GoogleCalendarSyncReconciledAt = utcNow;
    }

    public void Deactivate(DateTime scheduledDeletion)
    {
        IsDeactivated = true;
        DeactivatedAt = DateTime.UtcNow;
        ScheduledDeletionAt = scheduledDeletion;
        GoogleAccessToken = null;
        GoogleRefreshToken = null;
        GoogleCalendarAutoSyncEnabled = false;
    }

    public void CancelDeactivation()
    {
        IsDeactivated = false;
        DeactivatedAt = null;
        ScheduledDeletionAt = null;
    }

    public Result SetWeekStartDay(int day)
    {
        if (day is not (0 or 1))
            return Result.Failure(DomainErrors.InvalidWeekStartDay);

        WeekStartDay = day;
        return Result.Success();
    }

    public void SetReferralCode(string code) => ReferralCode = code;

    public Result SetHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || !HandleRegex().IsMatch(handle))
            return Result.Failure(DomainErrors.InvalidHandle);

        Handle = handle;
        return Result.Success();
    }

    /// <summary>
    /// Assigns the deterministic, collision-free default handle (<c>user_</c> + the first 12 hex of
    /// the user's id) for a freshly created account. Bypasses format validation because the result is
    /// provably valid (17 chars, alphanumeric + underscore); the same formula backfills existing rows.
    /// </summary>
    public void SeedDefaultHandle() => Handle = $"user_{Id:N}"[..17];

    public void SetSocialOptIn(bool enabled) => SocialOptIn = enabled;

    /// <summary>
    /// Grants this user the single administrative role — the only code path that sets IsAdmin, which
    /// mints the JWT admin claim the "Admin" policy gates admin-only endpoints on. Idempotent. The
    /// first admin is bootstrapped by a direct DB update; this mutator is the seam a future in-app
    /// admin dashboard uses to grant admin.
    /// </summary>
    public void GrantAdmin() => IsAdmin = true;

    /// <summary>
    /// Grants permanent Pro entitlement that never expires and outranks any subscription or trial
    /// state (see <see cref="IsPro"/>). Idempotent. Lifetime Pro has no self-serve purchase flow; it is
    /// bootstrapped by a direct DB update, and this mutator is the domain seam a future comp/grant path
    /// (or admin dashboard) uses instead of writing the column directly.
    /// </summary>
    public void GrantLifetimePro() => IsLifetimePro = true;

    /// <summary>
    /// Records the user's explicit product-marketing-email consent decision (<c>true</c> = opted in,
    /// <c>false</c> = opted out) and stamps the decision time. A null value means never asked; once set
    /// it stays non-null so the one-time in-app consent prompt never shows again.
    /// </summary>
    public void SetMarketingConsent(bool consented)
    {
        MarketingEmailConsent = consented;
        MarketingConsentUpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Sets (or clears, when null) the opaque token that addresses the user's public shareable
    /// profile at <c>/u/{slug}</c>. A null slug means the public profile is disabled.
    /// </summary>
    public void SetPublicProfileSlug(string? slug) => PublicProfileSlug = slug;

    /// <summary>
    /// Sets which fields the public profile exposes. Each flag gates one section of the no-auth
    /// projection; a field whose flag is false is omitted server-side, never just hidden client-side.
    /// </summary>
    public void SetPublicProfileVisibility(bool showStreak, bool showLevel, bool showAchievements, bool showTopHabits)
    {
        PublicProfileShowStreak = showStreak;
        PublicProfileShowLevel = showLevel;
        PublicProfileShowAchievements = showAchievements;
        PublicProfileShowTopHabits = showTopHabits;
    }

    public void SetReferredBy(Guid referrerUserId) => ReferredByUserId = referrerUserId;

    public void ExtendTrial(int days)
    {
        if (TrialEndsAt is null || TrialEndsAt.Value < DateTime.UtcNow)
            TrialEndsAt = DateTime.UtcNow.AddDays(days);
        else
            TrialEndsAt = TrialEndsAt.Value.AddDays(days);
    }

    public void SetReferralCoupon(string? couponId) => ReferralCouponId = couponId;

    public void AddXp(int amount)
    {
        if (amount <= 0) return;
        TotalXp += amount;
    }

    public void SetLevel(int level)
    {
        if (level < 1) return;
        Level = level;
    }

    public void UpdateStreak(DateOnly today)
    {
        if (LastActiveDate == today)
            return;
        if (LastActiveDate == today.AddDays(-1))
        {
            CurrentStreak++;
        }
        else
        {
            CurrentStreak = 1;
        }

        LastActiveDate = today;

        if (CurrentStreak > LongestStreak)
            LongestStreak = CurrentStreak;
    }

    public void SetStreakState(int currentStreak, int longestStreak, DateOnly? lastActiveDate)
    {
        var normalizedStreak = Math.Max(0, currentStreak);
        if (normalizedStreak < CurrentStreak)
        {
            PreGapFreezeAwardStreak = LastFreezeAwardStreak;
            PreGapLastActiveDate = LastActiveDate;
            LastFreezeAwardStreak = 0;
        }
        CurrentStreak = normalizedStreak;
        LongestStreak = Math.Max(CurrentStreak, longestStreak);
        LastActiveDate = lastActiveDate;
    }

    public void RestoreStreakAfterGapRepair(
        int currentStreak, int longestStreak, DateOnly? lastActiveDate, DateOnly precedingDate)
    {
        var awardCursor = PreGapLastActiveDate == precedingDate && PreGapFreezeAwardStreak.HasValue
            ? PreGapFreezeAwardStreak.Value
            : GetEligibleFreezeAwardStreak(currentStreak, DefaultStreakDaysPerFreeze);
        SetStreakState(currentStreak, longestStreak, lastActiveDate);
        LastFreezeAwardStreak = awardCursor;
        PreGapFreezeAwardStreak = null;
        PreGapLastActiveDate = null;
    }

    public bool AwardStreakFreezeIfEligible(
        int maxAccumulated = 3, int daysPerFreeze = DefaultStreakDaysPerFreeze)
    {
        if (CurrentStreak < daysPerFreeze)
            return false;

        var eligibleMilestone = GetEligibleFreezeAwardStreak(CurrentStreak, daysPerFreeze);

        if (StreakFreezesAccumulated >= maxAccumulated)
        {
            LastFreezeAwardStreak = eligibleMilestone;
            return false;
        }

        if (eligibleMilestone <= LastFreezeAwardStreak)
            return false;

        var milestonesCrossed = (eligibleMilestone - LastFreezeAwardStreak) / daysPerFreeze;
        var awardable = Math.Min(milestonesCrossed, maxAccumulated - StreakFreezesAccumulated);

        StreakFreezesAccumulated += awardable;
        LastFreezeAwardStreak += awardable * daysPerFreeze;
        return awardable > 0;
    }

    private static int GetEligibleFreezeAwardStreak(int currentStreak, int daysPerFreeze) =>
        currentStreak - (currentStreak % daysPerFreeze);

    public Result ConsumeStreakFreezes(int count)
    {
        if (count <= 0)
            return Result.Failure(DomainErrors.InvalidStreakGap);
        if (StreakFreezesAccumulated < count)
            return Result.Failure(DomainErrors.InsufficientStreakFreezes);

        StreakFreezesAccumulated -= count;
        return Result.Success();
    }

    public Result ConsumeStreakFreeze()
    {
        if (StreakFreezesAccumulated <= 0)
            return Result.Failure(DomainErrors.NoStreakFreezesAccumulated);
        StreakFreezesAccumulated--;
        return Result.Success();
    }

    /// <summary>
    /// Resets progress and integration state (onboarding, gamification, calendar) to defaults while
    /// preserving identity, preferences, subscription, and metered AI usage. The daily message
    /// quota and ad reward allowances are kept so an account reset cannot refill the AI paygate.
    /// </summary>
    public void ResetAccount()
    {
        HasCompletedOnboarding = false;
        HasCompletedTour = false;
        HasCreatedFirstHabit = false;
        HasLoggedFirstHabit = false;
        HasTriedAstra = false;
        HasCompletedOnboardingChecklist = false;
        TotalXp = 0;
        Level = 1;
        CurrentStreak = 0;
        LongestStreak = 0;
        LastActiveDate = null;
        StreakFreezesAccumulated = 0;
        LastFreezeAwardStreak = 0;
        PreGapFreezeAwardStreak = null;
        PreGapLastActiveDate = null;
        HasImportedCalendar = false;
        GoogleAccessToken = null;
        GoogleRefreshToken = null;
        GoogleCalendarAutoSyncEnabled = false;
        GoogleCalendarSelectedIds = null;
        GoogleCalendarAutoSyncStatus = null;
        GoogleCalendarLastSyncedAt = null;
        GoogleCalendarLastSyncError = null;
        GoogleCalendarSyncReconciledAt = null;
    }
}
