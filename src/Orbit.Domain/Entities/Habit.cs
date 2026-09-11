using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

#pragma warning disable S6964 // Domain entity with private setters - not a model-bound DTO

namespace Orbit.Domain.Entities;

public record HabitCreateParams(
    Guid UserId,
    string Title,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    DateOnly DueDate,
    string? Description = null,
    IReadOnlyList<System.DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    Guid? ParentHabitId = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    bool SlipAlertEnabled = false,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    bool IsGeneral = false,
    bool IsFlexible = false,
    DateOnly? EndDate = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    int? Position = null,
    string? GoogleEventId = null,
    string? Emoji = null,
    int? IntervalWeeks = null);

public record HabitUpdateParams(
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<System.DayOfWeek>? Days,
    bool IsBadHabit,
    DateOnly? DueDate,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool? ReminderEnabled = null,
    IReadOnlyList<int>? ReminderTimes = null,
    bool? SlipAlertEnabled = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    bool? IsGeneral = null,
    bool? IsFlexible = null,
    DateOnly? EndDate = null,
    bool? ClearEndDate = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    string? Emoji = null,
    DateOnly? UserToday = null,
    int? IntervalWeeks = null);

public class Habit : Entity, ITimestamped, ISoftDeletable
{
    private const int DaysInWeek = 7;
    private const int YearsInGregorianCycle = 400;
    private const int MonthsInGregorianCycle = YearsInGregorianCycle * 12;

    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public string? Emoji { get; private set; }
    public FrequencyUnit? FrequencyUnit { get; private set; }
    public int? FrequencyQuantity { get; private set; }
    public int? IntervalWeeks { get; private set; }
    public bool IsBadHabit { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateOnly DueDate { get; private set; }
    public DateOnly? ScheduledStartDate { get; private set; }
    /// <summary>
    /// For monthly/yearly habits, the original day-of-month from the first DueDate (1-31).
    /// Preserves the anchor across month-end clamping so a Jan 31 habit re-anchors to Mar 31
    /// rather than permanently drifting to day 28 after the Feb clamp.
    /// Null for daily/weekly habits and for legacy rows pre-migration (falls back to DueDate.Day).
    /// </summary>
    public int? OriginalDayOfMonth { get; private set; }
    public TimeOnly? DueTime { get; private set; }
    public TimeOnly? DueEndTime { get; private set; }
    public bool ReminderEnabled { get; private set; }
    public IReadOnlyList<int> ReminderTimes { get; private set; } = [15];
    public bool IsGeneral { get; private set; }
    public bool IsFlexible { get; private set; }
    public bool SlipAlertEnabled { get; private set; }
    public IReadOnlyList<ChecklistItem> ChecklistItems { get; private set; } = [];
    public IReadOnlyList<ScheduledReminderTime> ScheduledReminders { get; private set; } = [];
    public DateOnly? EndDate { get; private set; }
    public int? Position { get; private set; }
    public string? GoogleEventId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }
    public ICollection<System.DayOfWeek> Days { get; private set; } = [];

    public Guid? ParentHabitId { get; private set; }

    private readonly List<HabitLog> _logs = [];
    public IReadOnlyCollection<HabitLog> Logs => _logs.AsReadOnly();

    private readonly List<Habit> _children = [];
    public IReadOnlyCollection<Habit> Children => _children.AsReadOnly();

    private readonly List<Tag> _tags = [];
    public IReadOnlyCollection<Tag> Tags => _tags.AsReadOnly();

    private readonly List<Goal> _goals = [];
    public IReadOnlyCollection<Goal> Goals => _goals.AsReadOnly();

    private Habit() { }

    public static Result<Habit> Create(HabitCreateParams p)
    {
        if (p.UserId == Guid.Empty)
            return Result.Failure<Habit>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(p.Title))
            return Result.Failure<Habit>(DomainErrors.TitleRequired);

        var emojiValidation = HabitInvariants.ValidateEmoji(p.Emoji);
        if (emojiValidation is not null)
            return Result.Failure<Habit>(emojiValidation);

