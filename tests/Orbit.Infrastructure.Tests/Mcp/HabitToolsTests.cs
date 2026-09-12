using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class HabitToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly HabitTools _tools;
    private readonly ClaimsPrincipal _user;

    public HabitToolsTests()
    {
        _tools = new HabitTools(_mediator, _userDateService, new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static RetrospectiveMetrics EmptyMetrics() =>
        new(0, 0, 0, 0, 0, 0, 0, 0, new int[7], [], []);

    private void StubExecutor(AgentOperationStatus status, string? targetId = null, string? targetName = null,
        string? policyReason = null, object? payload = null, Guid? pendingOperationId = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "operation",
            "operation",
            AgentRiskClass.Low,
            AgentConfirmationRequirement.None,
            status,
            Summary: "summary",
            TargetId: targetId,
            TargetName: targetName,
            PolicyReason: policyReason,
            PendingOperationId: pendingOperationId,
            Payload: payload));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private async Task<AgentExecuteOperationRequest> CapturedRequestAsync(Func<Task> act)
    {
        await act();
        var calls = _executor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .ToList();
        calls.Should().NotBeEmpty();
        return (AgentExecuteOperationRequest)calls[^1].GetArguments()[0]!;
    }

    [Fact]
    public async Task ListHabits_Success_ReturnsFormattedList()
    {
        var items = new List<HabitScheduleItem>
        {
            new(Guid.NewGuid(), "Read", null, FrequencyUnit.Day, 1, false, false, false, false,
                [], null, DateTime.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow), null, null, null,
                [], false, false, [], [], false, [], [], [], [], false, null, null, false, [])
        };
        var paginated = new PaginatedResponse<HabitScheduleItem>(items, 1, 50, 1, 1);
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(paginated));

        var result = await _tools.ListHabits(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("Read");
        result.Should().Contain("page 1/1");
    }

    [Fact]
    public async Task ListHabits_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PaginatedResponse<HabitScheduleItem>>("Date range invalid"));

        var result = await _tools.ListHabits(_user, "2026-04-01", "2026-04-07");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ListHabits_Empty_ReturnsNoHabitsMessage()
    {
        var paginated = new PaginatedResponse<HabitScheduleItem>([], 1, 50, 0, 0);
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(paginated));

        var result = await _tools.ListHabits(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("No habits found");
    }

    [Fact]
    public async Task GetHabit_Success_ReturnsDetail()
    {
        var habitId = Guid.NewGuid();
        var detail = new HabitDetailResponse(
            habitId, "Exercise", "Go for a run", FrequencyUnit.Day, 1,
            false, false, false, false,
            DateOnly.FromDateTime(DateTime.UtcNow), null, null, null,
            [], null, false, [], [], [], DateTime.UtcNow, []);

        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(detail));

        var result = await _tools.GetHabit(_user, habitId.ToString());

        result.Should().Contain("Exercise");
        result.Should().Contain("Active");
    }

    [Fact]
    public async Task GetHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitDetailResponse>("Habit not found"));

        var result = await _tools.GetHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateHabit_Success_RoutesThroughExecutorAndReturnsCreatedMessage()
    {
        var newId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: newId.ToString(), targetName: "New Habit");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.CreateHabit(
            _user,
            "New Habit",
            "2026-04-01",
            intervalWeeks: 2));

        request.OperationId.Should().Be("create_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        request.Arguments.GetProperty("interval_weeks").GetInt32().Should().Be(2);
        result.Should().Contain("Created habit 'New Habit'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Title is required");

        var result = await _tools.CreateHabit(_user, "", "2026-04-01");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateHabit_ReadOnlyCredentialDenied_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Denied, policyReason: "read_only_credential");

        var result = await _tools.CreateHabit(_user, "New Habit", "2026-04-01");

        result.Should().StartWith("Error: ");
        result.Should().Contain("read_only_credential");
    }

    [Fact]
    public async Task UpdateHabit_Success_RoutesThroughExecutor()
    {
        var habitId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.UpdateHabit(
            _user,
            habitId.ToString(),
            "Updated Title",
            intervalWeeks: 3));

        request.OperationId.Should().Be("update_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        request.Arguments.GetProperty("interval_weeks").GetInt32().Should().Be(3);
        result.Should().Contain("Updated habit");
    }

    [Fact]
    public async Task UpdateHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.UpdateHabit(_user, Guid.NewGuid().ToString(), "Title");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DeleteHabit_Success_RoutesThroughExecutor()
    {
        var habitId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.DeleteHabit(_user, habitId.ToString()));

        request.OperationId.Should().Be("delete_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        result.Should().Contain("Deleted habit");
    }

    [Fact]
    public async Task DeleteHabit_PendingConfirmation_ReturnsConfirmationPrompt()
    {
        StubExecutor(AgentOperationStatus.PendingConfirmation, pendingOperationId: Guid.NewGuid());

        var result = await _tools.DeleteHabit(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Confirmation required");
        result.Should().Contain("confirm_agent_operation_v2");
    }

    [Fact]
    public async Task DeleteHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.DeleteHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task LogHabit_Success_RoutesThroughExecutor()
    {
        var habitId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.LogHabit(_user, habitId.ToString()));

        request.OperationId.Should().Be("log_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        result.Should().Contain("Logged habit");
    }

    [Fact]
    public async Task LogHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.LogHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SkipHabit_Success_RoutesThroughExecutor()
    {
        var habitId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.SkipHabit(_user, habitId.ToString()));

        request.OperationId.Should().Be("skip_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        result.Should().Contain("Skipped habit");
    }

    [Fact]
    public async Task SkipHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Not a recurring habit");

        var result = await _tools.SkipHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetHabitMetrics_Success_ReturnsFormattedMetrics()
    {
        var metrics = new HabitMetrics(5, 10, 0.85m, 0.75m, 50, DateOnly.FromDateTime(DateTime.UtcNow));
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(metrics));

        var result = await _tools.GetHabitMetrics(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Current Streak: 5 days");
        result.Should().Contain("Longest Streak: 10 days");
        result.Should().Contain("Total Completions: 50");
    }

    [Fact]
    public async Task GetHabitMetrics_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitMetrics>("Habit not found"));

        var result = await _tools.GetHabitMetrics(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DuplicateHabit_Success_RoutesThroughExecutor()
    {
        var newId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: newId.ToString());

        var habitId = Guid.NewGuid();
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.DuplicateHabit(_user, habitId.ToString()));

        request.OperationId.Should().Be("duplicate_habit");
        result.Should().Contain("Duplicated habit");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task DuplicateHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.DuplicateHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task MoveHabitParent_Success_WithParent_ReturnsMovedMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var habitId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.MoveHabitParent(_user, habitId.ToString(), parentId.ToString()));

        request.OperationId.Should().Be("move_habit_parent");
        result.Should().Contain("Moved habit");
        result.Should().Contain("under parent");
    }

    [Fact]
    public async Task MoveHabitParent_Success_NoParent_ReturnsPromotedMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var result = await _tools.MoveHabitParent(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Promoted habit");
        result.Should().Contain("top-level");
    }

    [Fact]
    public async Task MoveHabitParent_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.MoveHabitParent(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateSubHabit_Success_RoutesThroughExecutor()
    {
        var newId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: newId.ToString(), targetName: "Sub Habit");

        var parentId = Guid.NewGuid();
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.CreateSubHabit(_user, parentId.ToString(), "Sub Habit"));

        request.OperationId.Should().Be("create_sub_habit");
        result.Should().Contain("Created sub-habit 'Sub Habit'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateSubHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Parent not found");

        var result = await _tools.CreateSubHabit(_user, Guid.NewGuid().ToString(), "Sub");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ReorderHabits_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var json = $"[{{\"habitId\":\"{Guid.NewGuid()}\",\"position\":0}}]";
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.ReorderHabits(_user, json));

        request.OperationId.Should().Be("reorder_habits");
        result.Should().Contain("Reordered 1 habits");
    }

    [Fact]
    public async Task ReorderHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Invalid positions");

        var json = "[]";
        var result = await _tools.ReorderHabits(_user, json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateChecklist_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var json = "[{\"text\":\"Item 1\",\"isChecked\":true}]";
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.UpdateChecklist(_user, Guid.NewGuid().ToString(), json));

        request.OperationId.Should().Be("update_checklist");
        result.Should().Contain("Updated checklist");
        result.Should().Contain("1 items");
    }

    [Fact]
    public async Task UpdateChecklist_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var json = "[]";
        var result = await _tools.UpdateChecklist(_user, Guid.NewGuid().ToString(), json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task LinkGoalsToHabit_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var goalId = Guid.NewGuid();
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.LinkGoalsToHabit(_user, Guid.NewGuid().ToString(), goalId.ToString()));

        request.OperationId.Should().Be("link_goals_to_habit");
        result.Should().Contain("Linked 1 goals");
    }

    [Fact]
    public async Task LinkGoalsToHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Not found");

        var result = await _tools.LinkGoalsToHabit(_user, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkCreateHabits_Success_RoutesThroughExecutorAndFormatsBreakdown()
    {
        var id = Guid.NewGuid();
        var bulkResult = new BulkCreateResult([
            new BulkCreateItemResult(0, BulkItemStatus.Success, id, "Habit1")
        ]);
        StubExecutor(AgentOperationStatus.Succeeded, payload: bulkResult);

        var json = "[{\"title\":\"Habit1\",\"dueDate\":\"2026-04-01\"}]";
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkCreateHabits(_user, json));

        request.OperationId.Should().Be("bulk_create_habits");
        result.Should().Contain("1 succeeded, 0 failed");
    }

    [Fact]
    public async Task BulkCreateHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Limit exceeded");

        var json = "[{\"title\":\"Habit1\",\"dueDate\":\"2026-04-01\"}]";
        var result = await _tools.BulkCreateHabits(_user, json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkDeleteHabits_Success_RoutesThroughExecutor()
    {
        var id = Guid.NewGuid();
        var bulkResult = new BulkDeleteResult([
            new BulkDeleteItemResult(0, BulkItemStatus.Success, id)
        ]);
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Deleted 1 of 1 matching habit(s). Skipped 0. Complete result.", payload: bulkResult);

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkDeleteHabits(_user, id.ToString()));

        request.OperationId.Should().Be("bulk_delete_habits");
        result.Should().Contain("Deleted 1 of 1");
    }

    [Fact]
    public async Task BulkDeleteHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Error");

        var result = await _tools.BulkDeleteHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkUpdateHabits_RoutesFilterAndUpdatesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Updated 250 of 250 matching habit(s). Skipped 0. Complete result.");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkUpdateHabits(
            _user,
            """{"all":true}""",
            """{"description":"Changed"}"""));

        request.OperationId.Should().Be("bulk_update_habits");
        request.Arguments.GetProperty("filter").GetProperty("all").GetBoolean().Should().BeTrue();
        request.Arguments.GetProperty("updates").GetProperty("description").GetString().Should().Be("Changed");
        result.Should().Contain("250 of 250");
    }

    [Fact]
    public async Task BulkRescheduleHabits_RoutesFilterAndDateThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Rescheduled 250 of 250 matching habit(s). Skipped 0. Complete result.");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkRescheduleHabits(
            _user,
            """{"all":true}""",
            "2026-09-12"));

        request.OperationId.Should().Be("bulk_reschedule_habits");
        request.Arguments.GetProperty("filter").GetProperty("all").GetBoolean().Should().BeTrue();
        request.Arguments.GetProperty("due_date").GetString().Should().Be("2026-09-12");
        result.Should().Contain("250 of 250");
    }

    [Fact]
    public async Task BulkLogHabits_Success_RoutesThroughExecutor()
    {
        var id = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Logged 1 of 1 matching habit(s). Skipped 0. Complete result.");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkLogHabits(_user, id.ToString()));

        request.OperationId.Should().Be("bulk_log_habits");
        result.Should().Contain("Logged 1 of 1");
    }

    [Fact]
    public async Task BulkLogHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Error");

        var result = await _tools.BulkLogHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkSkipHabits_Success_RoutesThroughExecutor()
    {
        var id = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Skipped 1 of 1 matching habit(s). Skipped 0. Complete result.");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkSkipHabits(_user, id.ToString()));

        request.OperationId.Should().Be("bulk_skip_habits");
        result.Should().Contain("Skipped 1 of 1");
    }

    [Fact]
    public async Task BulkSkipHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Error");

        var result = await _tools.BulkSkipHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetDailySummary_Success_ReturnsSummary()
    {
        var response = new DailySummaryResponse("You did great today!", "Knock out one more.", false);
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetDailySummary(_user, "2026-04-01", "2026-04-01");

        result.Should().Contain("You did great today!");
        result.Should().Contain("Summary");
    }

    [Fact]
    public async Task GetDailySummary_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DailySummaryResponse>("Pro required"));

        var result = await _tools.GetDailySummary(_user, "2026-04-01", "2026-04-01");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetRetrospective_Success_ReturnsRetrospective()
    {
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 4, 3));

        var response = new RetrospectiveResponse(
            "week",
            EmptyMetrics(),
            new RetrospectiveNarrative("Great week!", "", "", ""),
            FromCache: false);
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetRetrospective(_user, "week");

        result.Should().Contain("Great week!");
        result.Should().Contain("Retrospective (week)");
    }

    [Fact]
    public async Task GetRetrospective_Failure_ReturnsError()
    {
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 4, 3));

        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RetrospectiveResponse>("Pro required"));

        var result = await _tools.GetRetrospective(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetHabitLogs_Success_ReturnsFormattedLogs()
    {
        var logs = new List<HabitLogResponse>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 4, 1), 1, DateTime.UtcNow)
        };
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitLogResponse>>(logs));

        var result = await _tools.GetHabitLogs(_user, Guid.NewGuid().ToString());

        result.Should().Contain("2026-04-01");
    }

    [Fact]
    public async Task GetHabitLogs_Empty_ReturnsNoLogsMessage()
    {
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitLogResponse>>([]));

        var result = await _tools.GetHabitLogs(_user, Guid.NewGuid().ToString());

        result.Should().Contain("No logs found");
    }

    [Fact]
    public async Task GetHabitLogs_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<HabitLogResponse>>("Not found"));

        var result = await _tools.GetHabitLogs(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetAllHabitLogs_Success_ReturnsGroupedLogs()
    {
        var habitId = Guid.NewGuid();
        var dict = new Dictionary<Guid, List<HabitLogResponse>>
        {
            [habitId] = [new HabitLogResponse(Guid.NewGuid(), new DateOnly(2026, 4, 1), 1, DateTime.UtcNow)]
        };
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(dict));

        var result = await _tools.GetAllHabitLogs(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("1 habits");
        result.Should().Contain("partial: false");
    }

    [Fact]
    public async Task GetAllHabitLogs_WhenPerHabitOutputIsCapped_ReportsTruncation()
    {
        var habitId = Guid.NewGuid();
        var logs = Enumerable.Range(1, 12)
            .Select(day => new HabitLogResponse(Guid.NewGuid(), new DateOnly(2026, 4, day), 1, DateTime.UtcNow))
            .ToList();
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new Dictionary<Guid, List<HabitLogResponse>> { [habitId] = logs }));

        var result = await _tools.GetAllHabitLogs(_user, "2026-04-01", "2026-04-12");

        result.Should().Contain("total_logs: 12");
        result.Should().Contain("returned_logs: 10");
        result.Should().Contain("partial: true");
        result.Should().Contain("total 12, returned 10");
    }

    [Fact]
    public async Task GetAllHabitLogs_Empty_ReturnsNoLogsMessage()
    {
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new Dictionary<Guid, List<HabitLogResponse>>()));

        var result = await _tools.GetAllHabitLogs(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("No logs found");
    }
}
