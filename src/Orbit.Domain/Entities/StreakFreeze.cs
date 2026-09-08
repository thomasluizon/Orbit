using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class StreakFreeze : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly UsedOnDate { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private StreakFreeze() { }

    /// <summary>
    /// Validates what a bare date list can prove: real dates, no duplicates, and a last date of local
    /// yesterday.
    /// </summary>
    /// <remarks>
    /// Calendar-consecutiveness is deliberately NOT asserted. A gap is contiguous over the user's
    /// SCHEDULED occurrences, which this entity cannot see, so requiring consecutive calendar days
    /// rejected every valid weekly and every-N-day gap before a schedule was ever loaded.
    /// <see cref="Orbit.Domain.Interfaces.IUserStreakService"/> enforces the real contiguity against the
    /// scheduled dates.
    /// </remarks>
    public static Result<IReadOnlyList<StreakFreeze>> CreateGap(
        Guid userId, IReadOnlyCollection<DateOnly>? dates, DateOnly userToday)
    {
        if (userId == Guid.Empty || dates is null || dates.Count == 0)
            return Result.Failure<IReadOnlyList<StreakFreeze>>(DomainErrors.InvalidStreakGap);

        var ordered = dates.Order().ToArray();
        if (ordered[0] == DateOnly.MinValue
            || ordered[^1].DayNumber != userToday.DayNumber - 1
            || ordered.Where((date, index) => index > 0 && date == ordered[index - 1]).Any())
        {
            return Result.Failure<IReadOnlyList<StreakFreeze>>(DomainErrors.InvalidStreakGap);
        }

        return Result.Success<IReadOnlyList<StreakFreeze>>(
            ordered.Select(date => Create(userId, date)).ToArray());
    }

    public static StreakFreeze Create(Guid userId, DateOnly date)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User ID is required.", nameof(userId));
        if (date == DateOnly.MinValue)
            throw new ArgumentOutOfRangeException(nameof(date), "Used date is required.");

        return new StreakFreeze
        {
            UserId = userId,
            UsedOnDate = date,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
