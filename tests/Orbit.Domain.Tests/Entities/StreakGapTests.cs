using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class StreakGapTests
{
    private static readonly DateOnly Today = new(2026, 9, 6);

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
    [InlineData(-3, -1)]
    [InlineData(-1, -1)]
    [InlineData(-3, -2)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    public void CreateGap_InvalidDates_IsRefused(int first, int last)
    {
        var result = StreakFreeze.CreateGap(Guid.NewGuid(), [Today.AddDays(first), Today.AddDays(last)], Today);

        result.ErrorCode.Should().Be(DomainErrors.InvalidStreakGap.Code);
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