        var scheduleValidation = HabitInvariants.ValidateScheduleOptions(
            p.IsGeneral, p.IsFlexible, p.IsBadHabit, p.FrequencyUnit, p.FrequencyQuantity, p.Days, p.IntervalWeeks);
        if (scheduleValidation is not null)
            return Result.Failure<Habit>(scheduleValidation);

        var dateValidation = HabitInvariants.ValidateDateOptions(
            p.DueTime, p.DueEndTime, p.EndDate, p.FrequencyUnit, p.IsGeneral, p.DueDate);
        if (dateValidation is not null)
            return Result.Failure<Habit>(dateValidation);

        var reminderValidation = HabitInvariants.ValidateScheduledReminders(p.ScheduledReminders);
        if (reminderValidation is not null)
            return Result.Failure<Habit>(reminderValidation);

        var reminderTimesValidation = HabitInvariants.ValidateReminderTimes(p.ReminderTimes);
        if (reminderTimesValidation is not null)
            return Result.Failure<Habit>(reminderTimesValidation);

        return Result.Success(new Habit
        {
            UserId = p.UserId,
            Title = p.Title.Trim(),
            Description = p.Description?.Trim(),
            Emoji = HabitInvariants.NormalizeEmoji(p.Emoji),
            FrequencyUnit = p.FrequencyUnit,
            FrequencyQuantity = p.FrequencyQuantity,
            IntervalWeeks = p.IntervalWeeks,
            Days = p.IsFlexible ? [] : (p.Days?.ToList() ?? []),
            IsBadHabit = p.IsBadHabit,
            IsGeneral = p.IsGeneral,
            IsFlexible = p.IsFlexible,
            DueDate = p.DueDate,
            ScheduledStartDate = p.DueDate,
            OriginalDayOfMonth = p.FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year
                ? p.DueDate.Day
                : null,
            DueTime = p.DueTime,
            DueEndTime = p.DueEndTime,
            ParentHabitId = p.ParentHabitId,
            ReminderEnabled = p.ReminderEnabled,
            ReminderTimes = p.ReminderTimes ?? [15],
            SlipAlertEnabled = p.SlipAlertEnabled,
            ChecklistItems = p.ChecklistItems ?? [],
            ScheduledReminders = p.ScheduledReminders ?? [],
            EndDate = p.EndDate,
            Position = p.Position,
            GoogleEventId = p.GoogleEventId,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result<HabitLog> Log(
        DateOnly date,
        string? note = null,
        bool advanceDueDate = true,
        int weekStartDay = 1)
    {
        if (IsCompleted && !IsGeneral)
            return Result.Failure<HabitLog>(DomainErrors.CannotLogCompletedHabit);

        if (!IsBadHabit && !IsFlexible && _logs.Exists(l => l.Date == date && !l.IsDeleted))
            return Result.Failure<HabitLog>(DomainErrors.AlreadyLoggedForDate);

        var log = HabitLog.Create(Id, date, 1, note);
        _logs.Add(log);

        if (FrequencyUnit is null && !IsGeneral)
        {
            IsCompleted = true;
        }
        else if (FrequencyUnit is not null && !IsFlexible && advanceDueDate)
        {
            AdvanceDueDate(date, weekStartDay);

            if (ChecklistItems.Count > 0)
                ChecklistItems = ChecklistItems.Select(i => i with { IsChecked = false }).ToList();
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public Result AdvanceDueDate(DateOnly today, int weekStartDay = 1)
    {
        var candidate = DueDate;
        var anchor = ScheduledStartDate ?? DueDate;

        do
        {
            var next = AdvanceDueDateByOneStep(candidate, anchor, weekStartDay);
            if (next.IsFailure)
                return Result.Failure(DomainErrors.RecurrenceScheduleUnsatisfiable);

            if (next.Value == candidate) break;
            candidate = next.Value;
        } while (candidate <= today);

        CaptureLegacyScheduledStart();
        DueDate = candidate;

        if (EndDate.HasValue && DueDate > EndDate.Value)
        {
            IsCompleted = true;
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    /// Advances DueDate to the nearest scheduled date on or after today, without going past it.
    /// Used by background service to keep DueDate current for recurring habits.
    /// </summary>
    public Result CatchUpDueDate(DateOnly today, int weekStartDay = 1)
    {
        var shouldAdvance = DueDate < today && !IsCompleted;
        var candidate = DueDate;
        var anchor = ScheduledStartDate ?? DueDate;
        var shouldComplete = IsCompleted;

        while (candidate < today && !shouldComplete)
        {
            var next = AdvanceDueDateByOneStep(candidate, anchor, weekStartDay);
            if (next.IsFailure)
                return Result.Failure(DomainErrors.RecurrenceScheduleUnsatisfiable);

            if (next.Value == candidate) break;
            candidate = next.Value;

            if (EndDate.HasValue && candidate > EndDate.Value)
                shouldComplete = true;
        }

        if (shouldAdvance)
            CaptureLegacyScheduledStart();

        DueDate = candidate;
        IsCompleted = shouldComplete;

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    private void CaptureLegacyScheduledStart()
    {
        if (ScheduledStartDate is null)
            ScheduledStartDate = DueDate;
    }

    /// <summary>
    /// Advances DueDate by one frequency step, re-anchoring for monthly/yearly drift
    /// and snapping to the next matching day-of-week if Days are set.
    /// </summary>
    private Result<DateOnly> AdvanceDueDateByOneStep(
        DateOnly current,
        DateOnly anchor,
        int weekStartDay)
    {
        var advancement = AdvanceFrom(current);
        if (advancement.IsFailure)
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        var next = advancement.Value;
        var intervalWeeks = IntervalWeeks ?? 1;

        if (intervalWeeks <= 1)
            return Result.Success(next);

        var searchLimit = GetRecurrenceSearchLimit(intervalWeeks);
        for (var attempt = 0; attempt < searchLimit; attempt++)
        {
            var activeWeek = IsInActiveWeek(next, anchor, intervalWeeks, weekStartDay);
            if (activeWeek.IsFailure)
                return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

            if (activeWeek.Value)
                return Result.Success(next);

            advancement = AdvanceFrom(next);
            if (advancement.IsFailure || advancement.Value <= next)
                return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

            next = advancement.Value;
        }

        return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);
    }

    private int GetRecurrenceSearchLimit(int intervalWeeks)
    {
        var boundedInterval = Math.Min(intervalWeeks, DomainConstants.MaxIntervalWeeks);
        return FrequencyUnit switch
        {
            Enums.FrequencyUnit.Day => DaysInWeek * boundedInterval,
            Enums.FrequencyUnit.Week => boundedInterval,
            Enums.FrequencyUnit.Month => MonthsInGregorianCycle * boundedInterval,
            Enums.FrequencyUnit.Year => YearsInGregorianCycle * boundedInterval,
            _ => 1
        };
    }

    private Result<DateOnly> AdvanceFrom(DateOnly current)
    {
        try
        {
            var originalDay = OriginalDayOfMonth ?? current.Day;

            var next = (FrequencyUnit, FrequencyQuantity) switch
            {
                (Enums.FrequencyUnit.Day, var q) => current.AddDays(q!.Value),
                (Enums.FrequencyUnit.Week, var q) => current.AddDays(checked(DaysInWeek * q!.Value)),
                (Enums.FrequencyUnit.Month, var q) => current.AddMonths(q!.Value),
                (Enums.FrequencyUnit.Year, var q) => current.AddYears(q!.Value),
                _ => current
            };

            if (FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year)
            {
                var daysInTargetMonth = DateTime.DaysInMonth(next.Year, next.Month);
                var correctedDay = Math.Min(originalDay, daysInTargetMonth);
                next = new DateOnly(next.Year, next.Month, correctedDay);
            }

            if (Days.Count > 0)
            {
                while (!Days.Contains(next.DayOfWeek))
                    next = next.AddDays(1);
            }

            return Result.Success(next);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);
        }
        catch (OverflowException)
        {
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);
        }
    }

    private static Result<bool> IsInActiveWeek(
        DateOnly target,
        DateOnly anchor,
        int intervalWeeks,
        int weekStartDay)
    {
        var targetWeekStart = GetWeekStart(target, weekStartDay);
        if (targetWeekStart.IsFailure)
            return Result.Failure<bool>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        var anchorWeekStart = GetWeekStart(anchor, weekStartDay);
        if (anchorWeekStart.IsFailure)
            return Result.Failure<bool>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        var weekDiff = (targetWeekStart.Value.DayNumber - anchorWeekStart.Value.DayNumber) / 7;
        return Result.Success(((weekDiff % intervalWeeks) + intervalWeeks) % intervalWeeks == 0);
    }

    private static Result<DateOnly> GetWeekStart(DateOnly date, int weekStartDay)
    {
        if (weekStartDay is < 0 or >= DaysInWeek)
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        var daysToStart = ((int)date.DayOfWeek - weekStartDay + 7) % 7;
        if (date.DayNumber < daysToStart)
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        return Result.Success(date.AddDays(-daysToStart));
    }

    /// <summary>
    /// Postpones a one-time task to the given date.
    /// </summary>
    public void PostponeTo(DateOnly date)
    {
        DueDate = date;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public Result<HabitLog> SkipFlexible(DateOnly date)
    {
        if (!IsFlexible)
            return Result.Failure<HabitLog>(DomainErrors.OnlyFlexibleHabitsSkippable);

        if (FrequencyUnit is null)
            return Result.Failure<HabitLog>(DomainErrors.CannotSkipOneTimeTask);

        var log = HabitLog.Create(Id, date, 0, null);
        _logs.Add(log);
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public Result<HabitLog> Unlog(DateOnly date)
    {
        var log = _logs.Find(l => l.Date == date && l.Value > 0 && !l.IsDeleted);
        if (log is null)
            return Result.Failure<HabitLog>(DomainErrors.LogNotFoundForDate);

        log.SoftDelete();

        if (FrequencyUnit is null)
        {
            IsCompleted = false;
        }
        else if (!IsFlexible)
        {
            DueDate = date;
            if (IsCompleted)
                IsCompleted = false;
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public Result Update(HabitUpdateParams p)
    {
        var validation = ValidateUpdate(p);
        if (validation.IsFailure)
            return validation;

        ApplyRequiredUpdates(p);
        ApplyOptionalUpdates(p);

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result ValidateUpdate(HabitUpdateParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Title))
            return Result.Failure(DomainErrors.TitleRequired);

        var validationError = ValidateUpdateParams(p);
        if (validationError is not null)
            return Result.Failure(validationError);

        return Result.Success();
    }

    private AppError? ValidateUpdateParams(HabitUpdateParams p)
    {
        var effectiveIsGeneral = p.IsGeneral ?? IsGeneral;
        var effectiveIsFlexible = p.IsFlexible ?? IsFlexible;

        var scheduleValidation = HabitInvariants.ValidateScheduleOptions(
            effectiveIsGeneral, effectiveIsFlexible, p.IsBadHabit, p.FrequencyUnit, p.FrequencyQuantity, p.Days, p.IntervalWeeks);
        if (scheduleValidation is not null)
            return scheduleValidation;

        var dateValidation = HabitInvariants.ValidateDateOptions(
            p.DueTime ?? DueTime, p.DueEndTime ?? DueEndTime,
            p.ClearEndDate == true ? null : (p.EndDate ?? EndDate),
            p.FrequencyUnit, effectiveIsGeneral, p.DueDate ?? DueDate);
        if (dateValidation is not null)
            return dateValidation;

        var emojiValidation = HabitInvariants.ValidateEmoji(p.Emoji);
        if (emojiValidation is not null)
            return emojiValidation;

        var scheduledReminderValidation = HabitInvariants.ValidateScheduledReminders(p.ScheduledReminders);
        if (scheduledReminderValidation is not null)
            return scheduledReminderValidation;

        return HabitInvariants.ValidateReminderTimes(p.ReminderTimes);
    }

    private void ApplyRequiredUpdates(HabitUpdateParams p)
    {
        var effectiveIsFlexible = p.IsFlexible ?? IsFlexible;
        var effectiveDays = effectiveIsFlexible ? [] : (p.Days?.ToList() ?? []);
        var effectiveDueDate = p.DueDate ?? DueDate;
        var recurrencePhaseChanged = FrequencyUnit != p.FrequencyUnit
            || FrequencyQuantity != p.FrequencyQuantity
            || IntervalWeeks != p.IntervalWeeks
            || !Days.ToHashSet().SetEquals(effectiveDays);

        Title = p.Title.Trim();
        Description = p.Description?.Trim();
        Emoji = HabitInvariants.NormalizeEmoji(p.Emoji);
        FrequencyUnit = p.FrequencyUnit;
        FrequencyQuantity = p.FrequencyQuantity;
        IntervalWeeks = p.IntervalWeeks;
        Days = effectiveDays;
        IsBadHabit = p.IsBadHabit;
        DueTime = p.DueTime;
        DueEndTime = p.DueEndTime;

        if (effectiveDueDate != DueDate || recurrencePhaseChanged)
        {
            DueDate = effectiveDueDate;
            ScheduledStartDate = effectiveDueDate;
        }

        if (FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year)
            OriginalDayOfMonth = DueDate.Day;
        else
            OriginalDayOfMonth = null;
    }

    private void ApplyOptionalUpdates(HabitUpdateParams p)
    {
        if (p.IsGeneral.HasValue)
        {
            IsGeneral = p.IsGeneral.Value;
            if (IsGeneral)
                IsCompleted = false;
        }
        if (p.IsFlexible.HasValue)
            IsFlexible = p.IsFlexible.Value;
        if (p.ReminderEnabled.HasValue)
            ReminderEnabled = p.ReminderEnabled.Value;
        if (p.ReminderTimes is not null)
            ReminderTimes = p.ReminderTimes;
        if (p.SlipAlertEnabled.HasValue)
            SlipAlertEnabled = p.SlipAlertEnabled.Value;
        if (p.ChecklistItems is not null)
            ChecklistItems = p.ChecklistItems;
        if (p.ScheduledReminders is not null)
            ScheduledReminders = p.ScheduledReminders;

        if (p.ClearEndDate == true)
            EndDate = null;
        else if (p.EndDate.HasValue)
            EndDate = p.EndDate.Value;

        if ((p.ClearEndDate == true || p.EndDate.HasValue) && FrequencyUnit is not null)
            RecomputeCompletionForEndDate();
    }

    private void RecomputeCompletionForEndDate()
    {
        if (EndDate.HasValue && DueDate > EndDate.Value)
            IsCompleted = true;
        else if (IsCompleted)
            IsCompleted = false;
    }

    public void UpdateChecklist(IReadOnlyList<ChecklistItem> items)
    {
        ChecklistItems = items;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SetPosition(int? position)
    {
        Position = position;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Assigns or updates the Google Calendar master event ID used by auto-sync dedupe.
    /// Used both at creation time from the sync review flow and by the one-time
    /// reconciliation pass that backfills pre-existing manually-imported habits.
    /// </summary>
    public void SetGoogleEventId(string? googleEventId)
    {
        GoogleEventId = googleEventId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SetParentHabitId(Guid? parentHabitId)
    {
        ParentHabitId = parentHabitId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SoftDelete() => SoftDelete(DateTime.UtcNow);

    /// <summary>
    /// Soft-deletes the habit at a caller-supplied instant so a parent and the sub-habits cascaded
    /// with it share one <see cref="DeletedAtUtc"/>. Restore uses that shared instant to bring back
    /// exactly the subtree a single delete removed, without resurrecting a child deleted earlier in
    /// an unrelated action.
    /// </summary>
    public void SoftDelete(DateTime deletedAtUtc)
    {
        IsDeleted = true;
        DeletedAtUtc = deletedAtUtc;
        UpdatedAtUtc = deletedAtUtc;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddTag(Tag tag) { if (!_tags.Contains(tag)) _tags.Add(tag); }

    public void RemoveTag(Tag tag) => _tags.Remove(tag);

    public void AddGoal(Goal goal)
    {
        if (_goals.Contains(goal))
            return;

        _goals.Add(goal);
        goal.AddHabit(this);
    }

    public void RemoveGoal(Goal goal)
    {
        if (!_goals.Remove(goal))
            return;

        goal.RemoveHabit(this);
    }

    /// <summary>
    /// Removes every goal link through the bidirectional entity methods so each affected goal can
    /// restore manual progress or preserve an earned completion according to its own invariants.
    /// </summary>
    public void RemoveAllGoals()
    {
        foreach (var goal in _goals.ToList())
            RemoveGoal(goal);
    }
}
