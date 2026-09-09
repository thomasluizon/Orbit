using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetRetrospectiveQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IRetrospectiveService _retroService = Substitute.For<IRetrospectiveService>();
    private readonly IUserStreakService _streakService = Substitute.For<IUserStreakService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GetRetrospectiveQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly DateFrom = new(2026, 3, 2);
    private static readonly DateOnly DateTo = new(2026, 3, 8);

    private static readonly RetrospectiveNarrative SampleNarrative =
        new("Highlights body", "Missed body", "Trends body", "Suggestion body");

    public GetRetrospectiveQueryHandlerTests()
    {
        _handler = new GetRetrospectiveQueryHandler(
            _habitRepo,
            _payGate,
            _retroService,
            _streakService,
            _userDateService,
            _cache);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _streakService.RecalculateAsync(UserId, false, Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(4, 9, DateTo));
    }

    private static Habit CreateDailyHabit(string title = "Test Habit", string? emoji = null, bool isBadHabit = false)
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1,
            Emoji: emoji, IsBadHabit: isBadHabit, DueDate: DateFrom)).Value;
    }

    private static Habit CreateLoggedHabit(string title = "Test Habit", string? emoji = null)
    {
        var habit = CreateDailyHabit(title, emoji);
        habit.Log(DateFrom, advanceDueDate: false);
        return habit;
    }

    private void StubHabits(params Habit[] habits)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.ToList().AsReadOnly());
    }

    private void StubNarrative(RetrospectiveNarrative narrative)
    {
        _retroService.GenerateRetrospectiveAsync(
            Arg.Any<List<Habit>>(),
            DateFrom, DateTo, "week", "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(narrative));
    }

    private Task<Result<RetrospectiveResponse>> HandleWeek() =>
        _handler.Handle(new GetRetrospectiveQuery(UserId, DateFrom, DateTo, "week", "en"), CancellationToken.None);

    [Fact]
    public async Task Handle_GeneratesNewRetrospective_WhenNotCached()
    {
        StubHabits(CreateLoggedHabit());
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        result.IsSuccess.Should().BeTrue();
        result.Value.Period.Should().Be("week");
        result.Value.Narrative.Should().Be(SampleNarrative);
        result.Value.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_EchoesStreakFromStreakService()
    {
        StubHabits(CreateLoggedHabit());
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        result.Value.Metrics.CurrentStreak.Should().Be(4);
        result.Value.Metrics.BestStreak.Should().Be(9);
    }

    [Fact]
    public async Task Handle_ComputesPeriodDaysInclusively()
    {
        StubHabits(CreateLoggedHabit());
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        result.Value.Metrics.PeriodDays.Should().Be(7);
    }

    [Fact]
    public async Task Handle_ComputesCompletionRateAndActiveDays()
    {
        var habit = CreateDailyHabit();
        habit.Log(DateFrom, advanceDueDate: false);
        habit.Log(DateFrom.AddDays(1), advanceDueDate: false);
        habit.Log(DateFrom.AddDays(2), advanceDueDate: false);
        StubHabits(habit);
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        var metrics = result.Value.Metrics;
        metrics.TotalScheduled.Should().Be(7);
        metrics.TotalCompletions.Should().Be(3);
        metrics.CompletionRate.Should().Be(43);
        metrics.ActiveDays.Should().Be(3);
    }

    [Fact]
    public async Task Handle_WeeklyConsistency_HasSevenMondayFirstValues()
    {
        var habit = CreateDailyHabit();
        habit.Log(DateFrom, advanceDueDate: false);
        habit.Log(DateFrom.AddDays(2), advanceDueDate: false);
        StubHabits(habit);
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        var consistency = result.Value.Metrics.WeeklyConsistency;
        consistency.Should().HaveCount(7);
        consistency[0].Should().Be(100);
        consistency[1].Should().Be(0);
        consistency[2].Should().Be(100);
        consistency[6].Should().Be(0);
    }

    [Fact]
    public async Task Handle_TopHabits_OrderedByHighestRateFirst()
    {
        var strong = CreateDailyHabit("Strong");
        var weak = CreateDailyHabit("Weak");
        for (var i = 0; i < 7; i++)
            strong.Log(DateFrom.AddDays(i), advanceDueDate: false);
        weak.Log(DateFrom, advanceDueDate: false);
        StubHabits(strong, weak);
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        var top = result.Value.Metrics.TopHabits;
        top.Should().HaveCount(2);
        top[0].Name.Should().Be("Strong");
        top[0].CompletionRate.Should().Be(100);
        top[0].Emoji.Should().BeNull();
        top[1].Name.Should().Be("Weak");
    }

    [Fact]
    public async Task Handle_NeedsAttention_OrderedByLowestRate_ExcludesPerfectHabits()
    {
        var perfect = CreateDailyHabit("Perfect");
        var weak = CreateDailyHabit("Weak");
        var middling = CreateDailyHabit("Middling");
        for (var i = 0; i < 7; i++)
            perfect.Log(DateFrom.AddDays(i), advanceDueDate: false);
        weak.Log(DateFrom, advanceDueDate: false);
        for (var i = 0; i < 4; i++)
            middling.Log(DateFrom.AddDays(i), advanceDueDate: false);
        StubHabits(perfect, weak, middling);
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        var needs = result.Value.Metrics.NeedsAttention;
        needs.Should().HaveCount(2);
        needs.Should().NotContain(s => s.Name == "Perfect");
        needs[0].Name.Should().Be("Weak");
        needs[1].Name.Should().Be("Middling");
    }

    [Fact]
    public async Task Handle_BadHabitSlips_CountedSeparately_NotInHabitLists()
    {
        var bad = CreateDailyHabit("Smoking", isBadHabit: true);
        bad.Log(DateFrom, advanceDueDate: false);
        bad.Log(DateFrom.AddDays(1), advanceDueDate: false);
        StubHabits(bad);
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        var metrics = result.Value.Metrics;
        metrics.BadHabitSlips.Should().Be(2);
        metrics.TotalScheduled.Should().Be(0);
        metrics.TopHabits.Should().BeEmpty();
        metrics.NeedsAttention.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_IncludesHabitEmoji()
    {
        StubHabits(CreateLoggedHabit("Run", "🏃"));
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        result.Value.Metrics.TopHabits.Should().ContainSingle()
            .Which.Emoji.Should().Be("🏃");
    }

    [Fact]
    public async Task Handle_ReturnsCachedResult_WhenCached()
    {
        StubHabits(CreateLoggedHabit());
        StubNarrative(SampleNarrative);

        await HandleWeek();
        var result = await HandleWeek();

        result.IsSuccess.Should().BeTrue();
        result.Value.FromCache.Should().BeTrue();
        result.Value.Narrative.Should().Be(SampleNarrative);
    }

    [Fact]
    public async Task Handle_CacheHit_DoesNotRegenerateNarrative()
    {
        StubHabits(CreateLoggedHabit());
        StubNarrative(SampleNarrative);

        await HandleWeek();
        await HandleWeek();

        await _retroService.Received(1).GenerateRetrospectiveAsync(
            Arg.Any<List<Habit>>(), DateFrom, DateTo, "week", "en", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PayGateFails_ReturnsFailure()
    {
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("PAY_GATE", "PAY_GATE"));

        var result = await HandleWeek();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NoHabits_ReturnsFailure()
    {
        StubHabits();

        var result = await HandleWeek();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No habits found");
    }

    [Fact]
    public async Task Handle_RetrospectiveServiceFails_ReturnsFailure()
    {
        StubHabits(CreateLoggedHabit());
        _retroService.GenerateRetrospectiveAsync(
            Arg.Any<List<Habit>>(),
            DateFrom, DateTo, "week", "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RetrospectiveNarrative>("AI service error"));

        var result = await HandleWeek();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("AI service error");
    }

    [Fact]
    public async Task Handle_HabitsButNoCompletions_ReturnsFailure()
    {
        StubHabits(CreateDailyHabit());
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No habits found");
    }

    [Fact]
    public async Task Handle_CompletionRate_CapsOverLoggingPerHabit_SoMissesStayVisible()
    {
        var overLogged = Habit.Create(new HabitCreateParams(
            UserId, "Consistent", FrequencyUnit.Week, 1, DueDate: DateFrom)).Value;
        for (var i = 0; i < 7; i++)
            overLogged.Log(DateFrom.AddDays(i), advanceDueDate: false);

        var missed = Habit.Create(new HabitCreateParams(
            UserId, "Missed", FrequencyUnit.Week, 1, DueDate: DateFrom)).Value;

        StubHabits(overLogged, missed);
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        var metrics = result.Value.Metrics;
        metrics.CompletionRate.Should().Be(50);
        metrics.TotalCompletions.Should().Be(7);
        metrics.TopHabits.Single(s => s.Name == "Consistent").CompletionRate.Should().Be(100);
    }

    [Fact]
    public async Task Handle_HabitRankings_ExcludeOneTimeTasks()
    {
        var recurring = CreateDailyHabit("Recurring");
        for (var i = 0; i < 6; i++)
            recurring.Log(DateFrom.AddDays(i), advanceDueDate: false);

        var completedOneTime = Habit.Create(new HabitCreateParams(
            UserId, "Completed One Time", null, null, DueDate: DateFrom)).Value;
        completedOneTime.Log(DateFrom, advanceDueDate: false);
        var incompleteOneTime = Habit.Create(new HabitCreateParams(
            UserId, "Incomplete One Time", null, null, DueDate: DateFrom)).Value;
        StubHabits(recurring, completedOneTime, incompleteOneTime);
        StubNarrative(SampleNarrative);

        var result = await HandleWeek();

        var metrics = result.Value.Metrics;
        metrics.TopHabits.Should().ContainSingle()
            .Which.Name.Should().Be("Recurring");
        metrics.TopHabits[0].CompletionRate.Should().Be(86);
        metrics.NeedsAttention.Should().ContainSingle()
            .Which.Name.Should().Be("Recurring");
    }
}
