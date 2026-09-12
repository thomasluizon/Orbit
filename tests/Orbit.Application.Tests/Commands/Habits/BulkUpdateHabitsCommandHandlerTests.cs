using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Habits;

public sealed class BulkUpdateHabitsCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 9, 11);
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly BulkUpdateHabitsCommandHandler _handler;

    public BulkUpdateHabitsCommandHandlerTests()
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<int>>>>(0)(call.ArgAt<CancellationToken>(1)));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);
        _handler = new BulkUpdateHabitsCommandHandler(
            _habitRepository,
            _userDateService,
            _payGate,
            _unitOfWork,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<BulkUpdateHabitsCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_AllFilter_UpdatesEveryMatchBeyondPaginationCapInChunks()
    {
        var habits = Enumerable.Range(1, 250).Select(index => CreateHabit($"Habit {index}")).ToArray();
        SetupHabits(habits);
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasDescription: true, Description: "Updated"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new BulkHabitMutationResult(250, 250, 0, false));
        habits.Should().OnlyContain(habit => habit.Description == "Updated");
        await _unitOfWork.Received(3).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TagFilter_UpdatesEveryTaggedMatchBeyondPaginationCap()
    {
        var work = Tag.Create(UserId, "Work", "#123456").Value;
        var tagged = Enumerable.Range(1, 205).Select(index => CreateHabit($"Work {index}")).ToArray();
        foreach (var habit in tagged)
            habit.AddTag(work);
        var untagged = Enumerable.Range(1, 20).Select(index => CreateHabit($"Other {index}")).ToArray();
        SetupHabits(tagged.Concat(untagged).ToArray());
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(false, [], Tag: "work"),
            new BulkHabitChanges(HasDueDate: true, DueDate: Today.AddDays(1)));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Value.Should().Be(new BulkHabitMutationResult(205, 205, 0, false));
        tagged.Should().OnlyContain(habit => habit.DueDate == Today.AddDays(1));
        untagged.Should().OnlyContain(habit => habit.DueDate == Today);
    }

    [Fact]
    public async Task Handle_UnrelatedChange_PreservesDueEndTime()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Timed habit",
            FrequencyUnit.Day,
            1,
            Today,
            DueTime: new TimeOnly(9, 0),
            DueEndTime: new TimeOnly(10, 0))).Value;
        SetupHabits(habit);
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasDescription: true, Description: "Updated"));

        await _handler.Handle(command, CancellationToken.None);

        habit.DueEndTime.Should().Be(new TimeOnly(10, 0));
    }

    [Fact]
    public async Task Handle_WhenSecondChunkCannotCommit_StopsAndReportsPartialCounts()
    {
        var habits = Enumerable.Range(1, 250).Select(index => CreateHabit($"Habit {index}")).ToArray();
        SetupHabits(habits);
        var saveCount = 0;
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            saveCount++;
            return saveCount == 2
                ? Task.FromException<int>(new InvalidOperationException("write failed"))
                : Task.FromResult(1);
        });
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasDescription: true, Description: "Updated"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Value.Should().Be(new BulkHabitMutationResult(100, 250, 150, true));
        _unitOfWork.Received(1).DiscardChanges();
    }

    [Fact]
    public async Task Handle_WhenTransactionRetries_ReloadsTrackedHabitsAndCountsCommittedAttemptOnly()
    {
        var selected = CreateHabit("Selected");
        var failedAttempt = CreateHabit("Failed attempt");
        var committedAttempt = CreateHabit("Committed attempt");
        var idProperty = typeof(Habit).GetProperty(nameof(Habit.Id))!;
        idProperty.SetValue(failedAttempt, selected.Id);
        idProperty.SetValue(committedAttempt, selected.Id);
        _habitRepository.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns([selected]);
        var loadCount = 0;
        _habitRepository.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ++loadCount switch
            {
                1 => [failedAttempt],
                _ => [committedAttempt]
            });
        var attemptCount = 0;
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task<Result<int>>>>(0);
                var token = call.ArgAt<CancellationToken>(1);
                try
                {
                    attemptCount++;
                    return await operation(token);
                }
                catch (InvalidOperationException) when (attemptCount == 1)
                {
                    return await operation(token);
                }
            });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("transient")), Task.FromResult(1));
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasDescription: true, Description: "Updated"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Value.Should().Be(new BulkHabitMutationResult(1, 1, 0, false));
        committedAttempt.Description.Should().Be("Updated");
        loadCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_CompletedFilter_UpdatesOnlyCompletedHabits()
    {
        var active = CreateHabit("Active");
        var completed = Habit.Create(new HabitCreateParams(UserId, "Completed", null, null, Today)).Value;
        completed.Log(Today);
        SetupHabits(active, completed);
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(false, [], IsCompleted: true),
            new BulkHabitChanges(HasDescription: true, Description: "Archived"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Value.Should().Be(new BulkHabitMutationResult(1, 1, 0, false));
        completed.Description.Should().Be("Archived");
        active.Description.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ReactivatingCompletedRoots_ChecksWholeChunkAllowanceUnderLock()
    {
        var first = CreateCompletedRecurringHabit("First");
        var second = CreateCompletedRecurringHabit("Second");
        SetupHabits(first, second);
        _payGate.CanCreateHabits(UserId, 2, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit limit reached."));
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(false, [], IsCompleted: true),
            new BulkHabitChanges(HasEndDate: true, EndDate: null));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit limit reached.");
        first.IsCompleted.Should().BeTrue();
        second.IsCompleted.Should().BeTrue();
        await _unitOfWork.Received(1).AcquireAdvisoryLockAsync(
            HabitCeilingLock.ForUser(UserId),
            Arg.Any<CancellationToken>());
        await _payGate.Received(1).CanCreateHabits(UserId, 2, Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AddingDueTime_MigratesScheduledRemindersToOffsets()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Appointment",
            FrequencyUnit.Day,
            1,
            Today,
            ScheduledReminders:
            [
                new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8, 0))
            ])).Value;
        SetupHabits(habit);
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasDueTime: true, DueTime: new TimeOnly(9, 0)));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.DueTime.Should().Be(new TimeOnly(9, 0));
        habit.ReminderTimes.Should().ContainSingle().Which.Should().Be(60);
        habit.ScheduledReminders.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_TimedHabitGivenScheduledReminders_NormalizesToOffsets()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Standup",
            FrequencyUnit.Day,
            1,
            Today,
            DueTime: new TimeOnly(9, 0))).Value;
        SetupHabits(habit);
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(
                HasScheduledReminders: true,
                ScheduledReminders:
                [
                    new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8, 30))
                ]));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.ReminderTimes.Should().ContainSingle().Which.Should().Be(30);
        habit.ScheduledReminders.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_OneTimeHabitGivenRecurringUnitWithoutQuantity_RejectsChunkBeforeMutation()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, Today)).Value;
        SetupHabits(habit);
        var command = new BulkUpdateHabitsCommand(
            UserId,
            new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasFrequencyUnit: true, FrequencyUnit: FrequencyUnit.Day));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        habit.FrequencyUnit.Should().BeNull();
        habit.FrequencyQuantity.Should().BeNull();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private void SetupHabits(params Habit[] habits)
    {
        _habitRepository.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
                return habits.Where(predicate).ToList();
            });
        _habitRepository.FindTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
                return habits.Where(predicate).ToList();
            });
    }

    private static Habit CreateHabit(string title) =>
        Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, Today)).Value;

    private static Habit CreateCompletedRecurringHabit(string title)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            title,
            FrequencyUnit.Day,
            1,
            Today,
            EndDate: Today)).Value;
        habit.Log(Today);
        return habit;
    }
}
