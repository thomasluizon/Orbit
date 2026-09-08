using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public class GamificationControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GamificationController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public GamificationControllerTests()
    {
        _controller = new GamificationController(_mediator, _userDateService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task GetProfile_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(GamificationProfileResponse)!));

        var result = await _controller.GetProfile(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetProfile_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<GamificationProfileResponse>("Pro required"));

        var result = await _controller.GetProfile(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetAchievements_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(AchievementsResponse)!));

        var result = await _controller.GetAchievements(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAchievements_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<AchievementsResponse>("Pro required"));

        var result = await _controller.GetAchievements(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetStreakInfo_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(StreakInfoResponse)!));

        var result = await _controller.GetStreakInfo(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetStreakInfo_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakInfoResponse>("Error"));

        var result = await _controller.GetStreakInfo(CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetStreakInfo_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<StreakInfoResponse>("Pro required"));

        var result = await _controller.GetStreakInfo(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task RepairStreak_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<RepairStreakCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(StreakInfoResponse)!));

        var result = await _controller.RepairStreak(null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RepairStreak_Unavailable_Returns409WithStableCode()
    {
        _mediator.Send(Arg.Any<RepairStreakCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakInfoResponse>(ErrorMessages.StreakRepairUnavailable));

        var result = await _controller.RepairStreak(null, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            error = "No streak repair is available for yesterday.",
            errorCode = ErrorCodes.StreakRepairUnavailable
        });
    }

    [Fact]
    public void RepairStreak_IsTheOnlyPostAndIsRateLimited()
    {
        var postActions = typeof(GamificationController).GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Length > 0)
            .ToList();

        postActions.Should().ContainSingle();
        postActions[0].Name.Should().Be(nameof(GamificationController.RepairStreak));
        postActions[0].GetCustomAttributes(typeof(DistributedRateLimitAttribute), inherit: false)
            .Should().ContainSingle();
    }

    [Fact]
    public void RepairStreakRequest_ClientSuppliedDate_IsRejected()
    {
        var deserialize = () => JsonSerializer.Deserialize<RepairStreakRequest>(
            "{\"date\":\"2026-08-21\"}");

        deserialize.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task GetRecap_Success_ReturnsOk()
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 6, 20));
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(1);
        _mediator.Send(Arg.Any<GetRecapQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(RecapResponse)!));

        var result = await _controller.GetRecap("week", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetRecap_ClosedMonth_SendsCalendarBoundsAndParameters()
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 8, 1));
        _mediator.Send(Arg.Any<GetRecapQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(RecapResponse)!));

        var result = await _controller.GetRecap("month", CancellationToken.None, 2026, 7);

        result.Should().BeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<GetRecapQuery>(query =>
                query.DateFrom == new DateOnly(2026, 7, 1)
                && query.DateTo == new DateOnly(2026, 7, 31)
                && query.ClosedYear == 2026
                && query.ClosedMonth == 7),
            Arg.Any<CancellationToken>());
        await _userDateService.DidNotReceive()
            .GetUserWeekStartDayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRecap_RollingMonth_KeepsThirtyDayWindow()
    {
        var today = new DateOnly(2026, 8, 22);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        _mediator.Send(Arg.Any<GetRecapQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(RecapResponse)!));

        var result = await _controller.GetRecap("month", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<GetRecapQuery>(query =>
                query.DateTo.DayNumber - query.DateFrom.DayNumber + 1 == 30
                && query.DateTo == today
                && query.ClosedYear == null
                && query.ClosedMonth == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRecap_CurrentMonth_ReturnsNamedBadRequest()
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 8, 22));

        var result = await _controller.GetRecap("month", CancellationToken.None, 2026, 8);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().NotBeNull();
        System.Text.Json.JsonSerializer.Serialize(objectResult.Value)
            .Should().Contain(ErrorCodes.RecapMonthNotClosed);
        await _mediator.DidNotReceive().Send(Arg.Any<GetRecapQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("month", 2026, null)]
    [InlineData("month", null, 7)]
    [InlineData("week", 2026, 7)]
    public async Task GetRecap_InvalidClosedMonthShape_ReturnsBadRequest(string period, int? year, int? month)
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 8, 1));

        var result = await _controller.GetRecap(period, CancellationToken.None, year, month);

        result.Should().BeOfType<BadRequestObjectResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<GetRecapQuery>(), Arg.Any<CancellationToken>());
    }
}
