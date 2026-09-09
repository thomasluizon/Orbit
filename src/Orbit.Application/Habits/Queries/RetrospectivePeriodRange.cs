using Orbit.Application.Common;

namespace Orbit.Application.Habits.Queries;

/// <summary>
/// Resolves a retrospective period string into a [DateFrom, DateTo] window. The "week" period
/// anchors on the user's <c>WeekStartDay</c> (0 = Sunday, 1 = Monday) via <see cref="WeekMath"/>
/// so the server window matches the client's WeekStartDay-driven calendars; longer periods use
/// rolling spans that have no week-start dependency.
/// </summary>
public static class RetrospectivePeriodRange
{
    private static readonly string[] Known = ["week", "month", "quarter", "semester", "year"];

    /// <summary>
    /// True when <paramref name="period"/> is one <see cref="Resolve"/> understands (case-insensitive),
    /// so callers can reject unknown periods at the trust boundary before resolving.
    /// </summary>
    public static bool IsKnownPeriod(string? period) =>
        period is not null && Known.Contains(period, StringComparer.OrdinalIgnoreCase);

    public static (DateOnly DateFrom, DateOnly DateTo) Resolve(string period, DateOnly today, int weekStartDay)
    {
        var normalized = period.ToLowerInvariant();
        var dateFrom = normalized switch
        {
            "week" => WeekMath.WeekStart(today, weekStartDay),
            "month" => today.AddDays(-29),
            "quarter" => today.AddDays(-89),
            "semester" => today.AddDays(-179),
            "year" => today.AddDays(-364),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown retrospective period.")
        };

        return (dateFrom, today);
    }
}
