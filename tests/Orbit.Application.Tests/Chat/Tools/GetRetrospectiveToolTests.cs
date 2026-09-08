using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat.Tools;

public class GetRetrospectiveToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetRetrospectiveTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 4);

    public GetRetrospectiveToolTests()
    {
        _tool = new GetRetrospectiveTool(_mediator, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
    }

    [Fact]
    public async Task Success_ReturnsPayload()
    {
        var response = CreateResponse("week", "Last week you kept a 5-day streak.");
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.Payload.Should().Be(response);
    }

    [Fact]
    public async Task PayGateFailure_PropagatesError()
    {
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<RetrospectiveResponse>("Upgrade to yearly Pro to use the retrospective."));

        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("yearly Pro");
    }

    [Fact]
    public async Task MonthPeriod_DerivesThirtyDayRange()
    {
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CreateResponse("month", "ok")));

        await Execute("""{"period": "month"}""");

        await _mediator.Received(1).Send(
            Arg.Is<GetRetrospectiveQuery>(q =>
                q.UserId == UserId &&
                q.Period == "month" &&
                q.DateTo.DayNumber - q.DateFrom.DayNumber + 1 == 30 &&
                q.DateTo == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoPeriod_DefaultsToWeek_AnchoredOnMondayStart()
    {
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CreateResponse("week", "ok")));

        await Execute("{}");

        await _mediator.Received(1).Send(
            Arg.Is<GetRetrospectiveQuery>(q =>
                q.Period == "week" &&
                q.DateFrom == new DateOnly(2026, 6, 1) &&
                q.DateTo == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WeekPeriod_SundayStartUser_AnchorsOnSunday()
    {
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(0);
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CreateResponse("week", "ok")));

        await Execute("""{"period": "week"}""");

        await _mediator.Received(1).Send(
            Arg.Is<GetRetrospectiveQuery>(q =>
                q.Period == "week" &&
                q.DateFrom == new DateOnly(2026, 5, 31) &&
                q.DateTo == Today),
            Arg.Any<CancellationToken>());
    }

    private static RetrospectiveResponse CreateResponse(string period, string highlights) =>
        new(
            period,
            new RetrospectiveMetrics(0, 0, 0, 0, 0, 0, 0, 0, new int[7], [], []),
            new RetrospectiveNarrative(highlights, "", "", ""),
            FromCache: false);

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
