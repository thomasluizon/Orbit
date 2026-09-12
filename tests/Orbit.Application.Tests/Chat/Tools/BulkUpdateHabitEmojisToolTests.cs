using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkUpdateHabitEmojisToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IHabitEmojiInferenceService _inferenceService = Substitute.For<IHabitEmojiInferenceService>();
    private readonly BulkUpdateHabitEmojisTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public BulkUpdateHabitEmojisToolTests()
    {
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<int>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<int>>>(0)(call.ArgAt<CancellationToken>(1)));
        _inferenceService.InferAsync(
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var inputs = call.ArgAt<IReadOnlyList<HabitEmojiInferenceInput>>(1);
                IReadOnlyDictionary<Guid, string> mappings = inputs.ToDictionary(
                    input => input.HabitId,
                    input => input.Title.Contains("gym", StringComparison.OrdinalIgnoreCase) ? "🏋️"
                        : input.Title.Contains("read", StringComparison.OrdinalIgnoreCase) ? "📚"
                        : "🏃");
                return Result.Success(mappings);
            });
        _tool = new BulkUpdateHabitEmojisTool(
            _habitRepo,
            _inferenceService,
            _unitOfWork,
            NullLogger<BulkUpdateHabitEmojisTool>.Instance);
    }

    [Fact]
    public async Task InferFromTitle_UpdatesAllActiveHabitsWithSensibleEmojis()
    {
        var gym = CreateHabit("Go to the gym");
        var read = CreateHabit("Read a book");
        SetupHabits(gym, read);

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeTrue();
        gym.Emoji.Should().Be("🏋️");
        read.Emoji.Should().Be("📚");
        result.EntityName.Should().Contain("2 of 2");
    }

    [Fact]
    public async Task SpecificEmoji_AppliesToSelectedHabits()
    {
        var gym = CreateHabit("Gym");
        var read = CreateHabit("Read");
        SetupHabits(gym, read);

        var result = await Execute($$$"""{"habit_ids": ["{{{gym.Id}}}"], "emoji": "✅", "infer_from_title": false}""");

        result.Success.Should().BeTrue();
        gym.Emoji.Should().Be("✅");
        read.Emoji.Should().BeNull();
    }

    [Fact]
    public async Task SpecificEmoji_CommitsUpdatedHabitsBeforeReportingSuccess()
    {
        var gym = CreateHabit("Gym");
        SetupHabits(gym);

        var result = await Execute($$$"""{"habit_ids": ["{{{gym.Id}}}"], "emoji": "✅", "infer_from_title": false}""");

        result.Success.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullEmoji_ClearsSelectedHabits()
    {
        var gym = CreateHabit("Gym", emoji: "🏋️");
        SetupHabits(gym);

        var result = await Execute($$$"""{"habit_ids": ["{{{gym.Id}}}"], "emoji": null, "infer_from_title": false}""");

        result.Success.Should().BeTrue();
        gym.Emoji.Should().BeNull();
    }

    [Fact]
    public async Task Default_ExcludesCompletedHabits()
    {
        var active = CreateHabit("Run");
        var completed = Habit.Create(new HabitCreateParams(UserId, "Old task", null, null, DueDate: Today)).Value;
        completed.Log(Today);
        SetupHabits(active, completed);

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeTrue();
        active.Emoji.Should().Be("🏃");
        completed.Emoji.Should().BeNull();
    }

    [Fact]
    public async Task NoMatchingHabits_ReturnsError()
    {
        SetupHabits();

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No matching habits");
    }

    [Fact]
    public async Task EmptyHabitIds_ReturnsErrorWithoutUpdatingAllHabits()
    {
        var gym = CreateHabit("Gym");
        SetupHabits(gym);

        var result = await Execute("""{"habit_ids": [], "infer_from_title": true}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids");
        gym.Emoji.Should().BeNull();
        await _habitRepo.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default);
    }

    [Fact]
    public async Task InvalidHabitIds_ReturnsErrorWithoutUpdatingAllHabits()
    {
        var gym = CreateHabit("Gym");
        SetupHabits(gym);

        var result = await Execute("""{"habit_ids": ["not-a-guid"], "infer_from_title": true}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids");
        gym.Emoji.Should().BeNull();
        await _habitRepo.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default);
    }

    [Fact]
    public async Task InferFromTitle_ChunksRequestsAndAggregatesHonestCounts()
    {
        var habits = Enumerable.Range(1, 55).Select(index => CreateHabit($"Habit {index}")).ToArray();
        SetupHabits(habits);

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeTrue();
        await _inferenceService.Received(3).InferAsync(
            UserId,
            Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(),
            Arg.Any<CancellationToken>());
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(55);
        payload.GetProperty("updated_count").GetInt32().Should().Be(55);
        payload.GetProperty("total_matched").GetInt32().Should().Be(55);
        payload.GetProperty("skipped_count").GetInt32().Should().Be(0);
        payload.GetProperty("partial").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task InferFromTitle_WhenLaterChunkFails_ReportsUntouchedRemainderAsPartial()
    {
        var habits = Enumerable.Range(1, 55).Select(index => CreateHabit($"Habit {index}")).ToArray();
        SetupHabits(habits);
        var callCount = 0;
        _inferenceService.InferAsync(
                UserId,
                Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                callCount++;
                if (callCount == 2)
                    return Result.Failure<IReadOnlyDictionary<Guid, string>>("budget unavailable");
                var inputs = call.ArgAt<IReadOnlyList<HabitEmojiInferenceInput>>(1);
                return Result.Success<IReadOnlyDictionary<Guid, string>>(
                    inputs.ToDictionary(input => input.HabitId, _ => "🎯"));
            });

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeTrue();
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(25);
        payload.GetProperty("total_matched").GetInt32().Should().Be(55);
        payload.GetProperty("skipped_count").GetInt32().Should().Be(30);
        payload.GetProperty("partial").GetBoolean().Should().BeTrue();
        result.EntityName.Should().Contain("Partial result");
    }

    [Fact]
    public async Task InferFromTitle_MalformedModelValue_IsSkippedRatherThanWritten()
    {
        var habit = CreateHabit("Medication");
        SetupHabits(habit);
        _inferenceService.InferAsync(
                UserId,
                Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyDictionary<Guid, string>>(
                new Dictionary<Guid, string> { [habit.Id] = "medicine" }));

        var result = await Execute("""{"infer_from_title": true}""");

        result.Success.Should().BeTrue();
        habit.Emoji.Should().BeNull();
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(0);
        payload.GetProperty("skipped_count").GetInt32().Should().Be(1);
        payload.GetProperty("partial").GetBoolean().Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InferFromTitle_WhenTransactionRetries_ReloadsHabitsAndCountsCommittedAttemptOnly()
    {
        var selected = CreateHabit("Selected");
        var failedAttempt = CreateHabit("Failed attempt");
        var committedAttempt = CreateHabit("Committed attempt");
        var idProperty = typeof(Habit).GetProperty(nameof(Habit.Id))!;
        idProperty.SetValue(failedAttempt, selected.Id);
        idProperty.SetValue(committedAttempt, selected.Id);
        _habitRepo.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns([selected]);
        var loadCount = 0;
        _habitRepo.FindTrackedAsync(
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
                Arg.Any<Func<CancellationToken, Task<int>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var operation = call.ArgAt<Func<CancellationToken, Task<int>>>(0);
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

        var result = await Execute("""{"infer_from_title":true}""");

        result.Success.Should().BeTrue();
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(1);
        committedAttempt.Emoji.Should().Be("🏃");
        loadCount.Should().Be(2);
    }

    private static Habit CreateHabit(string title, string? emoji = null)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today, Emoji: emoji)).Value;
    }

    private void SetupHabits(params Habit[] habits)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList();
        });
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList();
        });
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
