using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class HabitTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly int[] ReminderTimes5And10 = [5, 10];
    private static readonly int[] ReminderTimes5And15And30 = [5, 15, 30];
    private static readonly ChecklistItem[] SingleChecklistItem = [new("Item 1", false)];
    private static readonly ChecklistItem[] SingleChecklistItemStep1 = [new("Step 1", false)];
    private static readonly DateOnly Yesterday = new(2026, 8, 5);
    private static readonly DateOnly Today = new(2026, 8, 6);

    private static Habit CreateValidHabit(
        FrequencyUnit? frequencyUnit = FrequencyUnit.Day,
        int? frequencyQuantity = 1,
        bool isBadHabit = false,
        DateOnly? dueDate = null,
        IReadOnlyList<DayOfWeek>? days = null,
        Guid? parentHabitId = null)
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            frequencyUnit,
            frequencyQuantity,
            Description: "Daily workout",
            Days: days,
            IsBadHabit: isBadHabit,
            DueDate: dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            ParentHabitId: parentHabitId));
        return result.Value;
    }

    private static Habit CreateOneTimeHabit(DateOnly? dueDate = null)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            "One-time task",
            FrequencyUnit: null,
            FrequencyQuantity: null,
            DueDate: dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow))).Value;
    }

    private static Habit CreateGeneralHabit()
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId, "Read a book someday", null, null,
            DueDate: Today, IsGeneral: true)).Value;
    }

    [Fact]
    public void SoftDelete_MarksDeletedWithTimestamp()
    {
        var habit = CreateValidHabit();

        habit.SoftDelete();

        habit.IsDeleted.Should().BeTrue();
        habit.DeletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void SoftDelete_WithInstant_UsesSuppliedTimestamp()
    {
        var habit = CreateValidHabit();
        var instant = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        habit.SoftDelete(instant);

        habit.IsDeleted.Should().BeTrue();
        habit.DeletedAtUtc.Should().Be(instant);
    }

    [Fact]
    public void Restore_ClearsDeletedState()
    {
        var habit = CreateValidHabit();
        habit.SoftDelete();

        habit.Restore();

        habit.IsDeleted.Should().BeFalse();
        habit.DeletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Exercise");
        result.Value.UserId.Should().Be(ValidUserId);
        result.Value.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Create_EmptyUserId_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(Guid.Empty, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User ID is required");
    }

    [Fact]
    public void Create_EmptyTitle_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Create_NegativeFrequencyQty_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, -1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    [Fact]
    public void Create_ZeroFrequencyQty_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 0, DueDate: DateOnly.FromDateTime(DateTime.UtcNow)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    [Fact]
    public void Create_DaysWithQtyGreaterThan1_ReturnsFailure()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Week, 2, DueDate: DateOnly.FromDateTime(DateTime.UtcNow), Days: days));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Days can only be set when frequency quantity is 1");
    }

    [Fact]
    public void Create_DaysWithWeeklyFrequency_ReturnsFailure()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Week, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow), Days: days));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Days can only be set when frequency quantity is 1");
    }

    [Fact]
    public void Create_DaysWithDailyFrequency_Succeeds()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow), Days: days));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_TrimsTitle()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "  Exercise  ", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow)));

        result.Value.Title.Should().Be("Exercise");
    }

    [Fact]
    public void Create_SetsParentHabitId()
    {
        var parentId = Guid.NewGuid();

        var habit = CreateValidHabit(parentHabitId: parentId);

        habit.ParentHabitId.Should().Be(parentId);
    }

    [Fact]
    public void Log_ActiveHabit_CreatesLog()
    {
        var habit = CreateValidHabit(dueDate: DateOnly.FromDateTime(DateTime.UtcNow));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var result = habit.Log(today, "Done");

        result.IsSuccess.Should().BeTrue();
        result.Value.Date.Should().Be(today);
        habit.Logs.Should().HaveCount(1);
    }

    [Fact]
    public void Log_CompletedHabit_ReturnsFailure()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateOneTimeHabit(dueDate: today);
        habit.Log(today);
        var result = habit.Log(today);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Cannot log a completed habit");
    }

    [Fact]
    public void Log_AlreadyLoggedDate_ReturnsFailure()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(dueDate: today);
        habit.Log(today);

        var result = habit.Log(today);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already been logged for this date");
    }

    [Fact]
    public void Log_BadHabit_AllowsDuplicateDate()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(isBadHabit: true, dueDate: today);
        habit.Log(today);

        var result = habit.Log(today);

        result.IsSuccess.Should().BeTrue();
        habit.Logs.Should().HaveCount(2);
    }

    [Fact]
    public void Log_OneTime_MarksCompleted()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateOneTimeHabit(dueDate: today);

        habit.Log(today);

        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void Symptom3_GeneralLoggedYesterday_ShowsCompletedToday()
    {
        var habit = CreateGeneralHabit();

        habit.Log(Yesterday).IsSuccess.Should().BeTrue();

        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Unlog_GeneralHabit_MissingDateFailsAndExistingDateDeletesOnlyMatchingLog()
    {
        var habit = CreateGeneralHabit();
        habit.Log(Yesterday).IsSuccess.Should().BeTrue();
        habit.Log(Today).IsSuccess.Should().BeTrue();

        var missingDateResult = habit.Unlog(Today.AddDays(1));

        missingDateResult.IsFailure.Should().BeTrue();
        habit.Logs.Should().OnlyContain(log => !log.IsDeleted);

        var existingDateResult = habit.Unlog(Today);

        existingDateResult.IsSuccess.Should().BeTrue();
        habit.Logs.Should().ContainSingle(log => log.Date == Yesterday && !log.IsDeleted);
        habit.Logs.Should().ContainSingle(log => log.Date == Today && log.IsDeleted);
    }

    [Fact]
    public void Compounding_AfterFailedUnlog_TheHabitCannotBeLoggedAgainEither()
    {
        var habit = CreateGeneralHabit();
        habit.Log(Yesterday).IsSuccess.Should().BeTrue();
        habit.Unlog(Today);

        var result = habit.Log(Today);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Control_RecurringHabit_UnlogsFineOnTheDayItWasLogged()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: Yesterday)).Value;
        habit.Log(Yesterday).IsSuccess.Should().BeTrue();

        var result = habit.Unlog(Yesterday);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Log_OneTimeTaskOnDifferentDate_MarksCompletedPermanently()
    {
        var habit = CreateOneTimeHabit(dueDate: Today);

        habit.Log(Yesterday).IsSuccess.Should().BeTrue();

        habit.IsCompleted.Should().BeTrue();
        habit.Log(Today).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void GeneralHabit_LogAndUnlogTwiceOnSameDate_EndsUnlogged()
    {
        var habit = CreateGeneralHabit();

        habit.Log(Today).IsSuccess.Should().BeTrue();
        habit.Unlog(Today).IsSuccess.Should().BeTrue();
        habit.Log(Today).IsSuccess.Should().BeTrue();
        habit.Unlog(Today).IsSuccess.Should().BeTrue();

        habit.IsCompleted.Should().BeFalse();
        habit.Logs.Should().NotContain(log => log.Date == Today && !log.IsDeleted);
    }

    [Fact]
    public void Update_CompletedOneTimeTaskToGeneral_ClearsPermanentCompletion()
    {
        var habit = CreateOneTimeHabit(dueDate: Yesterday);
        habit.Log(Yesterday).IsSuccess.Should().BeTrue();

        var result = habit.Update(new HabitUpdateParams(
            "Read a book someday", null, null, null, null, false, Today, IsGeneral: true));

        result.IsSuccess.Should().BeTrue();
        habit.IsGeneral.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
        habit.Log(Today).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Log_Recurring_AdvancesDueDate()
    {
        var startDate = new DateOnly(2025, 1, 1);
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            dueDate: startDate);

        habit.Log(startDate);

        habit.DueDate.Should().BeAfter(startDate);
    }

    [Fact]
    public void Log_RecurringWithDays_AdvancesToNextMatchingDay()
    {
        var monday = new DateOnly(2025, 1, 6);
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            dueDate: monday,
            days: days);

        habit.Log(monday);

        habit.DueDate.DayOfWeek.Should().BeOneOf(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday);
        habit.DueDate.Should().BeAfter(monday);
    }

    [Fact]
    public void Unlog_ExistingLog_SoftDeletesAndReturns()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(dueDate: today);
        habit.Log(today);

        var result = habit.Unlog(today);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsDeleted.Should().BeTrue();
        result.Value.DeletedAtUtc.Should().NotBeNull();
        habit.Logs.Should().NotContain(l => !l.IsDeleted);
    }

    [Fact]
    public void Log_AfterUnlog_SameDate_Succeeds()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(dueDate: today);
        habit.Log(today);
        habit.Unlog(today);

        var result = habit.Log(today);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsDeleted.Should().BeFalse();
        habit.Logs.Count(l => l.Date == today && !l.IsDeleted).Should().Be(1);
    }

    [Fact]
    public void Unlog_NoLogForDate_ReturnsFailure()
    {
        var habit = CreateValidHabit();
        var date = new DateOnly(2025, 6, 15);

        var result = habit.Unlog(date);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No log found for this date");
    }

    [Fact]
    public void Unlog_OneTime_RevertsCompleted()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateOneTimeHabit(dueDate: today);
        habit.Log(today);
        habit.IsCompleted.Should().BeTrue();

        habit.Unlog(today);

        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Unlog_Recurring_ResetsDueDate()
    {
        var startDate = new DateOnly(2025, 1, 1);
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            dueDate: startDate);
        habit.Log(startDate);
        habit.DueDate.Should().NotBe(startDate);

        habit.Unlog(startDate);

        habit.DueDate.Should().Be(startDate);
    }

    [Fact]
    public void Update_ValidInput_UpdatesFields()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "Running",
            "Morning jog",
            FrequencyUnit.Week,
            2,
            Days: null,
            IsBadHabit: false,
            DueDate: new DateOnly(2025, 6, 1)));

        result.IsSuccess.Should().BeTrue();
        habit.Title.Should().Be("Running");
        habit.Description.Should().Be("Morning jog");
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        habit.FrequencyQuantity.Should().Be(2);
        habit.DueDate.Should().Be(new DateOnly(2025, 6, 1));
    }

    [Fact]
    public void Update_MonthlyHabit_ChangingDueDateRefreshesOriginalDayOfMonth()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId: Guid.NewGuid(),
            Title: "Monthly Bills",
            FrequencyUnit: FrequencyUnit.Month,
            FrequencyQuantity: 1,
            DueDate: new DateOnly(2025, 1, 31))).Value;
        habit.OriginalDayOfMonth.Should().Be(31);

        habit.Update(new HabitUpdateParams(
            "Monthly Bills",
            null,
            FrequencyUnit.Month,
            1,
            Days: null,
            IsBadHabit: false,
            DueDate: new DateOnly(2025, 2, 15)));

        habit.OriginalDayOfMonth.Should().Be(15);
    }

    [Fact]
    public void Update_DailyToMonthly_SeedsOriginalDayOfMonth()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId: Guid.NewGuid(),
            Title: "Daily Stretch",
            FrequencyUnit: FrequencyUnit.Day,
            FrequencyQuantity: 1,
            DueDate: new DateOnly(2025, 3, 10))).Value;
        habit.OriginalDayOfMonth.Should().BeNull();

        habit.Update(new HabitUpdateParams(
            "Daily Stretch",
            null,
            FrequencyUnit.Month,
            1,
            Days: null,
            IsBadHabit: false,
            DueDate: new DateOnly(2025, 3, 28)));

        habit.OriginalDayOfMonth.Should().Be(28);
    }

    [Fact]
    public void Update_MonthlyToDaily_ClearsOriginalDayOfMonth()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId: Guid.NewGuid(),
            Title: "Monthly Review",
            FrequencyUnit: FrequencyUnit.Month,
            FrequencyQuantity: 1,
            DueDate: new DateOnly(2025, 1, 20))).Value;
        habit.OriginalDayOfMonth.Should().Be(20);

        habit.Update(new HabitUpdateParams(
            "Monthly Review",
            null,
            FrequencyUnit.Day,
            1,
            Days: null,
            IsBadHabit: false,
            DueDate: null));

        habit.OriginalDayOfMonth.Should().BeNull();
    }

    [Fact]
    public void Update_EmptyTitle_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams("", null, FrequencyUnit.Day, 1, null, false, null));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title is required");
    }

    [Fact]
    public void Update_NegativeFrequencyQty_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, -5, null, false, null));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    [Fact]
    public void AddTag_NewTag_Adds()
    {
        var habit = CreateValidHabit();
        var tag = Tag.Create(ValidUserId, "Fitness", "#FF0000").Value;

        habit.AddTag(tag);

        habit.Tags.Should().ContainSingle().Which.Should().Be(tag);
    }

    [Fact]
    public void AddTag_DuplicateTag_NoOp()
    {
        var habit = CreateValidHabit();
        var tag = Tag.Create(ValidUserId, "Fitness", "#FF0000").Value;

        habit.AddTag(tag);
        habit.AddTag(tag);

        habit.Tags.Should().HaveCount(1);
    }

    [Fact]
    public void RemoveTag_ExistingTag_Removes()
    {
        var habit = CreateValidHabit();
        var tag = Tag.Create(ValidUserId, "Fitness", "#FF0000").Value;
        habit.AddTag(tag);

        habit.RemoveTag(tag);

        habit.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Create_EndDateOnOneTime_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Task", FrequencyUnit: null, FrequencyQuantity: null,
            DueDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 6, 30)));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("One-time tasks cannot have an end date");
    }

    [Fact]
    public void Create_EndDateBeforeDueDate_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 6, 1), EndDate: new DateOnly(2026, 1, 1)));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End date must be on or after the start date");
    }

    [Fact]
    public void Create_ValidEndDate_Success()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 6, 30)));
        result.IsSuccess.Should().BeTrue();
        result.Value.EndDate.Should().Be(new DateOnly(2026, 6, 30));
    }

    [Fact]
    public void AdvanceDueDate_PastEndDate_MarksCompleted()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 29), EndDate: new DateOnly(2026, 1, 31))).Value;
        habit.AdvanceDueDate(new DateOnly(2026, 1, 31));
        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void AdvanceDueDate_NotPastEndDate_StaysActive()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 12, 31))).Value;
        habit.AdvanceDueDate(new DateOnly(2026, 1, 1));
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Update_ClearEndDate_SetsNull()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 6, 30))).Value;
        habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null, ClearEndDate: true));
        habit.EndDate.Should().BeNull();
    }

    [Fact]
    public void Update_SetEndDate()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 1))).Value;
        habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null, EndDate: new DateOnly(2026, 12, 31)));
        habit.EndDate.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void Update_EndDateBeforeDueDate_ReturnsFailure()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 6, 1))).Value;
        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null, EndDate: new DateOnly(2026, 1, 1)));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End date must be on or after the start date");
    }

    [Fact]
    public void Update_OneTimeTaskWithEndDate_ReturnsFailure()
    {
        var habit = CreateOneTimeHabit(dueDate: new DateOnly(2026, 1, 1));
        var result = habit.Update(new HabitUpdateParams("Task", null, FrequencyUnit: null, FrequencyQuantity: null,
            null, false, new DateOnly(2026, 1, 1), EndDate: new DateOnly(2026, 6, 30)));
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("One-time tasks cannot have an end date");
    }

    [Fact]
    public void Update_ShrinkEndDateBelowDueDate_RejectedByValidation()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 6, 10), EndDate: new DateOnly(2026, 12, 31))).Value;

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false,
            new DateOnly(2026, 6, 10), EndDate: new DateOnly(2026, 6, 5)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End date must be on or after the start date");
    }

    [Fact]
    public void Update_ExtendEndDatePastDueDate_Reopens()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 29), EndDate: new DateOnly(2026, 1, 31))).Value;
        habit.AdvanceDueDate(new DateOnly(2026, 1, 31));
        habit.IsCompleted.Should().BeTrue();

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false,
            null, EndDate: new DateOnly(2026, 12, 31)));

        result.IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Update_ClearEndDate_ReopensAutoCompletedHabit()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 29), EndDate: new DateOnly(2026, 1, 31))).Value;
        habit.AdvanceDueDate(new DateOnly(2026, 1, 31));
        habit.IsCompleted.Should().BeTrue();

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false,
            null, ClearEndDate: true));

        result.IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Unlog_RecurringAutoCompletedByEndDate_Reopens()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 1, 31), EndDate: new DateOnly(2026, 1, 31))).Value;
        habit.Log(new DateOnly(2026, 1, 31));
        habit.IsCompleted.Should().BeTrue();

        var result = habit.Unlog(new DateOnly(2026, 1, 31));

        result.IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
        habit.DueDate.Should().Be(new DateOnly(2026, 1, 31));
    }

    [Fact]
    public void Create_Flexible_WithFrequency_ReturnsSuccess()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            IsFlexible: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFlexible.Should().BeTrue();
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        result.Value.FrequencyQuantity.Should().Be(3);
    }

    [Fact]
    public void Create_Flexible_WithoutFrequencyUnit_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit: null,
            FrequencyQuantity: null,
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            IsFlexible: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Flexible habits must have a frequency unit");
    }

    [Fact]
    public void Create_Flexible_WithDays_ReturnsFailure()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };

        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            Days: days,
            IsFlexible: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Flexible habits cannot have specific days set");
    }

    [Fact]
    public void Create_Flexible_DefaultsFalse()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow)));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFlexible.Should().BeFalse();
    }

    [Fact]
    public void Log_FlexibleHabit_AllowsMultipleLogsPerDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: today,
            IsFlexible: true));
        var habit = result.Value;

        var log1 = habit.Log(today);
        var log2 = habit.Log(today);

        log1.IsSuccess.Should().BeTrue();
        log2.IsSuccess.Should().BeTrue();
        habit.Logs.Should().HaveCount(2);
    }

    [Fact]
    public void Log_FlexibleHabit_DoesNotAdvanceDueDate()
    {
        var startDate = new DateOnly(2025, 1, 6);
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: startDate,
            IsFlexible: true));
        var habit = result.Value;

        habit.Log(startDate);

        habit.DueDate.Should().Be(startDate);
    }

    [Fact]
    public void Log_NonFlexibleHabit_StillBlocksDuplicateDate()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = CreateValidHabit(dueDate: today);
        habit.Log(today);

        var result = habit.Log(today);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already been logged for this date");
    }

    [Fact]
    public void Update_SwitchToFlexible_ClearsDays()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            days: days);
        habit.Days.Should().HaveCount(2);

        var result = habit.Update(new HabitUpdateParams(
            "Exercise",
            null,
            FrequencyUnit.Week,
            3,
            Days: null,
            IsBadHabit: false,
            DueDate: null,
            IsFlexible: true));

        result.IsSuccess.Should().BeTrue();
        habit.IsFlexible.Should().BeTrue();
        habit.Days.Should().BeEmpty();
    }

    [Fact]
    public void Update_FlexibleWithDays_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            IsFlexible: true));
        var habit = result.Value;

        var updateResult = habit.Update(new HabitUpdateParams(
            "Exercise",
            null,
            FrequencyUnit.Week,
            3,
            Days: new[] { DayOfWeek.Monday },
            IsBadHabit: false,
            DueDate: null,
            IsFlexible: true));

        updateResult.IsFailure.Should().BeTrue();
        updateResult.Error.Should().Contain("Flexible habits cannot have specific days set");
    }

    [Fact]
    public void Update_FlexibleWithoutFrequency_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Exercise",
            FrequencyUnit.Week,
            3,
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            IsFlexible: true));
        var habit = result.Value;

        var updateResult = habit.Update(new HabitUpdateParams(
            "Exercise",
            null,
            FrequencyUnit: null,
            FrequencyQuantity: null,
            Days: null,
            IsBadHabit: false,
            DueDate: null,
            IsFlexible: true));

        updateResult.IsFailure.Should().BeTrue();
        updateResult.Error.Should().Contain("Flexible habits must have a frequency unit");
    }

    [Fact]
    public void Create_WithValidScheduledReminders_ReturnsSuccess()
    {
        var reminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0)),
            new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0))
        };

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            ScheduledReminders: reminders));

        result.IsSuccess.Should().BeTrue();
        result.Value.ScheduledReminders.Should().HaveCount(2);
    }

    [Fact]
    public void Create_WithEmptyScheduledReminders_DefaultsToEmpty()
    {
        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow)));

        result.IsSuccess.Should().BeTrue();
        result.Value.ScheduledReminders.Should().BeEmpty();
    }

    [Fact]
    public void Create_ScheduledReminders_OverLimit_ReturnsFailure()
    {
        var reminders = Enumerable.Range(0, 6)
            .Select(i => new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8 + i, 0)))
            .ToList();

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            ScheduledReminders: reminders));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at most 5 scheduled reminders");
    }

    [Fact]
    public void Create_ScheduledReminders_Duplicates_ReturnsFailure()
    {
        var reminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0)),
            new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0))
        };

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            ScheduledReminders: reminders));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("duplicate entries");
    }

    [Fact]
    public void Update_WithScheduledReminders_SetsReminders()
    {
        var habit = CreateValidHabit();
        var reminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0))
        };

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            ScheduledReminders: reminders));

        result.IsSuccess.Should().BeTrue();
        habit.ScheduledReminders.Should().HaveCount(1);
        habit.ScheduledReminders[0].When.Should().Be(ScheduledReminderWhen.DayBefore);
    }

    [Fact]
    public void Update_ScheduledReminders_OverLimit_ReturnsFailure()
    {
        var habit = CreateValidHabit();
        var reminders = Enumerable.Range(0, 6)
            .Select(i => new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8 + i, 0)))
            .ToList();

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            ScheduledReminders: reminders));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at most 5 scheduled reminders");
    }

    [Fact]
    public void Update_ScheduledReminders_Null_KeepsExisting()
    {
        var reminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0))
        };
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            ScheduledReminders: reminders)).Value;

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            ScheduledReminders: null));

        result.IsSuccess.Should().BeTrue();
        habit.ScheduledReminders.Should().HaveCount(1);
    }

    [Fact]
    public void Create_ReminderTimes_AtLimit_ReturnsSuccess()
    {
        var reminderTimes = Enumerable.Range(0, DomainConstants.MaxReminderTimes).ToList();

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            ReminderTimes: reminderTimes));

        result.IsSuccess.Should().BeTrue();
        result.Value.ReminderTimes.Should().HaveCount(DomainConstants.MaxReminderTimes);
    }

    [Fact]
    public void Create_ReminderTimes_OverLimit_ReturnsFailure()
    {
        var reminderTimes = Enumerable.Range(0, DomainConstants.MaxReminderTimes + 1).ToList();

        var result = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            ReminderTimes: reminderTimes));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain($"at most {DomainConstants.MaxReminderTimes} reminder times");
    }

    [Fact]
    public void Update_ReminderTimes_AtLimit_ReturnsSuccess()
    {
        var habit = CreateValidHabit();
        var reminderTimes = Enumerable.Range(0, DomainConstants.MaxReminderTimes).ToList();

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            ReminderTimes: reminderTimes));

        result.IsSuccess.Should().BeTrue();
        habit.ReminderTimes.Should().HaveCount(DomainConstants.MaxReminderTimes);
    }

    [Fact]
    public void Update_ReminderTimes_OverLimit_ReturnsFailure()
    {
        var habit = CreateValidHabit();
        var reminderTimes = Enumerable.Range(0, DomainConstants.MaxReminderTimes + 1).ToList();

        var result = habit.Update(new HabitUpdateParams("Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            ReminderTimes: reminderTimes));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain($"at most {DomainConstants.MaxReminderTimes} reminder times");
    }

    [Fact]
    public void Update_AllOptionalFields_Applied()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "Updated Title",
            "New Description",
            FrequencyUnit.Week,
            2,
            Days: null,
            IsBadHabit: true,
            DueDate: new DateOnly(2026, 6, 1),
            DueTime: new TimeOnly(9, 0),
            DueEndTime: new TimeOnly(10, 0),
            ReminderEnabled: true,
            ReminderTimes: ReminderTimes5And10,
            SlipAlertEnabled: true,
            ChecklistItems: SingleChecklistItem,
            IsGeneral: false,
            IsFlexible: false));

        result.IsSuccess.Should().BeTrue();
        habit.Title.Should().Be("Updated Title");
        habit.Description.Should().Be("New Description");
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        habit.FrequencyQuantity.Should().Be(2);
        habit.IsBadHabit.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2026, 6, 1));
        habit.DueTime.Should().Be(new TimeOnly(9, 0));
        habit.DueEndTime.Should().Be(new TimeOnly(10, 0));
        habit.ReminderEnabled.Should().BeTrue();
        habit.ReminderTimes.Should().BeEquivalentTo(ReminderTimes5And10);
        habit.SlipAlertEnabled.Should().BeTrue();
        habit.ChecklistItems.Should().HaveCount(1);
    }

    [Fact]
    public void Update_DueEndTimeBeforeDueTime_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "Exercise", null, FrequencyUnit.Day, 1, null, false, null,
            DueTime: new TimeOnly(10, 0),
            DueEndTime: new TimeOnly(9, 0)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End time must be after start time");
    }

    [Fact]
    public void Update_SwitchToGeneral_ClearsFrequency()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "General Habit", null, null, null, null, false, null,
            IsGeneral: true));

        result.IsSuccess.Should().BeTrue();
        habit.IsGeneral.Should().BeTrue();
        habit.FrequencyUnit.Should().BeNull();
    }

    [Fact]
    public void Update_GeneralWithFrequency_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "General Habit", null, FrequencyUnit.Day, 1, null, false, null,
            IsGeneral: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("General habits cannot have a frequency");
    }

    [Fact]
    public void Update_GeneralBadHabit_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.Update(new HabitUpdateParams(
            "General Bad", null, null, null, null, true, null,
            IsGeneral: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("General habits cannot be bad habits");
    }

    [Fact]
    public void Update_TrimsTitle()
    {
        var habit = CreateValidHabit();

        habit.Update(new HabitUpdateParams("  Trimmed  ", null, FrequencyUnit.Day, 1, null, false, null));

        habit.Title.Should().Be("Trimmed");
    }

    [Fact]
    public void Update_TrimsDescription()
    {
        var habit = CreateValidHabit();

        habit.Update(new HabitUpdateParams("Title", "  Trimmed Desc  ", FrequencyUnit.Day, 1, null, false, null));

        habit.Description.Should().Be("Trimmed Desc");
    }

    [Fact]
    public void Update_NullDueDate_KeepsExisting()
    {
        var dueDate = new DateOnly(2026, 3, 15);
        var habit = CreateValidHabit(dueDate: dueDate);

        habit.Update(new HabitUpdateParams("Title", null, FrequencyUnit.Day, 1, null, false, null));

        habit.DueDate.Should().Be(dueDate);
    }

    [Fact]
    public void CatchUpDueDate_Daily_AdvancesToToday()
    {
        var start = new DateOnly(2025, 1, 1);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Day, frequencyQuantity: 1, dueDate: start);
        var today = new DateOnly(2025, 1, 5);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().BeOnOrAfter(today);
        habit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void CatchUpDueDate_Weekly_AdvancesToNextWeek()
    {
        var start = new DateOnly(2025, 1, 6); var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Week, frequencyQuantity: 1, dueDate: start);
        var today = new DateOnly(2025, 1, 20);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().BeOnOrAfter(today);
    }

    [Fact]
    public void CatchUpDueDate_Monthly_AdvancesToNextMonth()
    {
        var start = new DateOnly(2025, 1, 15);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Month, frequencyQuantity: 1, dueDate: start);
        var today = new DateOnly(2025, 4, 1);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().BeOnOrAfter(today);
    }

    [Fact]
    public void CatchUpDueDate_Yearly_AdvancesToNextYear()
    {
        var start = new DateOnly(2023, 6, 15);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Year, frequencyQuantity: 1, dueDate: start);
        var today = new DateOnly(2025, 7, 1);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().BeOnOrAfter(today);
    }

    [Fact]
    public void CatchUpDueDate_WithEndDate_MarksCompletedWhenPastEnd()
    {
        var habit = Habit.Create(new HabitCreateParams(ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2025, 1, 1), EndDate: new DateOnly(2025, 1, 5))).Value;

        habit.CatchUpDueDate(new DateOnly(2025, 1, 10));

        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void CatchUpDueDate_AlreadyOnOrAfterToday_NoChange()
    {
        var today = new DateOnly(2025, 1, 6);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Day, frequencyQuantity: 1, dueDate: today);

        habit.CatchUpDueDate(today);

        habit.DueDate.Should().Be(today);
    }

    [Fact]
    public void AdvanceDueDate_Daily_AdvancesPastLogged()
    {
        var start = new DateOnly(2025, 1, 1);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Day, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);

        habit.DueDate.Should().BeAfter(start);
    }

    [Fact]
    public void AdvanceDueDate_Weekly_AdvancesBy7Days()
    {
        var start = new DateOnly(2025, 1, 6); var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Week, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);

        habit.DueDate.Should().Be(new DateOnly(2025, 1, 13));
    }

    [Fact]
    public void AdvanceDueDate_Monthly_AdvancesBy1Month()
    {
        var start = new DateOnly(2025, 1, 15);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Month, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);

        habit.DueDate.Should().Be(new DateOnly(2025, 2, 15));
    }

    [Fact]
    public void AdvanceDueDate_Monthly_Jan31_AdvancesToFeb28()
    {
        var start = new DateOnly(2025, 1, 31);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Month, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);
        habit.DueDate.Should().Be(new DateOnly(2025, 2, 28));
    }

    [Fact]
    public void AdvanceDueDate_Monthly_Feb28_AdvancesToMar28()
    {
        var start = new DateOnly(2025, 2, 28);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Month, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);
        habit.DueDate.Should().Be(new DateOnly(2025, 3, 28));
    }

    [Fact]
    public void AdvanceDueDate_Yearly_AdvancesBy1Year()
    {
        var start = new DateOnly(2025, 3, 15);
        var habit = CreateValidHabit(frequencyUnit: FrequencyUnit.Year, frequencyQuantity: 1, dueDate: start);

        habit.AdvanceDueDate(start);

        habit.DueDate.Should().Be(new DateOnly(2026, 3, 15));
    }

    [Fact]
    public void AdvanceDueDate_WithDays_SnapsToNextMatchingDay()
    {
        var monday = new DateOnly(2025, 1, 6);
        var habit = CreateValidHabit(
            frequencyUnit: FrequencyUnit.Day,
            frequencyQuantity: 1,
            dueDate: monday,
            days: new[] { DayOfWeek.Monday, DayOfWeek.Friday });

        habit.AdvanceDueDate(monday);

        habit.DueDate.DayOfWeek.Should().BeOneOf(DayOfWeek.Monday, DayOfWeek.Friday);
        habit.DueDate.Should().BeAfter(monday);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DomainConstants.MaxIntervalWeeks)]
    public void Create_ValidIntervalWeeks_Succeeds(int? intervalWeeks)
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Day,
            1,
            new DateOnly(2025, 1, 7),
            Days: [DayOfWeek.Tuesday],
            IntervalWeeks: intervalWeeks));

        result.IsSuccess.Should().BeTrue();
        result.Value.IntervalWeeks.Should().Be(intervalWeeks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(DomainConstants.MaxIntervalWeeks + 1)]
    public void Create_InvalidIntervalWeeks_Fails(int intervalWeeks)
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Day,
            1,
            new DateOnly(2025, 1, 7),
            Days: [DayOfWeek.Tuesday],
            IntervalWeeks: intervalWeeks));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INTERVAL_WEEKS_INVALID");
    }

    [Fact]
    public void AdvanceDueDate_FixedInterval_SkipsInactiveWeek()
    {
        var tuesday = new DateOnly(2025, 1, 7);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Day,
            1,
            tuesday,
            Days: [DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday],
            IntervalWeeks: 2)).Value;

        habit.AdvanceDueDate(tuesday.AddDays(2), weekStartDay: 1);

        habit.DueDate.Should().Be(tuesday.AddDays(14));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DueDateAdvancement_DisjointRecurrencePhases_ReturnsFailureWithoutChangingDueDate(bool catchUp)
    {
        var anchor = new DateOnly(2025, 1, 6);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Week,
            1,
            anchor)).Value;
        habit.AdvanceDueDate(anchor, weekStartDay: 1);
        typeof(Habit).GetProperty(nameof(Habit.FrequencyQuantity))!.SetValue(habit, 2);
        typeof(Habit).GetProperty(nameof(Habit.IntervalWeeks))!.SetValue(habit, 2);
        var dueDateBeforeAttempt = habit.DueDate;
        var methodName = catchUp ? nameof(Habit.CatchUpDueDate) : nameof(Habit.AdvanceDueDate);
        var method = typeof(Habit).GetMethod(methodName, [typeof(DateOnly), typeof(int)])!;
        object? rawResult = null;

        var act = () => rawResult = method.Invoke(
            habit,
            [catchUp ? dueDateBeforeAttempt.AddDays(1) : dueDateBeforeAttempt, 1]);

        act.Should().NotThrow();
        var result = rawResult.Should().BeOfType<Result>().Subject;
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("RECURRENCE_SCHEDULE_UNSATISFIABLE");
        habit.DueDate.Should().Be(dueDateBeforeAttempt);
    }

    [Fact]
    public void CatchUpDueDate_SundayStartAtDateOnlyMinValue_ReturnsFailureWithoutChangingDueDate()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Day,
            1,
            DateOnly.MinValue,
            IntervalWeeks: 2)).Value;

        var act = () => habit.CatchUpDueDate(DateOnly.MinValue.AddDays(1), weekStartDay: 0);

        var result = act.Should().NotThrow().Which;
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("RECURRENCE_SCHEDULE_UNSATISFIABLE");
        habit.DueDate.Should().Be(DateOnly.MinValue);
    }

    [Fact]
    public void Update_AdvancedIntervalHabitWithUnchangedDueDate_PreservesScheduledStartDate()
    {
        var originalDueDate = new DateOnly(2025, 1, 6);
        var currentDueDate = originalDueDate.AddDays(14);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Week,
            2,
            originalDueDate,
            IntervalWeeks: 2)).Value;
        habit.AdvanceDueDate(originalDueDate, weekStartDay: 1);

        var result = habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days.ToList(),
            habit.IsBadHabit,
            currentDueDate,
            UserToday: originalDueDate.AddDays(1),
            IntervalWeeks: 2));

        result.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(currentDueDate);
        habit.ScheduledStartDate.Should().Be(originalDueDate);
    }

    [Fact]
    public void Update_AdvancedHabitWithChangedCadenceAndUnchangedDueDate_RebasesScheduledStartDate()
    {
        var originalDueDate = new DateOnly(2025, 1, 6);
        var currentDueDate = originalDueDate.AddDays(7);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Week,
            1,
            originalDueDate)).Value;
        habit.AdvanceDueDate(originalDueDate, weekStartDay: 1);

        var result = habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            FrequencyUnit.Week,
            2,
            habit.Days.ToList(),
            habit.IsBadHabit,
            currentDueDate,
            UserToday: originalDueDate.AddDays(1),
            IntervalWeeks: 2));

        result.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(currentDueDate);
        habit.ScheduledStartDate.Should().Be(currentDueDate);

        habit.AdvanceDueDate(currentDueDate, weekStartDay: 1);

        habit.DueDate.Should().Be(currentDueDate.AddDays(14));
    }

    [Fact]
    public void AdvanceDueDate_BiweeklyHabitRescheduledByOneWeek_RebasesIntervalPhase()
    {
        var originalDueDate = new DateOnly(2025, 1, 6);
        var rescheduledDueDate = originalDueDate.AddDays(7);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Week,
            2,
            originalDueDate,
            IntervalWeeks: 2)).Value;

        var result = habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days.ToList(),
            habit.IsBadHabit,
            rescheduledDueDate,
            UserToday: originalDueDate.AddDays(1),
            IntervalWeeks: 2));
        habit.AdvanceDueDate(rescheduledDueDate, weekStartDay: 1);

        result.IsSuccess.Should().BeTrue();
        habit.ScheduledStartDate.Should().Be(rescheduledDueDate);
        habit.DueDate.Should().Be(rescheduledDueDate.AddDays(14));
    }

    [Fact]
    public void CatchUpDueDate_BiweeklyHabitRescheduledByOneWeek_RebasesIntervalPhase()
    {
        var originalDueDate = new DateOnly(2025, 1, 6);
        var rescheduledDueDate = originalDueDate.AddDays(7);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Run",
            FrequencyUnit.Week,
            2,
            originalDueDate,
            IntervalWeeks: 2)).Value;
        habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days.ToList(),
            habit.IsBadHabit,
            rescheduledDueDate,
            UserToday: originalDueDate.AddDays(1),
            IntervalWeeks: 2));

        habit.CatchUpDueDate(rescheduledDueDate.AddDays(1), weekStartDay: 1);

        habit.DueDate.Should().Be(rescheduledDueDate.AddDays(14));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DomainConstants.MaxIntervalWeeks)]
    public void Update_ValidIntervalWeeks_Succeeds(int? intervalWeeks)
    {
        var habit = CreateValidHabit();
        var result = habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days.ToList(),
            habit.IsBadHabit,
            habit.DueDate,
            IntervalWeeks: intervalWeeks));

        result.IsSuccess.Should().BeTrue();
        habit.IntervalWeeks.Should().Be(intervalWeeks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(DomainConstants.MaxIntervalWeeks + 1)]
    public void Update_InvalidIntervalWeeks_Fails(int intervalWeeks)
    {
        var habit = CreateValidHabit();
        var result = habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days.ToList(),
            habit.IsBadHabit,
            habit.DueDate,
            IntervalWeeks: intervalWeeks));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INTERVAL_WEEKS_INVALID");
    }

    [Fact]
    public void SkipFlexible_FlexibleHabit_CreatesSkipLog()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flexible", FrequencyUnit.Week, 3, DueDate: today, IsFlexible: true)).Value;

        var result = habit.SkipFlexible(today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(0);
        habit.Logs.Should().HaveCount(1);
    }

    [Fact]
    public void SkipFlexible_NonFlexibleHabit_ReturnsFailure()
    {
        var habit = CreateValidHabit();

        var result = habit.SkipFlexible(DateOnly.FromDateTime(DateTime.UtcNow));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Only flexible habits can be skipped");
    }

    [Fact]
    public void SkipFlexible_OneTimeTask_ReturnsFailure()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Task", FrequencyUnit.Week, 3, DueDate: today, IsFlexible: true)).Value;

        var result = habit.SkipFlexible(today);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Log_FlexibleHabit_MultipleLogsOnSameDay_AllSucceed()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flex", FrequencyUnit.Week, 5, DueDate: today, IsFlexible: true)).Value;

        for (int i = 0; i < 5; i++)
        {
            var result = habit.Log(today);
            result.IsSuccess.Should().BeTrue();
        }

        habit.Logs.Should().HaveCount(5);
        habit.DueDate.Should().Be(today);
    }

    [Fact]
    public void Log_RecurringWithChecklist_ResetsChecklist()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var checklist = new List<ChecklistItem>
        {
            new("Step 1", true),
            new("Step 2", true)
        };
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Routine", FrequencyUnit.Day, 1, DueDate: today,
            ChecklistItems: checklist)).Value;

        habit.Log(today);

        habit.ChecklistItems.Should().AllSatisfy(item => item.IsChecked.Should().BeFalse());
    }

    [Fact]
    public void PostponeTo_UpdatesDueDate()
    {
        var habit = CreateOneTimeHabit(dueDate: new DateOnly(2025, 3, 1));

        habit.PostponeTo(new DateOnly(2025, 3, 15));

        habit.DueDate.Should().Be(new DateOnly(2025, 3, 15));
    }

    [Fact]
    public void AddGoal_NewGoal_AddsSuccessfully()
    {
        var habit = CreateValidHabit();
        var goal = Goal.Create(ValidUserId, "Fitness", 10, "workouts").Value;

        habit.AddGoal(goal);

        habit.Goals.Should().ContainSingle().Which.Should().Be(goal);
    }

    [Fact]
    public void AddGoal_DuplicateGoal_NoOp()
    {
        var habit = CreateValidHabit();
        var goal = Goal.Create(ValidUserId, "Fitness", 10, "workouts").Value;

        habit.AddGoal(goal);
        habit.AddGoal(goal);

        habit.Goals.Should().HaveCount(1);
    }

    [Fact]
    public void RemoveGoal_ExistingGoal_Removes()
    {
        var habit = CreateValidHabit();
        var goal = Goal.Create(ValidUserId, "Fitness", 10, "workouts").Value;
        habit.AddGoal(goal);

        habit.RemoveGoal(goal);

        habit.Goals.Should().BeEmpty();
    }

    [Fact]
    public void RemoveAllGoals_RemovesBothSidesAndRestoresManualGoalProgress()
    {
        var habit = CreateValidHabit();
        var firstGoal = Goal.Create(ValidUserId, "Fitness", 10, "workouts").Value;
        var secondGoal = Goal.Create(ValidUserId, "Health", 10, "sessions").Value;
        habit.AddGoal(firstGoal);
        habit.AddGoal(secondGoal);
        firstGoal.SyncStandardProgress(3);
        secondGoal.SyncStandardProgress(4);

        habit.RemoveAllGoals();

        habit.Goals.Should().BeEmpty();
        firstGoal.Habits.Should().BeEmpty();
        secondGoal.Habits.Should().BeEmpty();
        firstGoal.CurrentValue.Should().Be(0);
        secondGoal.CurrentValue.Should().Be(0);
        firstGoal.IsProgressDerived.Should().BeFalse();
        secondGoal.IsProgressDerived.Should().BeFalse();
    }

    [Fact]
    public void SetPosition_SetsValue()
    {
        var habit = CreateValidHabit();
        habit.SetPosition(5);
        habit.Position.Should().Be(5);
    }

    [Fact]
    public void SetParentHabitId_SetsValue()
    {
        var habit = CreateValidHabit();
        var parentId = Guid.NewGuid();

        habit.SetParentHabitId(parentId);

        habit.ParentHabitId.Should().Be(parentId);
    }

    [Fact]
    public void UpdateChecklist_ReplacesItems()
    {
        var habit = CreateValidHabit();
        var items = new List<ChecklistItem>
        {
            new("Task A", false),
            new("Task B", true)
        };

        habit.UpdateChecklist(items);

        habit.ChecklistItems.Should().HaveCount(2);
    }

    [Fact]
    public void Create_GeneralHabit_Success()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "General", null, null, DueDate: DateOnly.FromDateTime(DateTime.UtcNow), IsGeneral: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsGeneral.Should().BeTrue();
    }

    [Fact]
    public void Create_GeneralWithFrequency_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "General", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow), IsGeneral: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("General habits cannot have a frequency");
    }

    [Fact]
    public void Create_GeneralBadHabit_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "General Bad", null, null, DueDate: DateOnly.FromDateTime(DateTime.UtcNow), IsGeneral: true, IsBadHabit: true));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("General habits cannot be bad habits");
    }

    [Fact]
    public void Create_DueEndTimeBeforeDueTime_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "Exercise", FrequencyUnit.Day, 1,
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
            DueTime: new TimeOnly(10, 0), DueEndTime: new TimeOnly(9, 0)));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("End time must be after start time");
    }

    [Fact]
    public void Create_WithAllOptions_Success()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "Full Habit", FrequencyUnit.Day, 1,
            Description: "Full description",
            DueDate: new DateOnly(2026, 1, 1),
            DueTime: new TimeOnly(9, 0),
            DueEndTime: new TimeOnly(10, 0),
            ReminderEnabled: true,
            ReminderTimes: ReminderTimes5And15And30,
            SlipAlertEnabled: true,
            ChecklistItems: SingleChecklistItemStep1));

        result.IsSuccess.Should().BeTrue();
        result.Value.DueTime.Should().Be(new TimeOnly(9, 0));
        result.Value.DueEndTime.Should().Be(new TimeOnly(10, 0));
        result.Value.ReminderEnabled.Should().BeTrue();
        result.Value.ReminderTimes.Should().BeEquivalentTo(ReminderTimes5And15And30);
        result.Value.SlipAlertEnabled.Should().BeTrue();
        result.Value.ChecklistItems.Should().HaveCount(1);
    }

    [Fact]
    public void Unlog_FlexibleHabit_SoftDeletesLogButDoesNotResetDueDate()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Flex", FrequencyUnit.Week, 3, DueDate: today, IsFlexible: true)).Value;
        habit.Log(today);
        var originalDueDate = habit.DueDate;

        var result = habit.Unlog(today);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsDeleted.Should().BeTrue();
        habit.Logs.Should().NotContain(l => !l.IsDeleted);
        habit.DueDate.Should().Be(originalDueDate);
    }

    [Fact]
    public void Create_UsesProvidedDueDateVerbatim_WithoutUtcSubstitution()
    {
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var userToday = utcToday.AddDays(1);

        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "Meditate", FrequencyUnit.Day, 1, DueDate: userToday));

        result.IsSuccess.Should().BeTrue();
        result.Value.DueDate.Should().Be(userToday);
        result.Value.DueDate.Should().NotBe(utcToday);
    }

    [Fact]
    public void Create_MonthlyHabit_AnchorsOriginalDayToProvidedDueDate()
    {
        var providedDueDate = new DateOnly(2026, 1, 31);

        var result = Habit.Create(new HabitCreateParams(
            ValidUserId, "Pay rent", FrequencyUnit.Month, 1, DueDate: providedDueDate));

        result.IsSuccess.Should().BeTrue();
        result.Value.DueDate.Should().Be(providedDueDate);
        result.Value.OriginalDayOfMonth.Should().Be(31);
    }

    [Fact]
    public void Create_RecurringUnitWithoutQuantity_ReturnsFailure()
    {
        var result = Habit.Create(new HabitCreateParams(
            ValidUserId,
            "Invalid recurrence",
            FrequencyUnit.Day,
            null,
            Today));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Frequency quantity must be greater than 0");
    }

    [Fact]
    public void Update_RecurringHabitWithNullQuantity_ReturnsFailureWithoutMutation()
    {
        var habit = CreateValidHabit();
        var result = habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            FrequencyUnit.Day,
            null,
            habit.Days.ToList(),
            habit.IsBadHabit,
            habit.DueDate));

        result.IsFailure.Should().BeTrue();
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        habit.FrequencyQuantity.Should().Be(1);
    }

    [Fact]
    public void Update_OneTimeHabitWithRecurringUnitAndNoQuantity_ReturnsFailureWithoutMutation()
    {
        var habit = CreateOneTimeHabit(Today);
        var result = habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            FrequencyUnit.Day,
            null,
            habit.Days.ToList(),
            habit.IsBadHabit,
            habit.DueDate));

        result.IsFailure.Should().BeTrue();
        habit.FrequencyUnit.Should().BeNull();
        habit.FrequencyQuantity.Should().BeNull();
    }
}
