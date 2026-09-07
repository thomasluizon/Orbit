using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitScheduleQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GetHabitScheduleQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetHabitScheduleQueryHandlerTests()
    {
        _handler = new GetHabitScheduleQueryHandler(
            _habitRepo,
            _habitLogRepo,
            _userDateService,
            _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Habit CreateTestHabit(
        string title = "Test Habit",
        FrequencyUnit? frequencyUnit = FrequencyUnit.Day,
        int? frequencyQuantity = 1,
        DateOnly? dueDate = null,
        bool isCompleted = false,
        bool isGeneral = false,
        string? description = null,
        Guid? parentHabitId = null,
        bool isBadHabit = false)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, title, frequencyUnit, frequencyQuantity,
            Description: description,
            DueDate: dueDate ?? Today,
            IsGeneral: isGeneral,
            ParentHabitId: parentHabitId,
            IsBadHabit: isBadHabit)).Value;

        if (isCompleted && frequencyUnit is null)
            habit.Log(dueDate ?? Today);

        return habit;
    }

    private void SetupHabits(params Habit[] habits)
    {
        var habitList = habits.ToList().AsReadOnly();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habitList);
    }

    [Fact]
    public async Task Handle_NoHabits_ReturnsEmptyPaginatedResponse()
    {
        SetupHabits();
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_SingleDailyHabitInRange_ReturnsHabit()
    {
        var habit = CreateTestHabit(dueDate: Today);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Test Habit");
    }

    [Fact]
    public async Task Handle_RangeExceedingHorizon_BoundsScheduledDatesAndInstancesConsistently()
    {
        var habit = CreateTestHabit(dueDate: Today);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(200));

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        var item = result.Value.Items[0];
        var horizonEnd = Today.AddDays(AppConstants.MaxInstanceHorizonDays);

        item.ScheduledDates.Should().NotBeEmpty();
        item.ScheduledDates.Max().Should().Be(horizonEnd);
        item.Instances.Should().NotBeEmpty();
        item.Instances.Select(i => i.Date).Max().Should().Be(horizonEnd);
    }

    [Fact]
    public async Task Handle_OneTimeTaskOverdue_WhenIncludeOverdue_ReturnsAsOverdue()
    {
        var pastDate = Today.AddDays(-5);
        var habit = CreateTestHabit(title: "Overdue Task", frequencyUnit: null, frequencyQuantity: null, dueDate: pastDate);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].IsOverdue.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_OneTimeTaskOverdue_WhenNotIncludeOverdue_ExcludesIt()
    {
        var pastDate = Today.AddDays(-5);
        var habit = CreateTestHabit(title: "Overdue", frequencyUnit: null, frequencyQuantity: null, dueDate: pastDate);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: false);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_RecurringOverdue_WhenIncludeOverdue_ReturnsAsOverdue()
    {
        var habit = CreateTestHabit(title: "Overdue Weekly", frequencyUnit: FrequencyUnit.Week, frequencyQuantity: 1, dueDate: Today.AddDays(-4));
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].IsOverdue.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_RecurringOverdueLongAgo_StillReturnsAsOverdue()
    {
        var habit = CreateTestHabit(title: "Long Overdue", frequencyUnit: FrequencyUnit.Week, frequencyQuantity: 1, dueDate: Today.AddDays(-60));
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].IsOverdue.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_RecurringDueToday_NotOverdue()
    {
        var habit = CreateTestHabit(title: "Due Today", frequencyUnit: FrequencyUnit.Day, frequencyQuantity: 1, dueDate: Today);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].IsOverdue.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_Pagination_ReturnsCorrectPage()
    {
        var habits = Enumerable.Range(1, 5).Select(i => CreateTestHabit(title: "Habit" + i, dueDate: Today)).ToArray();
        SetupHabits(habits);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), PageSize: 2, Page: 2);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(2);
        result.Value.TotalCount.Should().Be(5);
        result.Value.TotalPages.Should().Be(3);
        result.Value.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_SearchFilter_FiltersHabitsByTitle()
    {
        var matching = CreateTestHabit(title: "Morning Workout", dueDate: Today);
        var other = CreateTestHabit(title: "Read Book", dueDate: Today);
        SetupHabits(matching, other);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), Search: "Workout");
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Morning Workout");
    }

    [Fact]
    public async Task Handle_FrequencyFilter_FiltersOneTimeTasks()
    {
        var daily = CreateTestHabit(title: "Daily", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        var oneTime = CreateTestHabit(title: "One-time", frequencyUnit: null, frequencyQuantity: null, dueDate: Today);
        SetupHabits(daily, oneTime);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), FrequencyUnitFilter: "none");
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("One-time");
    }

    [Fact]
    public async Task Handle_FrequencyFilter_FiltersByUnit()
    {
        var daily = CreateTestHabit(title: "Daily", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        var weekly = CreateTestHabit(title: "Weekly", frequencyUnit: FrequencyUnit.Week, dueDate: Today);
        SetupHabits(daily, weekly);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), FrequencyUnitFilter: "Week");
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Weekly");
    }

    [Fact]
    public async Task Handle_NoDateRange_ReturnsAllHabits()
    {
        var habit = CreateTestHabit(title: "Some Habit", dueDate: Today);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_GeneralHabits_ReturnsGeneralOnly()
    {
        var general = CreateTestHabit(title: "General", frequencyUnit: null, frequencyQuantity: null, isGeneral: true);
        SetupHabits(general);
        var query = new GetHabitScheduleQuery(UserId, IsGeneral: true);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].IsGeneral.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_GeneralHabitLoggedToday_MapsDatedCompletion()
    {
        var general = CreateTestHabit(
            title: "General",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true);
        general.Log(Today).IsSuccess.Should().BeTrue();
        SetupHabits(general);

        var result = await _handler.Handle(
            new GetHabitScheduleQuery(UserId, IsGeneral: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_GeneralHabitLoggedOnlyYesterday_MapsIncompleteToday()
    {
        var general = CreateTestHabit(
            title: "General",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true);
        general.Log(Today.AddDays(-1)).IsSuccess.Should().BeTrue();
        SetupHabits(general);

        var result = await _handler.Handle(
            new GetHabitScheduleQuery(UserId, IsGeneral: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_GeneralHabitsHistoricalSelectedDay_MapsSelectedDayInsteadOfToday()
    {
        var selectedDay = Today.AddDays(-7);
        var loggedOnSelectedDay = CreateTestHabit(
            title: "Logged on selected day",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true);
        var loggedToday = CreateTestHabit(
            title: "Logged today",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true);
        loggedOnSelectedDay.Log(selectedDay).IsSuccess.Should().BeTrue();
        loggedToday.Log(Today).IsSuccess.Should().BeTrue();
        SetupHabits(loggedOnSelectedDay, loggedToday);

        var result = await _handler.Handle(
            new GetHabitScheduleQuery(UserId, selectedDay, selectedDay, IsGeneral: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(h => h.Id == loggedOnSelectedDay.Id && h.IsCompleted);
        result.Value.Items.Should().ContainSingle(h => h.Id == loggedToday.Id && !h.IsCompleted);
    }

    [Fact]
    public async Task Handle_GeneralHabitWithSoftDeletedLogToday_MapsIncomplete()
    {
        var general = CreateTestHabit(
            title: "General",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true);
        var log = general.Log(Today).Value;
        log.SoftDelete();
        SetupHabits(general);

        var result = await _handler.Handle(
            new GetHabitScheduleQuery(UserId, IsGeneral: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_NestedGeneralChildLoggedToday_MapsDatedCompletion()
    {
        var parent = CreateTestHabit(
            title: "Parent",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true);
        var child = CreateTestHabit(
            title: "Child",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true,
            parentHabitId: parent.Id);
        child.Log(Today).IsSuccess.Should().BeTrue();
        SetupHabits(parent, child);

        var result = await _handler.Handle(
            new GetHabitScheduleQuery(UserId, IsGeneral: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value.Items.Should().ContainSingle().Which;
        item.IsCompleted.Should().BeFalse();
        item.Children.Should().ContainSingle().Which.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_IncludeGeneral_MapsDatedCompletion()
    {
        var scheduled = CreateTestHabit(title: "Scheduled", dueDate: Today);
        var general = CreateTestHabit(
            title: "General",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true);
        general.Log(Today).IsSuccess.Should().BeTrue();
        IReadOnlyList<Habit> scheduledHabits = [scheduled];
        IReadOnlyList<Habit> generalHabits = [general];
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(scheduledHabits, scheduledHabits, generalHabits);

        var result = await _handler.Handle(
            new GetHabitScheduleQuery(UserId, Today, Today, IncludeGeneral: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(h => h.Id == general.Id && h.IsCompleted);
    }

    [Fact]
    public async Task Handle_IncludeGeneralHistoricalSelectedDay_MapsSelectedDayCompletion()
    {
        var selectedDay = Today.AddDays(-7);
        var scheduled = CreateTestHabit(title: "Scheduled", dueDate: selectedDay);
        var general = CreateTestHabit(
            title: "General",
            frequencyUnit: null,
            frequencyQuantity: null,
            isGeneral: true);
        general.Log(selectedDay).IsSuccess.Should().BeTrue();
        IReadOnlyList<Habit> scheduledHabits = [scheduled];
        IReadOnlyList<Habit> generalHabits = [general];
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(scheduledHabits, scheduledHabits, generalHabits);

        var result = await _handler.Handle(
            new GetHabitScheduleQuery(UserId, selectedDay, selectedDay, IncludeGeneral: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(h => h.Id == general.Id && h.IsCompleted);
    }

    [Fact]
    public async Task Handle_NonGeneralOneTimeTaskLoggedYesterday_RetainsLifetimeCompletion()
    {
        var task = CreateTestHabit(
            title: "One time task",
            frequencyUnit: null,
            frequencyQuantity: null,
            dueDate: Today.AddDays(-1));
        task.Log(Today.AddDays(-1)).IsSuccess.Should().BeTrue();
        SetupHabits(task);

        var result = await _handler.Handle(
            new GetHabitScheduleQuery(UserId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_PageBeyondTotal_ClampsToLastPage()
    {
        SetupHabits(CreateTestHabit(dueDate: Today));
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), PageSize: 10, Page: 999);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
    }

    [Fact]
    public async Task Handle_IsCompletedFilter_FiltersCompletedHabits()
    {
        var active = CreateTestHabit(title: "Active", dueDate: Today);
        var done = CreateTestHabit(title: "Done", frequencyUnit: null, frequencyQuantity: null, dueDate: Today, isCompleted: true);
        SetupHabits(active, done);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IsCompleted: false);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().OnlyContain(h => !h.IsCompleted);
    }

    [Fact]
    public async Task Handle_RecurringHabitLoggedInRange_ReturnsCompletedInstanceAfterDueDateAdvances()
    {
        var habit = CreateTestHabit(title: "Daily run", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        habit.Log(Today);
        SetupHabits(habit);

        var query = new GetHabitScheduleQuery(UserId, Today, Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].IsCompleted.Should().BeFalse();
        result.Value.Items[0].IsLoggedInRange.Should().BeTrue();
        result.Value.Items[0].Instances.Should().ContainSingle(i =>
            i.Date == Today && i.Status == InstanceStatus.Completed);
    }

    [Fact]
    public async Task Handle_ParentChildHabits_ChildrenNestedUnderParent()
    {
        var parent = CreateTestHabit(title: "Morning Routine", dueDate: Today);
        var child = CreateTestHabit(title: "Brush Teeth", dueDate: Today, parentHabitId: parent.Id);
        SetupHabits(parent, child);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Morning Routine");
        result.Value.Items[0].HasSubHabits.Should().BeTrue();
        result.Value.Items[0].Children.Should().HaveCount(1);
        result.Value.Items[0].Children[0].Title.Should().Be("Brush Teeth");
    }

    [Fact]
    public async Task Handle_ParentWithNoChildren_HasSubHabitsFalse()
    {
        var parent = CreateTestHabit(title: "Solo Habit", dueDate: Today);
        SetupHabits(parent);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].HasSubHabits.Should().BeFalse();
        result.Value.Items[0].Children.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ParentWithOnlyCompletedOneTimeTaskChild_HasSubHabitsFalse()
    {
        var parent = CreateTestHabit(title: "Morning Routine", dueDate: Today);
        var completedChild = CreateTestHabit(
            title: "Done Step", frequencyUnit: null, frequencyQuantity: null,
            dueDate: Today, isCompleted: true, parentHabitId: parent.Id);
        SetupHabits(parent, completedChild);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].HasSubHabits.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ParentWithIncompleteOneTimeTaskChild_HasSubHabitsTrue()
    {
        var parent = CreateTestHabit(title: "Morning Routine", dueDate: Today);
        var incompleteChild = CreateTestHabit(
            title: "Pending Step", frequencyUnit: null, frequencyQuantity: null,
            dueDate: Today, parentHabitId: parent.Id);
        SetupHabits(parent, incompleteChild);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].HasSubHabits.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_TagFilter_IncludesHabitsWithMatchingTag()
    {
        var tag = Tag.Create(UserId, "Health", "#00FF00").Value;
        var taggedHabit = CreateTestHabit(title: "Running", dueDate: Today);
        taggedHabit.AddTag(tag);
        var untaggedHabit = CreateTestHabit(title: "Coding", dueDate: Today);
        SetupHabits(taggedHabit, untaggedHabit);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), TagIds: [tag.Id]);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Running");
    }

    [Fact]
    public async Task Handle_TagFilter_IncludesParentIfChildHasTag()
    {
        var tag = Tag.Create(UserId, "Health", "#00FF00").Value;
        var parent = CreateTestHabit(title: "Morning Routine", dueDate: Today);
        var child = CreateTestHabit(title: "Stretch", dueDate: Today, parentHabitId: parent.Id);
        child.AddTag(tag);
        SetupHabits(parent, child);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), TagIds: [tag.Id]);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Morning Routine");
    }

    [Fact]
    public async Task Handle_HabitsWithPositions_SortsByPosition()
    {
        var habit1 = CreateTestHabit(title: "Third", dueDate: Today);
        habit1.SetPosition(3);
        var habit2 = CreateTestHabit(title: "First", dueDate: Today);
        habit2.SetPosition(1);
        var habit3 = CreateTestHabit(title: "Second", dueDate: Today);
        habit3.SetPosition(2);
        SetupHabits(habit1, habit2, habit3);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(i => i.Title).Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public async Task Handle_MixedFrequencies_AllReturnedWithCorrectTypes()
    {
        var daily = CreateTestHabit(title: "Daily Habit", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        var weekly = CreateTestHabit(title: "Weekly Habit", frequencyUnit: FrequencyUnit.Week, dueDate: Today);
        var oneTime = CreateTestHabit(title: "One-time Task", frequencyUnit: null, frequencyQuantity: null, dueDate: Today);
        SetupHabits(daily, weekly, oneTime);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(3);
        result.Value.Items.Select(i => i.FrequencyUnit)
            .Should().Contain(new FrequencyUnit?[] { FrequencyUnit.Day, FrequencyUnit.Week, null });
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public async Task Handle_SearchMatchesDescendantTitle_ReturnsParentWithMatchMetadata(
        int depth, bool withDateRange)
    {
        var parent = CreateTestHabit(title: "Morning Routine", dueDate: Today);
        var unrelated = CreateTestHabit(title: "Read", dueDate: Today);
        var habits = new List<Habit> { parent, unrelated };
        var ancestor = parent;
        for (var level = 1; level < depth; level++)
        {
            var intermediate = CreateTestHabit(title: $"Step {level}", parentHabitId: ancestor.Id);
            habits.Add(intermediate);
            ancestor = intermediate;
        }
        var descendant = CreateTestHabit(title: "Meditation", parentHabitId: ancestor.Id);
        habits.Add(descendant);
        SetupHabits(habits.ToArray());

        var query = new GetHabitScheduleQuery(
            UserId,
            withDateRange ? Today : null,
            withDateRange ? Today.AddDays(6) : null,
            Search: "Meditation");
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.Id.Should().Be(parent.Id);
        item.SearchMatches.Should().Equal(new SearchMatchField("child", "Meditation"));
    }

    [Fact]
    public async Task Handle_SearchMatchesDescription_ReturnsHabit()
    {
        var habit = CreateTestHabit(title: "Exercise", description: "30 minutes of cardio every morning", dueDate: Today);
        var other = CreateTestHabit(title: "Read", description: "Read for 20 minutes", dueDate: Today);
        SetupHabits(habit, other);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), Search: "cardio");
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Exercise");
    }

    [Fact]
    public async Task Handle_IsCompletedTrue_ReturnsOnlyCompleted()
    {
        var active = CreateTestHabit(title: "Active", dueDate: Today);
        var done = CreateTestHabit(title: "Done", frequencyUnit: null, frequencyQuantity: null, dueDate: Today, isCompleted: true);
        SetupHabits(active, done);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IsCompleted: true);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().OnlyContain(h => h.IsCompleted);
    }

    [Fact]
    public async Task Handle_SearchMatchesTagName_ReturnsHabit()
    {
        var tag = Tag.Create(UserId, "Fitness", "#FF0000").Value;
        var habit = CreateTestHabit(title: "Run", dueDate: Today);
        habit.AddTag(tag);
        var other = CreateTestHabit(title: "Code", dueDate: Today);
        SetupHabits(habit, other);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), Search: "Fitness");
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Run");
    }

    [Fact]
    public async Task Handle_GeneralHabitsWithSearch_FiltersCorrectly()
    {
        var matching = CreateTestHabit(title: "Stay Hydrated", frequencyUnit: null, frequencyQuantity: null, isGeneral: true);
        var nonMatching = CreateTestHabit(title: "Be Positive", frequencyUnit: null, frequencyQuantity: null, isGeneral: true);
        SetupHabits(matching, nonMatching);

        var query = new GetHabitScheduleQuery(UserId, IsGeneral: true, Search: "Hydrated");
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Stay Hydrated");
    }

    [Fact]
    public async Task Handle_NoDateRange_IncludesChildrenForParent()
    {
        var parent = CreateTestHabit(title: "Parent", dueDate: Today);
        var child = CreateTestHabit(title: "Child", dueDate: Today, parentHabitId: parent.Id);
        SetupHabits(parent, child);

        var query = new GetHabitScheduleQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Children.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_InvalidFrequencyFilter_ReturnsAllHabits()
    {
        var habit = CreateTestHabit(title: "Daily", dueDate: Today);
        SetupHabits(habit);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), FrequencyUnitFilter: "InvalidUnit");
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_GeneralHabitsIsCompletedFilter_FiltersCorrectly()
    {
        var active = CreateTestHabit(title: "Active General", frequencyUnit: null, frequencyQuantity: null, isGeneral: true);
        var done = CreateTestHabit(title: "Done General", frequencyUnit: null, frequencyQuantity: null, isGeneral: true, isCompleted: true);
        SetupHabits(active, done);

        var query = new GetHabitScheduleQuery(UserId, IsGeneral: true, IsCompleted: false);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().OnlyContain(h => !h.IsCompleted);
    }

    [Fact]
    public async Task Handle_HabitWithTags_TagsMappedCorrectly()
    {
        var tag = Tag.Create(UserId, "Health", "#00FF00").Value;
        var habit = CreateTestHabit(title: "Run", dueDate: Today);
        habit.AddTag(tag);
        SetupHabits(habit);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].Tags.Should().HaveCount(1);
        result.Value.Items[0].Tags[0].Name.Should().Be("Health");
        result.Value.Items[0].Tags[0].Color.Should().Be("#00FF00");
    }

    [Fact]
    public async Task Handle_CompletedOneTimeTask_NotOverdue()
    {
        var pastDate = Today.AddDays(-3);
        var habit = CreateTestHabit(title: "Done Task", frequencyUnit: null, frequencyQuantity: null, dueDate: pastDate, isCompleted: true);
        SetupHabits(habit);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Where(h => h.IsOverdue).Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_EmptySearchString_ReturnsAllHabits()
    {
        var habit1 = CreateTestHabit(title: "Habit A", dueDate: Today);
        var habit2 = CreateTestHabit(title: "Habit B", dueDate: Today);
        SetupHabits(habit1, habit2);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), Search: "  ");
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_ParentWithChildDueInRange_ParentIncluded()
    {
        var parent = CreateTestHabit(title: "Project", frequencyUnit: null, frequencyQuantity: null, dueDate: Today.AddDays(-30));
        var child = CreateTestHabit(title: "Step 1", dueDate: Today, parentHabitId: parent.Id);
        SetupHabits(parent, child);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Project");
    }

    [Fact]
    public async Task Handle_SearchWithMatch_PopulatesSearchMatches()
    {
        var habit = CreateTestHabit(title: "Morning Workout", dueDate: Today);
        SetupHabits(habit);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), Search: "Workout");
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].SearchMatches.Should().NotBeNull();
        result.Value.Items[0].SearchMatches!.Any(m => m.Field == "title").Should().BeTrue();
    }

    [Fact]
    public async Task Handle_BadHabitPastDue_NotMarkedOverdue()
    {
        var pastDate = Today.AddDays(-5);
        var badHabit = CreateTestHabit(title: "Smoking", frequencyUnit: FrequencyUnit.Day, dueDate: pastDate, isBadHabit: true);
        SetupHabits(badHabit);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        if (result.Value.Items.Count > 0)
            result.Value.Items.Where(h => h.IsOverdue).Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ParentWithOverdueOneTimeChild_IncludesChildAsOverdue()
    {
        var parent = CreateTestHabit(
            title: "Project",
            frequencyUnit: null,
            frequencyQuantity: null,
            dueDate: Today.AddDays(30));
        var child = CreateTestHabit(
            title: "Past step",
            frequencyUnit: null,
            frequencyQuantity: null,
            dueDate: Today.AddDays(-2),
            parentHabitId: parent.Id);
        SetupHabits(parent, child);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Children.Should().ContainSingle();
        result.Value.Items[0].Children[0].Title.Should().Be("Past step");
        result.Value.Items[0].Children[0].IsOverdue.Should().BeTrue();
        result.Value.Items[0].Children[0].ScheduledDates.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ParentWithOverdueRecurringChild_IncludesChildAsOverdue()
    {
        var parent = CreateTestHabit(
            title: "Project",
            frequencyUnit: null,
            frequencyQuantity: null,
            dueDate: Today.AddDays(30));
        var child = CreateTestHabit(
            title: "Weekly step",
            frequencyUnit: FrequencyUnit.Week,
            frequencyQuantity: 1,
            dueDate: Today.AddDays(-2),
            parentHabitId: parent.Id);
        SetupHabits(parent, child);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Children.Should().ContainSingle();
        result.Value.Items[0].Children[0].Title.Should().Be("Weekly step");
        result.Value.Items[0].Children[0].IsOverdue.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ZeroTotalCount_TotalPagesIsZeroAndPageOne()
    {
        SetupHabits();

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), PageSize: 20, Page: 1);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Page.Should().Be(1);
    }

    [Fact]
    public async Task Handle_TotalCountExactlyDivisibleByPageSize_ComputesExactTotalPages()
    {
        var habits = Enumerable.Range(1, 6).Select(i => CreateTestHabit(title: "Habit" + i, dueDate: Today)).ToArray();
        SetupHabits(habits);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), PageSize: 3, Page: 1);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(6);
        result.Value.TotalPages.Should().Be(2);
        result.Value.Page.Should().Be(1);
        result.Value.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task Handle_TotalCountWithRemainder_RoundsTotalPagesUpAndLastPageHoldsRemainder()
    {
        var habits = Enumerable.Range(1, 7).Select(i => CreateTestHabit(title: "Habit" + i, dueDate: Today)).ToArray();
        SetupHabits(habits);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), PageSize: 3, Page: 3);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(7);
        result.Value.TotalPages.Should().Be(3);
        result.Value.Page.Should().Be(3);
        result.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_PageFarBeyondLast_ClampsToLastPageWithRemainderItem()
    {
        var habits = Enumerable.Range(1, 5).Select(i => CreateTestHabit(title: "Habit" + i, dueDate: Today)).ToArray();
        SetupHabits(habits);

        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), PageSize: 2, Page: 999);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(5);
        result.Value.TotalPages.Should().Be(3);
        result.Value.Page.Should().Be(3);
        result.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_SearchAndFrequencyFilterWithPagination_ReturnsFilteredPagedSubset()
    {
        var runA = CreateTestHabit(title: "Run A", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        runA.SetPosition(1);
        var runB = CreateTestHabit(title: "Run B", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        runB.SetPosition(2);
        var runC = CreateTestHabit(title: "Run C", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        runC.SetPosition(3);
        var runWeekly = CreateTestHabit(title: "Run Weekly", frequencyUnit: FrequencyUnit.Week, dueDate: Today);
        runWeekly.SetPosition(4);
        var walk = CreateTestHabit(title: "Walk", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        walk.SetPosition(5);
        SetupHabits(runA, runB, runC, runWeekly, walk);

        var query = new GetHabitScheduleQuery(
            UserId, Today, Today.AddDays(6),
            Search: "Run", FrequencyUnitFilter: "Day", PageSize: 2, Page: 2);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(3);
        result.Value.TotalPages.Should().Be(2);
        result.Value.Page.Should().Be(2);
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Title.Should().Be("Run C");
    }
}
