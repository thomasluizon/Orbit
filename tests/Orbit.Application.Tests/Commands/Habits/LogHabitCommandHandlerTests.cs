using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Challenges.Services;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Data.Common;
using System.Linq.Expressions;
using Orbit.Application.Tests.Common;

namespace Orbit.Application.Tests.Commands.Habits;

public class LogHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IGoalCompletionService _goalCompletionService = Substitute.For<IGoalCompletionService>();
    private readonly IChallengeProgressService _challengeProgressService = Substitute.For<IChallengeProgressService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MediatR.IMediator _mediator = Substitute.For<MediatR.IMediator>();
    private readonly ILogger<LogHabitCommandHandler> _logger = Substitute.For<ILogger<LogHabitCommandHandler>>();
    private readonly LogHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public LogHabitCommandHandlerTests()
    {
        _unitOfWork.PassThroughTransactions<Result<LogHabitCommandHandler.PersistedLog>>();
        var repos = new LogHabitRepositories(_habitRepo, _habitLogRepo, _userRepo);
        var services = new LogHabitServices(
            _userDateService,
            _userStreakService,
            _gamificationService,
            _goalCompletionService,
            _challengeProgressService,
            _mediator,
            _payGate);
        _handler = new LogHabitCommandHandler(
            repos, services, _unitOfWork, _cache, _logger);
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<LogHabitResponse>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<LogHabitResponse>>>>(0)(
                call.ArgAt<CancellationToken>(1)));

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _userStreakService.RecalculateAsync(Arg.Any<Guid>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(1, 1, Today));
        _goalCompletionService.SyncDerivedGoalsAsync(
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<DateOnly>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GoalCompletionUpdate>());

        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private static Habit CreateTestHabit(Guid? userId = null)
    {
        return Habit.Create(new HabitCreateParams(
            userId ?? UserId, "Test Habit", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    [Fact]
    public async Task Handle_ValidCommand_LogsHabit()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LogId.Should().NotBeEmpty();
        await _habitLogRepo.Received(1).AddAsync(
            Arg.Is<HabitLog>(l => l.HabitId == habit.Id),
            Arg.Any<CancellationToken>());
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SuccessfulLog_FiresOnboardingHabitLoggedSignal()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        await _handler.Handle(new LogHabitCommand(UserId, habit.Id), CancellationToken.None);

        await _gamificationService.Received(1).ProcessOnboardingChecklistAsync(
            UserId, OnboardingChecklistSignal.HabitLogged, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConcurrencyConflictOnLogCommit_ReloadsHabitAndRetries()
    {
        var habit = CreateTestHabit();
        var reloadedHabit = CreateTestHabit();
        typeof(Habit).GetProperty("Id")!.SetValue(reloadedHabit, habit.Id);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit, reloadedHabit);

        var saveCount = 0;
        _goalCompletionService.SyncDerivedGoalsAsync(
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<DateOnly>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                saveCount++;
                return saveCount == 1
                    ? throw new DbUpdateConcurrencyException("simulated stale linked goal")
                    : Array.Empty<GoalCompletionUpdate>();
            });

        var result = await _handler.Handle(new LogHabitCommand(UserId, habit.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _unitOfWork.Received(1).ResetTracking();
        await _habitRepo.Received(2).FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyLogged_TogglesUnlog()
    {
        var habit = CreateTestHabit();
        habit.Log(Today, advanceDueDate: false);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _habitLogRepo.DidNotReceive().Remove(Arg.Any<HabitLog>());
        habit.Logs.Should().ContainSingle(l => l.Date == Today).Which.IsDeleted.Should().BeTrue();
        await _goalCompletionService.Received(1).SyncDerivedGoalsAsync(
            UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CompletedOneTimeTask_PayGateFailureRejectsWithoutChangingState()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Finished task", null, null, DueDate: Today)).Value;
        habit.Log(Today).IsSuccess.Should().BeTrue();
        var existingLog = habit.Logs.Should().ContainSingle().Which;
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Habit limit reached"));

        var result = await _handler.Handle(
            new LogHabitCommand(UserId, habit.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        habit.IsCompleted.Should().BeTrue();
        habit.Logs.Should().ContainSingle().Which.Should().BeSameAs(existingLog);
        existingLog.IsDeleted.Should().BeFalse();
        await _payGate.Received(1).CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new LogHabitCommand(UserId, Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit not found.");
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsFailure()
    {
        var otherUserId = Guid.NewGuid();
        var habit = CreateTestHabit(otherUserId);
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit does not belong to this user.");
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var cacheKey = $"summary:{UserId}:{Today:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new LogHabitCommand(UserId, habit.Id);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_LogInvalidatesCachedRetrospective_SoNextReadIsFresh()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => new[] { habit });

        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve("week", Today, weekStartDay: 1);
        var cacheKey = RetrospectiveCacheKey.Build(UserId, "week", dateFrom, "en");
        var staleNarrative = new RetrospectiveNarrative("Stale", "", "", "");
        var emptyMetrics = new RetrospectiveMetrics(0, 0, 0, 0, 0, 0, 0, 0, new int[7], [], []);
        _cache.Set(cacheKey, new RetrospectiveResponse("week", emptyMetrics, staleNarrative, FromCache: false));

        var payGate = Substitute.For<IPayGateService>();
        var retrospectiveService = Substitute.For<IRetrospectiveService>();
        var freshNarrative = new RetrospectiveNarrative("Fresh", "", "", "");
        payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        retrospectiveService.GenerateRetrospectiveAsync(
            Arg.Any<List<Habit>>(),
            dateFrom,
            dateTo,
            "week",
            "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(freshNarrative));
        var queryHandler = new GetRetrospectiveQueryHandler(
            _habitRepo,
            payGate,
            retrospectiveService,
            _userStreakService,
            _userDateService,
            _cache);

        await _handler.Handle(new LogHabitCommand(UserId, habit.Id), CancellationToken.None);
        var result = await queryHandler.Handle(
            new GetRetrospectiveQuery(UserId, dateFrom, dateTo, "week", "en"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FromCache.Should().BeFalse();
        result.Value.Narrative.Should().Be(freshNarrative);
    }

    [Fact]
    public async Task Handle_FutureDateOnRecurring_ReturnsFailure()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var futureDate = Today.AddDays(5);
        var command = new LogHabitCommand(UserId, habit.Id, Date: futureDate);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Cannot log a future date.");
    }

    [Fact]
    public async Task Handle_DateBeyondOverdueWindow_ReturnsFailure()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var oldDate = Today.AddDays(-8);
        var command = new LogHabitCommand(UserId, habit.Id, Date: oldDate);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Cannot log a date beyond the overdue window.");
    }

    [Fact]
    public async Task Handle_EveryTwoYearsDueMoreThanOneYearAgo_LogsOverdueOccurrence()
    {
        var dueDate = new DateOnly(2025, 1, 6);
        var today = new DateOnly(2026, 7, 1);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Biennial", FrequencyUnit.Year, 2, DueDate: dueDate)).Value;
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var result = await _handler.Handle(
            new LogHabitCommand(UserId, habit.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(
            Arg.Is<HabitLog>(log => log.Date == today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotScheduledOnDate_ReturnsFailure()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 2, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var offDay = Today.AddDays(-1);
        var command = new LogHabitCommand(UserId, habit.Id, Date: offDay);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit is not scheduled on this date.");
    }

    [Fact]
    public async Task Handle_IntervalChangedAfterAdvancement_RejectsInactiveWeekThenAllowsMissedActiveDay()
    {
        var anchor = new DateOnly(2025, 1, 6);
        var inactiveDueDate = anchor.AddDays(7);
        var inactiveToday = inactiveDueDate.AddDays(1);
        var missedActiveDay = anchor.AddDays(14);
        var catchUpDay = missedActiveDay.AddDays(1);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Alternating", FrequencyUnit.Day, 1, DueDate: anchor,
            Days: [DayOfWeek.Monday])).Value;
        habit.AdvanceDueDate(anchor.AddDays(6), weekStartDay: 1);
        typeof(Habit).GetProperty(nameof(Habit.IntervalWeeks))!.SetValue(habit, 2);
        habit.DueDate.Should().Be(inactiveDueDate);

        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(inactiveToday, catchUpDay);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var inactiveResult = await _handler.Handle(
            new LogHabitCommand(UserId, habit.Id), CancellationToken.None);
        var activeResult = await _handler.Handle(
            new LogHabitCommand(UserId, habit.Id), CancellationToken.None);

        inactiveResult.IsFailure.Should().BeTrue();
        inactiveResult.ErrorCode.Should().Be(ErrorCodes.NotScheduledOnDate);
        activeResult.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_CadenceEditWithEveryActiveOccurrenceResolved_RejectsOffScheduleToday()
    {
        var anchor = new DateOnly(2025, 1, 6);
        var inactiveDueDate = anchor.AddDays(7);
        var resolvedActiveDay = anchor.AddDays(14);
        var offScheduleToday = resolvedActiveDay.AddDays(1);
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Alternating",
            FrequencyUnit.Day,
            1,
            DueDate: anchor,
            Days: [DayOfWeek.Monday])).Value;
        habit.AdvanceDueDate(anchor.AddDays(6), weekStartDay: 1);
        typeof(Habit).GetProperty(nameof(Habit.IntervalWeeks))!.SetValue(habit, 2);
        habit.DueDate.Should().Be(inactiveDueDate);
        habit.Log(resolvedActiveDay, advanceDueDate: false).IsSuccess.Should().BeTrue();

        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(offScheduleToday);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var result = await _handler.Handle(
            new LogHabitCommand(UserId, habit.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotScheduledOnDate);
        await _habitLogRepo.DidNotReceive().AddAsync(
            Arg.Any<HabitLog>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ResolvedMultiYearDueDateOutsideLoadedWindow_RejectsOffScheduleToday()
    {
        var oldDueDate = Today.AddYears(-2);
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Renew passport",
            FrequencyUnit.Year,
            3,
            oldDueDate)).Value;
        var resolvedDueDate = HabitLog.Create(habit.Id, oldDueDate, 1);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habit);
        _habitLogRepo.FindAsync(
                Arg.Any<Expression<Func<HabitLog, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns([resolvedDueDate]);

        var result = await _handler.Handle(
            new LogHabitCommand(UserId, habit.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotScheduledOnDate);
        await _habitLogRepo.DidNotReceive().AddAsync(
            Arg.Any<HabitLog>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GamificationProcessing_CalledOnLog()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);

        await _handler.Handle(command, CancellationToken.None);

        await _gamificationService.Received(1).ProcessHabitLogged(
            UserId, habit.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GamificationFailure_DoesNotBreakLog()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _gamificationService.ProcessHabitLogged(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Gamification down"));

        var command = new LogHabitCommand(UserId, habit.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithExplicitDate_LogsOnThatDate()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id, Date: Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(
            Arg.Is<HabitLog>(l => l.Date == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OneTimeTask_FutureDateAllowed()
    {
        var futureDate = Today.AddDays(3);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One-time task", null, null, DueDate: futureDate)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id, Date: futureDate);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_FlexibleHabit_DoesNotToggleOnDuplicateDate()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Flexible", FrequencyUnit.Week, 3, DueDate: Today, IsFlexible: true)).Value;
        habit.Log(Today);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
        _habitLogRepo.DidNotReceive().Remove(Arg.Any<HabitLog>());
    }

    [Fact]
    public async Task Handle_BadHabit_DoesNotToggleOnDuplicateDate()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Bad Habit", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: Today)).Value;
        habit.Log(Today, advanceDueDate: false);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_LogCommand_DoesNotPersistNotes()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(
            Arg.Is<HabitLog>(l => l.Note == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserUpdatesStreak()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new LogHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Handle_ConcurrentDuplicateCompletion_ReturnsAlreadyLoggedWithoutDoubleCounting()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _goalCompletionService.SyncDerivedGoalsAsync(
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<DateOnly>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("duplicate", new FakeUniqueViolationException()));

        var winningLog = HabitLog.Create(habit.Id, Today, 1);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new[] { winningLog });

        var streakUser = User.Create("Streak", "streak@test.com").Value;
        streakUser.SetStreakState(4, 9, Today);
        _userRepo.FindAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new[] { streakUser });

        var command = new LogHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LogId.Should().Be(winningLog.Id);
        result.Value.IsFirstCompletionToday.Should().BeFalse();
        result.Value.CurrentStreak.Should().Be(4);
        result.Value.XpEarned.Should().BeNull();
        result.Value.NewAchievementIds.Should().BeNull();
        await _gamificationService.DidNotReceive().ProcessHabitLogged(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _userStreakService.DidNotReceive().RecalculateAsync(
            Arg.Any<Guid>(), cancellationToken: Arg.Any<CancellationToken>());
    }

    private sealed class FakeUniqueViolationException : DbException
    {
        public override string SqlState => "23505";
    }
}
