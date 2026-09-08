namespace Orbit.Domain.Models;

/// <param name="PrecedingScheduledDate">
/// The scheduled occurrence immediately before a repaired gap, set only by gap repair. The award
/// cursor is restored against it, and it is NOT the previous calendar day: for a weekly or yearly
/// cadence those differ, and passing the calendar day made the cursor match fail, which silently
/// marked a newly crossed milestone as awarded without granting its freeze.
/// </param>
/// <param name="PreGapStreak">
/// The streak as of <see cref="PrecedingScheduledDate"/>, so the award cursor can be bounded to what
/// was earned BEFORE the gap when no saved snapshot matches. Set only by gap repair.
/// </param>
public record UserStreakState(int CurrentStreak, int LongestStreak, DateOnly? LastActiveDate,
    DateOnly? PrecedingScheduledDate = null, int PreGapStreak = 0);
