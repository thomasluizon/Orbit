namespace Orbit.Domain.Models;

/// <param name="PrecedingScheduledDate">
/// The scheduled occurrence immediately before a repaired gap, set only by gap repair. The award
/// cursor is restored against it, and it is NOT the previous calendar day: for a weekly or yearly
/// cadence those differ, and passing the calendar day made the cursor match fail, which silently
/// marked a newly crossed milestone as awarded without granting its freeze.
/// </param>
public record UserStreakState(int CurrentStreak, int LongestStreak, DateOnly? LastActiveDate,
    DateOnly? PrecedingScheduledDate = null);
