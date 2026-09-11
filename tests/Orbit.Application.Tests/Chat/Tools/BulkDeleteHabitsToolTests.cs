using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkDeleteHabitsToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly BulkDeleteHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public BulkDeleteHabitsToolTests() => _tool = new BulkDeleteHabitsTool(_mediator, _habitRepository);

    [Fact]
    public void Metadata_IsExposed()
    {
        _tool.Name.Should().Be("bulk_delete_habits");
        _tool.GetParameterSchema().Should().NotBeNull();
    }

    [Fact]
    public async Task MissingHabitIds_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("filter or habit_ids");
    }

    [Fact]
    public async Task HabitIdsNotArray_ReturnsError()
    {
        var result = await Execute("""{"habit_ids": "x"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids");
    }

    [Fact]
    public async Task EmptyArray_ReturnsNoValidIdsError()
    {
        var result = await Execute("""{"habit_ids": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids");
    }

    [Fact]
    public async Task AllInvalidIds_ReturnsNoValidIdsError()
    {
        var result = await Execute("""{"habit_ids": ["nope", "still-not"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidIds_ReportsSuccessCount()
    {
        var firstHabit = CreateHabit("First");
        var secondHabit = CreateHabit("Second");
        var first = firstHabit.Id;
        var second = secondHabit.Id;
        SetupHabits(firstHabit, secondHabit);
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkDeleteResult(new[]
            {
                new BulkDeleteItemResult(0, BulkItemStatus.Success, first),
                new BulkDeleteItemResult(1, BulkItemStatus.Failed, second, "in use"),
            })));

        var result = await Execute($$"""{"habit_ids": ["{{first}}", "{{second}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Deleted 1 of 2");
        result.EntityName.Should().Contain("Partial result");
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(1);
        payload.GetProperty("total_matched").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkDeleteResult>("Too many habits."));

        var habit = CreateHabit("Habit");
        SetupHabits(habit);
        var result = await Execute($$"""{"habit_ids": ["{{habit.Id}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Too many habits.");
    }

    [Fact]
    public async Task AllFilter_DeletesEveryMatchAcrossBoundedCommands()
    {
        var habits = Enumerable.Range(1, 205).Select(index => CreateHabit($"Habit {index}")).ToArray();
        var chunkSizes = new List<int>();
        SetupHabits(habits);
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<BulkDeleteHabitsCommand>();
                chunkSizes.Add(command.HabitIds.Count);
                return Result.Success(new BulkDeleteResult(command.HabitIds.Select((id, index) =>
                    new BulkDeleteItemResult(index, BulkItemStatus.Success, id)).ToList()));
            });

        var result = await Execute("""{"filter":{"all":true}}""");

        result.EntityName.Should().Contain("Deleted 205 of 205");
        chunkSizes.Should().Equal(AppConstants.MaxBulkOperationSize, AppConstants.MaxBulkOperationSize, 5);
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);

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
        Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, new DateOnly(2026, 9, 11))).Value;
}
