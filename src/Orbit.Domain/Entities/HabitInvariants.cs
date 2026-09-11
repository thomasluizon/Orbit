using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Entities;

/// <summary>
/// Pure validation and normalization guards for <see cref="Habit"/> invariants.
/// Each method returns the matching <see cref="DomainErrors"/> entry on violation (or null when valid),
/// exactly as the factory and update paths expect; <see cref="NormalizeEmoji"/> trims and collapses
/// blank emoji to null.
/// </summary>
internal static class HabitInvariants
{
    public static AppError? ValidateScheduleOptions(
        bool isGeneral, bool isFlexible, bool isBadHabit,
        FrequencyUnit? frequencyUnit, int? frequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? days,
        int? intervalWeeks)
    {
        if (isGeneral && (frequencyUnit is not null || frequencyQuantity is not null))
            return DomainErrors.GeneralHabitHasFrequency;

        if (isGeneral && isBadHabit)
            return DomainErrors.GeneralHabitIsBadHabit;

        if (frequencyUnit is not null && frequencyQuantity is null)
            return DomainErrors.FrequencyQuantityInvalid;

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return DomainErrors.FrequencyQuantityInvalid;

        if (intervalWeeks is < 1 or > DomainConstants.MaxIntervalWeeks)
            return DomainErrors.IntervalWeeksInvalid;

        if (isFlexible && frequencyUnit is null)
            return DomainErrors.FlexibleNeedsFrequencyUnit;

        if (isFlexible && days?.Count > 0)
            return DomainErrors.FlexibleHasDays;

        if (!isFlexible && days?.Count > 0 && (frequencyQuantity != 1 || frequencyUnit != Enums.FrequencyUnit.Day))
            return DomainErrors.DaysRequireQuantityOne;

        return null;
    }

    public static AppError? ValidateDateOptions(
        TimeOnly? dueTime, TimeOnly? dueEndTime,
        DateOnly? endDate, FrequencyUnit? frequencyUnit,
        bool isGeneral, DateOnly dueDate)
    {
        if (dueEndTime.HasValue && dueTime.HasValue && dueEndTime.Value <= dueTime.Value)
            return DomainErrors.EndTimeBeforeStartTime;

        if (endDate.HasValue && frequencyUnit is null && !isGeneral)
            return DomainErrors.OneTimeTaskHasEndDate;

        if (endDate.HasValue && endDate.Value < dueDate)
            return DomainErrors.EndDateBeforeStartDate;

        return null;
    }

    public static AppError? ValidateScheduledReminders(
        IReadOnlyList<ScheduledReminderTime>? scheduledReminders)
    {
        if (scheduledReminders is null)
            return null;

        if (scheduledReminders.Count > DomainConstants.MaxScheduledReminders)
            return DomainErrors.MaxScheduledReminders.Format(DomainConstants.MaxScheduledReminders);

        var hasDuplicates = scheduledReminders
            .GroupBy(sr => (sr.When, sr.Time))
            .Any(g => g.Count() > 1);

        if (hasDuplicates)
            return DomainErrors.DuplicateScheduledReminders;

        return null;
    }

    public static AppError? ValidateReminderTimes(IReadOnlyList<int>? reminderTimes)
    {
        if (reminderTimes is null)
            return null;

        if (reminderTimes.Count > DomainConstants.MaxReminderTimes)
            return DomainErrors.MaxReminderTimes.Format(DomainConstants.MaxReminderTimes);

        return null;
    }

    public static AppError? ValidateEmoji(string? emoji)
    {
        if (emoji is null)
            return null;

        if (emoji.Trim().Length > DomainConstants.MaxHabitEmojiLength)
            return DomainErrors.EmojiTooLong.Format(DomainConstants.MaxHabitEmojiLength);

        return null;
    }

    public static string? NormalizeEmoji(string? emoji)
    {
        var normalized = emoji?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
