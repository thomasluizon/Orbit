using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class StreakGapTests
{
    private static readonly DateOnly Today = new(2026, 9, 6);

    [Theory]
    [InlineData(false, 14)]
    [InlineData(true, 7)]
    public void RestoreStreakAfterGapRepair_UsesSavedOrDerivedCursor(bool recalculated, int expectedCursor)
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStreakState(7, 7, Today.AddDays(-10));
        user.AwardStreakFreezeIfEligible();
        user.SetStreakState(14, 14, Today.AddDays(-3));
        if (recalculated)
        {
            user.SetStreakState(0, 14, null);
            user.SetStreakState(0, 14, null);
            user.LastFreezeAwardStreak.Should().Be(0);
        }

        user.RestoreStreakAfterGapRepair(14, 14, Today.AddDays(-1), Today.AddDays(-3));

        user.LastFreezeAwardStreak.Should().Be(expectedCursor);
        user.PreGapFreezeAwardStreak.Should().BeNull();
        user.PreGapLastActiveDate.Should().BeNull();
    }

    [Fact]
    public void RestoreStreakAfterGapRepair_PreservesCursorAdvancedAtFullBank()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStreakState(21, 21, Today.AddDays(-10));
        user.AwardStreakFreezeIfEligible();
        user.SetStreakState(28, 28, Today.AddDays(-3));
        user.AwardStreakFreezeIfEligible().Should().BeFalse();
        user.SetStreakState(0, 28, null);
        user.ConsumeStreakFreezes(2);

        user.RestoreStreakAfterGapRepair(28, 28, Today.AddDays(-1), Today.AddDays(-3));

        user.LastFreezeAwardStreak.Should().Be(28);
        user.AwardStreakFreezeIfEligible().Should().BeFalse();
        user.StreakFreezesAccumulated.Should().Be(1);
    }

    [Fact]
    public void RestoreStreakAfterGapRepair_DoesNotRestoreCursorFromDifferentRun()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStreakState(14, 14, Today.AddDays(-20));
        user.AwardStreakFreezeIfEligible();
        user.SetStreakState(0, 14, null);
        user.SetStreakState(6, 14, Today.AddDays(-3));
        user.ConsumeStreakFreezes(2);

        user.RestoreStreakAfterGapRepair(6, 14, Today.AddDays(-1), Today.AddDays(-3));
        user.UpdateStreak(Today);

        user.AwardStreakFreezeIfEligible().Should().BeTrue();
        user.StreakFreezesAccumulated.Should().Be(1);
        user.LastFreezeAwardStreak.Should().Be(7);
    }

    [Fact]
    public void ResetAccount_ClearsSavedAwardCursor()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStreakState(14, 14, Today.AddDays(-3));
        user.AwardStreakFreezeIfEligible();
        user.SetStreakState(0, 14, null);

        user.ResetAccount();

        user.PreGapFreezeAwardStreak.Should().BeNull();
        user.PreGapLastActiveDate.Should().BeNull();
        user.LastFreezeAwardStreak.Should().Be(0);
    }

    [Fact]
    public void CreateGap_UnorderedConsecutiveDates_CreatesOneFreezePerDay()
    {
        var userId = Guid.NewGuid();
        var result = StreakFreeze.CreateGap(userId, [Today.AddDays(-1), Today.AddDays(-2)], Today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(freeze => freeze.UsedOnDate).Should().Equal(Today.AddDays(-2), Today.AddDays(-1));
        result.Value.Should().OnlyContain(freeze => freeze.UserId == userId);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-3, -2)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    public void CreateGap_InvalidDates_IsRefused(int first, int last)
    {
        var result = StreakFreeze.CreateGap(Guid.NewGuid(), [Today.AddDays(first), Today.AddDays(last)], Today);

        result.ErrorCode.Should().Be(DomainErrors.InvalidStreakGap.Code);
    }

    /// <summary>
    /// A calendar-sparse selection is well formed HERE. Contiguity is defined over the user's scheduled
    /// occurrences, which this entity cannot see, so asserting calendar adjacency rejected every valid
    /// weekly and yearly gap before a schedule was ever loaded. UserStreakService refuses a selection
    /// that is not an unbroken run of scheduled occurrences.
    /// </summary>
    [Fact]
    public void CreateGap_CalendarSparseSelectionEndingYesterday_IsAccepted()
    {
        var result = StreakFreeze.CreateGap(Guid.NewGuid(), [Today.AddDays(-3), Today.AddDays(-1)], Today);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void CreateGap_MissingInputs_IsRefused()
    {
        StreakFreeze.CreateGap(Guid.Empty, [Today.AddDays(-1)], Today).IsFailure.Should().BeTrue();
        StreakFreeze.CreateGap(Guid.NewGuid(), null, Today).IsFailure.Should().BeTrue();
        StreakFreeze.CreateGap(Guid.NewGuid(), [], Today).IsFailure.Should().BeTrue();
        StreakFreeze.CreateGap(Guid.NewGuid(), [DateOnly.MinValue], DateOnly.MinValue.AddDays(1)).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(2, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(3, 2, false)]
    [InlineData(0, 2, false)]
    [InlineData(-1, 2, false)]
    public void ConsumeStreakFreezes_SpendsAllOrNone(int count, int remaining, bool succeeds)
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStreakState(14, 14, Today.AddDays(-3));
        user.AwardStreakFreezeIfEligible();

        var result = user.ConsumeStreakFreezes(count);

        result.IsSuccess.Should().Be(succeeds);
        user.StreakFreezesAccumulated.Should().Be(remaining);
        if (count > 2)
            result.ErrorCode.Should().Be(DomainErrors.InsufficientStreakFreezes.Code);
    }
}
