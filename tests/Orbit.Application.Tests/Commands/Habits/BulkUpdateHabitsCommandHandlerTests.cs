using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Habits;

public sealed class BulkUpdateHabitsCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 9, 11);
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly BulkUpdateHabitsCommandHandler _handler;

    public BulkUpdateHabitsCommandHandlerTests()
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);
        _handler = new BulkUpdateHabitsCommandHandler(
            _habitRepository,
            _userDateService,
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

    private void SetupHabits(params Habit[] habits)
    {
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
}
