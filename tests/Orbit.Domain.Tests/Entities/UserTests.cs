using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class UserTests
{
    private static User CreateValidUser(string name = "Thomas", string email = "thomas@example.com")
    {
        var result = User.Create(name, email);
        return result.Value;
    }

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = User.Create("Thomas", "thomas@example.com");

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Thomas");
        result.Value.Email.Should().Be("thomas@example.com");
    }

    [Fact]
    public void Create_EmptyName_ReturnsFailure()
    {
        var result = User.Create("", "thomas@example.com");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name is required");
    }

    [Fact]
    public void Create_WhitespaceName_ReturnsFailure()
    {
        var result = User.Create("   ", "thomas@example.com");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name is required");
    }

    [Fact]
    public void Create_EmptyEmail_ReturnsFailure()
    {
        var result = User.Create("Thomas", "");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Email is required");
    }

    [Fact]
    public void Create_InvalidEmailFormat_ReturnsFailure()
    {
        var result = User.Create("Thomas", "not-an-email");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid email format");
    }

    [Fact]
    public void Create_TrimsNameAndEmail()
    {
        var result = User.Create("  Thomas  ", "  thomas@example.com  ");

        result.Value.Name.Should().Be("Thomas");
        result.Value.Email.Should().Be("thomas@example.com");
    }

    [Fact]
    public void Create_LowercasesEmail()
    {
        var result = User.Create("Thomas", "Thomas@EXAMPLE.com");

        result.Value.Email.Should().Be("thomas@example.com");
    }

    [Fact]
    public void Create_SetsTrial7Days()
    {
        var before = DateTime.UtcNow.AddDays(7).AddSeconds(-5);
        var user = CreateValidUser();
        var after = DateTime.UtcNow.AddDays(7).AddSeconds(5);

        user.TrialEndsAt.Should().NotBeNull();
        user.TrialEndsAt!.Value.Should().BeOnOrAfter(before);
        user.TrialEndsAt!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void Create_SetsCreatedAtUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var user = CreateValidUser();
        var after = DateTime.UtcNow.AddSeconds(1);

        user.CreatedAtUtc.Should().BeOnOrAfter(before);
        user.CreatedAtUtc.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void IsPro_LifetimePro_ReturnsTrue()
    {
        var user = CreateValidUser();
        user.GrantLifetimePro();

        user.IsPro.Should().BeTrue();
    }

    [Fact]
    public void IsPro_ProPlanNotExpired_ReturnsTrue()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));

        user.IsPro.Should().BeTrue();
    }

    [Fact]
    public void IsPro_ProPlanExpired_ReturnsFalse()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(-1));

        user.IsPro.Should().BeFalse();
    }

    [Fact]
    public void IsPro_FreePlan_ReturnsFalse()
    {
        var user = CreateValidUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        user.IsPro.Should().BeFalse();
    }

    [Fact]
    public void IsTrialActive_FutureTrialEnd_ReturnsTrue()
    {
        var user = CreateValidUser();
        user.IsTrialActive.Should().BeTrue();
    }

    [Fact]
    public void IsTrialActive_PastTrialEnd_ReturnsFalse()
    {
        var user = CreateValidUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        user.IsTrialActive.Should().BeFalse();
    }

    [Fact]
    public void HasProAccess_ProOrTrial_ReturnsTrue()
    {
        var user = CreateValidUser();
        user.HasProAccess.Should().BeTrue();

        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));
        user.HasProAccess.Should().BeTrue();
    }

    [Fact]
    public void SetTimeZone_ValidIana_Success()
    {
        var user = CreateValidUser();

        var result = user.SetTimeZone("UTC");

        result.IsSuccess.Should().BeTrue();
        user.TimeZone.Should().Be("UTC");
    }

    [Fact]
    public void SetTimeZone_Invalid_ReturnsFailure()
    {
        var user = CreateValidUser();

        var result = user.SetTimeZone("Invalid/Timezone");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid timezone");
    }

    [Fact]
    public void SetName_ValidName_UpdatesName()
    {
        var user = CreateValidUser();

        var result = user.SetName("Ana Clara");

        result.IsSuccess.Should().BeTrue();
        user.Name.Should().Be("Ana Clara");
    }

    [Fact]
    public void SetName_TrimsWhitespace()
    {
        var user = CreateValidUser();

        var result = user.SetName("  Ana Clara  ");

        result.IsSuccess.Should().BeTrue();
        user.Name.Should().Be("Ana Clara");
    }

    [Fact]
    public void SetName_AllowsAccentedCharacters()
    {
        var user = CreateValidUser();

        var result = user.SetName("João Sebastião");

        result.IsSuccess.Should().BeTrue();
        user.Name.Should().Be("João Sebastião");
    }

    [Fact]
    public void SetName_EmptyName_ReturnsFailure()
    {
        var user = CreateValidUser();

        var result = user.SetName("");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name is required");
        user.Name.Should().Be("Thomas");
    }

    [Fact]
    public void SetName_WhitespaceName_ReturnsFailure()
    {
        var user = CreateValidUser();

        var result = user.SetName("   ");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name is required");
        user.Name.Should().Be("Thomas");
    }

    [Fact]
    public void SetName_TooLongName_ReturnsFailure()
    {
        var user = CreateValidUser();

        var result = user.SetName(new string('a', 51));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at most 50 characters");
        user.Name.Should().Be("Thomas");
    }

    [Fact]
    public void SetName_ExactlyMaxLength_Succeeds()
    {
        var user = CreateValidUser();
        var name = new string('a', 50);

        var result = user.SetName(name);

        result.IsSuccess.Should().BeTrue();
        user.Name.Should().Be(name);
    }

    [Fact]
    public void SetName_TrimmedWithinMaxLength_Succeeds()
    {
        var user = CreateValidUser();
        var name = new string('a', 50);

        var result = user.SetName($"  {name}  ");

        result.IsSuccess.Should().BeTrue();
        user.Name.Should().Be(name);
    }

    [Fact]
    public void SetStripeSubscription_SetsPlanToPro()
    {
        var user = CreateValidUser();
        var expires = DateTime.UtcNow.AddDays(30);

        user.SetStripeSubscription("sub_123", expires);

        user.Plan.Should().Be(UserPlan.Pro);
        user.StripeSubscriptionId.Should().Be("sub_123");
        user.PlanExpiresAt.Should().Be(expires);
    }

    [Fact]
    public void CancelStripeSubscription_WhenStripeIsSource_ClearsEntitlement()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));

        var beforeCancellation = DateTime.UtcNow;
        user.CancelStripeSubscription(SubscriptionLapseReason.Canceled);

        user.Plan.Should().Be(UserPlan.Free);
        user.StripeSubscriptionId.Should().BeNull();
        user.PlanExpiresAt.Should().BeNull();
        user.SubscriptionSource.Should().BeNull();
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.Canceled);
        user.SubscriptionEndedAtUtc.Should().BeOnOrAfter(beforeCancellation);
    }

    [Fact]
    public void SetStripeSubscription_SetsSourceToStripe()
    {
        var user = CreateValidUser();

        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);

        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
    }

    [Fact]
    public void SetStripeSubscription_ClearsStalePlayPurchaseToken()
    {
        var user = CreateValidUser();
        user.SetPlaySubscription("play_token_123", DateTime.UtcNow.AddDays(20), SubscriptionInterval.Monthly);

        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddMonths(6), SubscriptionInterval.Monthly);

        user.PlayPurchaseToken.Should().BeNull();
        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
    }

    [Fact]
    public void SetPlaySubscription_SetsPlanProSourceAndToken()
    {
        var user = CreateValidUser();
        var expires = DateTime.UtcNow.AddDays(30);

        user.SetPlaySubscription("play_token_123", expires, SubscriptionInterval.Yearly);

        user.Plan.Should().Be(UserPlan.Pro);
        user.PlayPurchaseToken.Should().Be("play_token_123");
        user.PlanExpiresAt.Should().Be(expires);
        user.SubscriptionSource.Should().Be(SubscriptionSource.GooglePlay);
        user.SubscriptionInterval.Should().Be(SubscriptionInterval.Yearly);
        user.IsPro.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, SubscriptionLapseReason.PaymentFailed)]
    [InlineData(false, null)]
    public void SetPlaySubscription_AfterPaymentFailure_SetsReasonFromCurrentState(
        bool isInGracePeriod, SubscriptionLapseReason? expectedReason)
    {
        var user = CreateValidUser();
        var expiresAt = DateTime.UtcNow.AddDays(30);
        user.SetPlaySubscription("play_token_123", expiresAt, SubscriptionInterval.Monthly);
        user.CancelPlaySubscription(SubscriptionLapseReason.PaymentFailed);

        user.SetPlaySubscription("play_token_123", expiresAt, SubscriptionInterval.Monthly, isInGracePeriod);

        user.IsPro.Should().BeTrue();
        user.HasProAccess.Should().BeTrue();
        user.SubscriptionSource.Should().Be(SubscriptionSource.GooglePlay);
        user.SubscriptionLapseReason.Should().Be(expectedReason);
        user.SubscriptionEndedAtUtc.Should().BeNull();
        user.PlayPurchaseToken.Should().Be("play_token_123");
        user.PlanExpiresAt.Should().Be(expiresAt);
    }

    [Fact]
    public void CancelPlaySubscription_WhenPlayIsSource_ClearsEntitlementAndToken()
    {
        var user = CreateValidUser();
        user.SetPlaySubscription("play_token_123", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);

        user.CancelPlaySubscription(SubscriptionLapseReason.Expired);

        user.Plan.Should().Be(UserPlan.Free);
        user.PlayPurchaseToken.Should().BeNull();
        user.SubscriptionSource.Should().BeNull();
        user.SubscriptionInterval.Should().BeNull();
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.Expired);
        user.SubscriptionEndedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void CancelStripeSubscription_WhenPlayIsActiveSource_KeepsPlayEntitlement()
    {
        var user = CreateValidUser();
        var playExpiry = DateTime.UtcNow.AddDays(20);
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddMonths(6));
        user.SetPlaySubscription("play_token_123", playExpiry, SubscriptionInterval.Monthly);

        user.CancelStripeSubscription(SubscriptionLapseReason.Canceled);

        user.StripeSubscriptionId.Should().BeNull();
        user.Plan.Should().Be(UserPlan.Pro);
        user.IsPro.Should().BeTrue();
        user.PlayPurchaseToken.Should().Be("play_token_123");
        user.SubscriptionSource.Should().Be(SubscriptionSource.GooglePlay);
        user.PlanExpiresAt.Should().Be(playExpiry);
        user.SubscriptionLapseReason.Should().BeNull();
        user.SubscriptionEndedAtUtc.Should().BeNull();
    }

    [Fact]
    public void CancelStripeSubscription_WhenStripeIsSourceWithLinkedPlayToken_PreservesPlayTokenForRecovery()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddMonths(6));
        user.LinkPlayPurchaseToken("play_token_123");

        user.CancelStripeSubscription(SubscriptionLapseReason.PaymentFailed);

        user.Plan.Should().Be(UserPlan.Free);
        user.StripeSubscriptionId.Should().BeNull();
        user.SubscriptionSource.Should().BeNull();
        user.PlayPurchaseToken.Should().Be("play_token_123");
        user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.PaymentFailed);
    }

    [Fact]
    public void CancelPlaySubscription_WhenStripeIsActiveSource_KeepsStripeEntitlement()
    {
        var user = CreateValidUser();
        var stripeExpiry = DateTime.UtcNow.AddMonths(6);
        user.SetPlaySubscription("play_token_123", DateTime.UtcNow.AddDays(20), SubscriptionInterval.Monthly);
        user.SetStripeSubscription("sub_123", stripeExpiry);

        user.CancelPlaySubscription(SubscriptionLapseReason.Expired);

        user.PlayPurchaseToken.Should().BeNull();
        user.Plan.Should().Be(UserPlan.Pro);
        user.IsPro.Should().BeTrue();
        user.StripeSubscriptionId.Should().Be("sub_123");
        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
        user.PlanExpiresAt.Should().Be(stripeExpiry);
        user.SubscriptionLapseReason.Should().BeNull();
        user.SubscriptionEndedAtUtc.Should().BeNull();
    }

    [Fact]
    public void SetStripeSubscription_AfterPaymentFailure_ClearsPendingLapse()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));
        user.RecordSubscriptionLapseReason(
            SubscriptionSource.Stripe, SubscriptionLapseReason.PaymentFailed);

        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));

        user.SubscriptionLapseReason.Should().BeNull();
        user.SubscriptionEndedAtUtc.Should().BeNull();
    }

    [Fact]
    public void CancelStripeSubscription_InvalidReason_Throws()
    {
        var user = CreateValidUser();

        var act = () => user.CancelStripeSubscription((SubscriptionLapseReason)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IncrementAiMessageCount_FirstTime_ResetsAndIncrements()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 1);

        user.IncrementAiMessageCount(today);

        user.AiMessagesUsedToday.Should().Be(1);
        user.AiMessagesLocalDate.Should().Be(today);
    }

    [Fact]
    public void IncrementAiMessageCount_SameLocalDate_JustIncrements()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 1);
        user.IncrementAiMessageCount(today);

        user.IncrementAiMessageCount(today);

        user.AiMessagesUsedToday.Should().Be(2);
        user.AiMessagesLocalDate.Should().Be(today);
    }

    [Fact]
    public void IncrementAiMessageCount_NextLocalDate_ResetsCounter()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 1);
        user.IncrementAiMessageCount(today);
        user.IncrementAiMessageCount(today);
        user.AiMessagesUsedToday.Should().Be(2);

        user.IncrementAiMessageCount(today.AddDays(1));

        user.AiMessagesUsedToday.Should().Be(1);
        user.AiMessagesLocalDate.Should().Be(today.AddDays(1));
    }

    [Fact]
    public void IncrementAiMessageCount_PreviousLocalDateAfterTimezoneChange_PreservesBucket()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 1);
        for (var i = 0; i < 5; i++)
            user.IncrementAiMessageCount(today.AddDays(1));

        user.IncrementAiMessageCount(today);

        user.AiMessagesUsedToday.Should().Be(6);
        user.AiMessagesLocalDate.Should().Be(today.AddDays(1));
    }

    [Fact]
    public void GetAiMessagesUsedToday_StoredDateMismatch_ReturnsZero()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 1);
        user.IncrementAiMessageCount(today.AddDays(-1));

        var result = user.GetAiMessagesUsedToday(today);

        result.Should().Be(0);
    }

    [Fact]
    public void GetAiMessagesUsedForQuota_StoredFutureDate_ReturnsExistingUsage()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 1);
        user.IncrementAiMessageCount(today.AddDays(1));

        var result = user.GetAiMessagesUsedForQuota(today);

        result.Should().Be(1);
    }

    [Fact]
    public void IncrementAiMessageCount_NextLocalDate_ZeroesAdRewardBonus()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 1);
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.IncrementAiMessageCount(today);
        user.GrantAdReward(today, bonusMessages: 5);
        user.IncrementAiMessageCount(today);
        user.AdRewardBonusMessages.Should().Be(5);

        user.IncrementAiMessageCount(today.AddDays(1));

        user.AiMessagesUsedToday.Should().Be(1);
        user.AdRewardBonusMessages.Should().Be(0);
    }

    [Fact]
    public void ResetAccount_PreservesMeteredAiUsageAndAdRewards()
    {
        var user = CreateValidUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        var today = new DateOnly(2026, 3, 1);
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount(today);
        user.GrantAdReward(today, bonusMessages: 5);

        user.ResetAccount();

        user.AiMessagesUsedToday.Should().Be(20);
        user.AiMessagesLocalDate.Should().Be(today);
        user.AdRewardBonusMessages.Should().Be(5);
        user.AdRewardsClaimedToday.Should().Be(1);
        user.LastAdRewardLocalDate.Should().Be(today);
    }

    [Fact]
    public void ResetAccount_ClearsOnboardingChecklistEvidence()
    {
        var user = CreateValidUser();
        user.MarkFirstHabitCreated();
        user.MarkFirstHabitLogged();
        user.MarkAstraUsed();
        user.CompleteOnboardingChecklist();

        user.ResetAccount();

        user.HasCreatedFirstHabit.Should().BeFalse();
        user.HasLoggedFirstHabit.Should().BeFalse();
        user.HasTriedAstra.Should().BeFalse();
        user.HasCompletedOnboardingChecklist.Should().BeFalse();
    }

    [Fact]
    public void ResetAccount_ThenSendMessage_DoesNotRefillQuota()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 1);
        for (int i = 0; i < 20; i++)
            user.IncrementAiMessageCount(today);

        user.ResetAccount();
        user.IncrementAiMessageCount(today);

        user.AiMessagesUsedToday.Should().Be(21);
    }

    [Fact]
    public void SetReferralCode_SetsCode()
    {
        var user = CreateValidUser();

        user.SetReferralCode("ABC12345");

        user.ReferralCode.Should().Be("ABC12345");
    }

    [Fact]
    public void SetReferredBy_SetsReferrerUserId()
    {
        var user = CreateValidUser();
        var referrerId = Guid.NewGuid();

        user.SetReferredBy(referrerId);

        user.ReferredByUserId.Should().Be(referrerId);
    }

    [Fact]
    public void ExtendTrial_ExpiredTrial_ExtendsFromNow()
    {
        var user = CreateValidUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        var before = DateTime.UtcNow.AddDays(10).AddSeconds(-1);

        user.ExtendTrial(10);

        var after = DateTime.UtcNow.AddDays(10).AddSeconds(1);

        user.TrialEndsAt.Should().NotBeNull();
        user.TrialEndsAt!.Value.Should().BeOnOrAfter(before);
        user.TrialEndsAt!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void ExtendTrial_NullTrial_ExtendsFromNow()
    {
        var user = CreateValidUser();
        typeof(User).GetProperty("TrialEndsAt")!.SetValue(user, null);

        var before = DateTime.UtcNow.AddDays(10).AddSeconds(-1);

        user.ExtendTrial(10);

        var after = DateTime.UtcNow.AddDays(10).AddSeconds(1);

        user.TrialEndsAt.Should().NotBeNull();
        user.TrialEndsAt!.Value.Should().BeOnOrAfter(before);
        user.TrialEndsAt!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void ExtendTrial_ActiveTrial_ExtendsFromExistingDate()
    {
        var user = CreateValidUser();
        var existingTrialEnd = user.TrialEndsAt!.Value;

        user.ExtendTrial(10);

        user.TrialEndsAt.Should().NotBeNull();
        user.TrialEndsAt!.Value.Should().BeCloseTo(existingTrialEnd.AddDays(10), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AddXp_PositiveAmount_IncrementsTotalXp()
    {
        var user = CreateValidUser();
        user.TotalXp.Should().Be(0);

        user.AddXp(50);

        user.TotalXp.Should().Be(50);
    }

    [Fact]
    public void AddXp_MultipleCalls_Accumulates()
    {
        var user = CreateValidUser();

        user.AddXp(25);
        user.AddXp(75);

        user.TotalXp.Should().Be(100);
    }

    [Fact]
    public void AddXp_ZeroAmount_NoChange()
    {
        var user = CreateValidUser();
        user.AddXp(50);

        user.AddXp(0);

        user.TotalXp.Should().Be(50);
    }

    [Fact]
    public void AddXp_NegativeAmount_NoChange()
    {
        var user = CreateValidUser();
        user.AddXp(50);

        user.AddXp(-10);

        user.TotalXp.Should().Be(50);
    }

    [Fact]
    public void SetLevel_ValidLevel_UpdatesLevel()
    {
        var user = CreateValidUser();
        user.Level.Should().Be(1);

        user.SetLevel(5);

        user.Level.Should().Be(5);
    }

    [Fact]
    public void SetLevel_MaxLevel_UpdatesLevel()
    {
        var user = CreateValidUser();

        user.SetLevel(10);

        user.Level.Should().Be(10);
    }

    [Fact]
    public void SetLevel_BelowMinimum_NoChange()
    {
        var user = CreateValidUser();

        user.SetLevel(0);

        user.Level.Should().Be(1);
    }

    [Fact]
    public void SetLevel_PastTableMax_UpdatesLevel()
    {
        var user = CreateValidUser();

        user.SetLevel(11);

        user.Level.Should().Be(11);
    }

    [Fact]
    public void Create_DefaultXpAndLevel()
    {
        var user = CreateValidUser();

        user.TotalXp.Should().Be(0);
        user.Level.Should().Be(1);
    }

    [Fact]
    public void Create_DefaultStreakFields()
    {
        var user = CreateValidUser();

        user.CurrentStreak.Should().Be(0);
        user.LongestStreak.Should().Be(0);
        user.LastActiveDate.Should().BeNull();
    }

    [Fact]
    public void UpdateStreak_FirstActivity_SetsStreakToOne()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 30);

        user.UpdateStreak(today);

        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(1);
        user.LastActiveDate.Should().Be(today);
    }

    [Fact]
    public void UpdateStreak_ConsecutiveDays_IncrementsStreak()
    {
        var user = CreateValidUser();
        var day1 = new DateOnly(2026, 3, 28);
        var day2 = new DateOnly(2026, 3, 29);
        var day3 = new DateOnly(2026, 3, 30);

        user.UpdateStreak(day1);
        user.UpdateStreak(day2);
        user.UpdateStreak(day3);

        user.CurrentStreak.Should().Be(3);
        user.LongestStreak.Should().Be(3);
        user.LastActiveDate.Should().Be(day3);
    }

    [Fact]
    public void UpdateStreak_GapInDays_ResetsToOne()
    {
        var user = CreateValidUser();
        var day1 = new DateOnly(2026, 3, 28);
        var day3 = new DateOnly(2026, 3, 30);
        user.UpdateStreak(day1);
        user.UpdateStreak(day3);

        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(1);
        user.LastActiveDate.Should().Be(day3);
    }

    [Fact]
    public void UpdateStreak_SameDay_IsIdempotent()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 30);

        user.UpdateStreak(today);
        user.UpdateStreak(today);
        user.UpdateStreak(today);

        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(1);
    }

    [Fact]
    public void UpdateStreak_TracksLongestStreak()
    {
        var user = CreateValidUser();

        for (int i = 0; i < 5; i++)
            user.UpdateStreak(new DateOnly(2026, 3, 1).AddDays(i));

        user.CurrentStreak.Should().Be(5);
        user.LongestStreak.Should().Be(5);

        user.UpdateStreak(new DateOnly(2026, 3, 10));
        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(5);
        user.UpdateStreak(new DateOnly(2026, 3, 11));
        user.UpdateStreak(new DateOnly(2026, 3, 12));
        user.CurrentStreak.Should().Be(3);
        user.LongestStreak.Should().Be(5);
    }

    [Fact]
    public void UpdateStreak_LongerStreakUpdatesLongest()
    {
        var user = CreateValidUser();

        user.UpdateStreak(new DateOnly(2026, 1, 1));
        user.UpdateStreak(new DateOnly(2026, 1, 2));
        user.LongestStreak.Should().Be(2);

        user.UpdateStreak(new DateOnly(2026, 2, 1));

        user.UpdateStreak(new DateOnly(2026, 2, 2));
        user.UpdateStreak(new DateOnly(2026, 2, 3));
        user.CurrentStreak.Should().Be(3);
        user.LongestStreak.Should().Be(3);
    }

    [Fact]
    public void AwardStreakFreezeIfEligible_SingleMilestone_AwardsOne()
    {
        var user = CreateValidUser();
        user.SetStreakState(currentStreak: 7, longestStreak: 7, lastActiveDate: new DateOnly(2026, 1, 7));

        var awarded = user.AwardStreakFreezeIfEligible(maxAccumulated: 3, daysPerFreeze: 7);

        awarded.Should().BeTrue();
        user.StreakFreezesAccumulated.Should().Be(1);
    }

    [Fact]
    public void AwardStreakFreezeIfEligible_MultiMilestoneJump_AwardsOnePerMilestone()
    {
        var user = CreateValidUser();
        user.SetStreakState(currentStreak: 21, longestStreak: 21, lastActiveDate: new DateOnly(2026, 1, 21));

        var awarded = user.AwardStreakFreezeIfEligible(maxAccumulated: 3, daysPerFreeze: 7);

        awarded.Should().BeTrue();
        user.StreakFreezesAccumulated.Should().Be(3);
    }

    [Fact]
    public void AwardStreakFreezeIfEligible_MultiMilestoneJump_RespectsMaxCap()
    {
        var user = CreateValidUser();
        user.SetStreakState(currentStreak: 35, longestStreak: 35, lastActiveDate: new DateOnly(2026, 2, 4));

        var awarded = user.AwardStreakFreezeIfEligible(maxAccumulated: 3, daysPerFreeze: 7);

        awarded.Should().BeTrue();
        user.StreakFreezesAccumulated.Should().Be(3);
    }

    [Fact]
    public void AwardStreakFreezeIfEligible_AlreadyAwardedMilestone_DoesNotDoubleAward()
    {
        var user = CreateValidUser();
        user.SetStreakState(currentStreak: 7, longestStreak: 7, lastActiveDate: new DateOnly(2026, 1, 7));
        user.AwardStreakFreezeIfEligible(maxAccumulated: 3, daysPerFreeze: 7);

        var awardedAgain = user.AwardStreakFreezeIfEligible(maxAccumulated: 3, daysPerFreeze: 7);

        awardedAgain.Should().BeFalse();
        user.StreakFreezesAccumulated.Should().Be(1);
    }

    [Fact]
    public void ConsumeStreakFreeze_WithBankedFreeze_DecrementsExactlyOne()
    {
        var user = CreateValidUser();
        user.SetStreakState(7, 7, new DateOnly(2026, 1, 7));
        user.AwardStreakFreezeIfEligible();

        var result = user.ConsumeStreakFreeze();

        result.IsSuccess.Should().BeTrue();
        user.StreakFreezesAccumulated.Should().Be(0);
    }

    [Fact]
    public void ConsumeStreakFreeze_EmptyBank_ReturnsGuardFailure()
    {
        var user = CreateValidUser();

        var result = user.ConsumeStreakFreeze();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("NO_STREAK_FREEZES");
        user.StreakFreezesAccumulated.Should().Be(0);
    }

    [Fact]
    public void Create_OnboardingChecklistFlags_DefaultFalse()
    {
        var user = CreateValidUser();

        user.HasCreatedFirstHabit.Should().BeFalse();
        user.HasLoggedFirstHabit.Should().BeFalse();
        user.HasTriedAstra.Should().BeFalse();
        user.HasCompletedOnboardingChecklist.Should().BeFalse();
    }

    [Fact]
    public void MarkFirstHabitCreated_SetsFlagAndIsIdempotent()
    {
        var user = CreateValidUser();

        user.MarkFirstHabitCreated();
        user.MarkFirstHabitCreated();

        user.HasCreatedFirstHabit.Should().BeTrue();
        user.HasLoggedFirstHabit.Should().BeFalse();
        user.HasTriedAstra.Should().BeFalse();
    }

    [Fact]
    public void MarkFirstHabitLogged_SetsFlagAndIsIdempotent()
    {
        var user = CreateValidUser();

        user.MarkFirstHabitLogged();
        user.MarkFirstHabitLogged();

        user.HasLoggedFirstHabit.Should().BeTrue();
        user.HasCreatedFirstHabit.Should().BeFalse();
        user.HasTriedAstra.Should().BeFalse();
    }

    [Fact]
    public void MarkAstraUsed_SetsFlagAndIsIdempotent()
    {
        var user = CreateValidUser();

        user.MarkAstraUsed();
        user.MarkAstraUsed();

        user.HasTriedAstra.Should().BeTrue();
        user.HasCreatedFirstHabit.Should().BeFalse();
        user.HasLoggedFirstHabit.Should().BeFalse();
    }

    [Fact]
    public void CompleteOnboardingChecklist_SetsFlagAndIsIdempotent()
    {
        var user = CreateValidUser();

        user.CompleteOnboardingChecklist();
        user.CompleteOnboardingChecklist();

        user.HasCompletedOnboardingChecklist.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_ClearsGoogleCalendarConnection()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));
        user.SetGoogleTokens("access-token", "refresh-token");
        user.EnableCalendarAutoSync().IsSuccess.Should().BeTrue();

        user.Deactivate(DateTime.UtcNow.AddDays(7));

        user.IsDeactivated.Should().BeTrue();
        user.GoogleAccessToken.Should().BeNull();
        user.GoogleRefreshToken.Should().BeNull();
        user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
    }

    [Fact]
    public void CancelDeactivation_LeavesGoogleTokensCleared_ReconnectRequired()
    {
        var user = CreateValidUser();
        user.SetGoogleTokens("access-token", "refresh-token");
        user.Deactivate(DateTime.UtcNow.AddDays(7));

        user.CancelDeactivation();

        user.IsDeactivated.Should().BeFalse();
        user.GoogleAccessToken.Should().BeNull();
        user.GoogleRefreshToken.Should().BeNull();
    }
}
