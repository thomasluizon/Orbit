using FluentAssertions;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Tests.Queries.Habits;

public class RetrospectivePeriodRangeTests
{
    private static readonly DateOnly Today = new(2026, 6, 20);

    [Theory]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("quarter")]
    [InlineData("semester")]
    [InlineData("year")]
    public void Resolve_KnownPeriod_WindowEndsToday(string period)
    {
        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve(period, Today, weekStartDay: 1);

        dateTo.Should().Be(Today);
        dateFrom.Should().BeOnOrBefore(Today);
    }

    [Theory]
    [InlineData("month", 30)]
    [InlineData("quarter", 90)]
    [InlineData("semester", 180)]
    [InlineData("year", 365)]
    public void Resolve_RollingPeriod_SpansExpectedDateCount(string period, int expectedDateCount)
    {
        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve(period, Today, weekStartDay: 1);

        var dateCount = dateTo.DayNumber - dateFrom.DayNumber + 1;

        dateCount.Should().Be(expectedDateCount);
    }

    [Fact]
    public void Resolve_UnknownPeriod_Throws()
    {
        var act = () => RetrospectivePeriodRange.Resolve("decade", Today, weekStartDay: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("quarter")]
    [InlineData("semester")]
    [InlineData("year")]
    [InlineData("WEEK")]
    [InlineData("Year")]
    public void IsKnownPeriod_KnownPeriod_ReturnsTrue(string period) =>
        RetrospectivePeriodRange.IsKnownPeriod(period).Should().BeTrue();

    [Theory]
    [InlineData("day")]
    [InlineData("decade")]
    [InlineData("")]
    [InlineData(null)]
    public void IsKnownPeriod_UnknownPeriod_ReturnsFalse(string? period) =>
        RetrospectivePeriodRange.IsKnownPeriod(period).Should().BeFalse();
}
