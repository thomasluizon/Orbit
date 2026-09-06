using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public class StreakGapControllerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_ReturnsDistinctConflictCodes(bool insufficient)
    {
        var sender = Substitute.For<ISender>();
        var userId = Guid.NewGuid();
        var dates = new[] { new DateOnly(2026, 9, 4), new DateOnly(2026, 9, 5) };
        var error = insufficient ? DomainErrors.InsufficientStreakFreezes : ErrorMessages.StreakGapRepairUnavailable;
        sender.Send(Arg.Any<RepairStreakGapCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakInfoResponse>(error));
        var controller = new StreakGapController(sender)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"))
                }
            }
        };

        var result = await controller.RepairGap(new(dates), CancellationToken.None);

        var response = result.Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        response.Value.Should().BeEquivalentTo(new { error = error.Message, errorCode = error.Code });
        await sender.Received(1).Send(Arg.Is<RepairStreakGapCommand>(command => command.UserId == userId && command.Dates.SequenceEqual(dates)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Endpoint_IsAuthorizedAndRateLimited()
    {
        typeof(StreakGapController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        typeof(StreakGapController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/gamification/streak");
        var action = typeof(StreakGapController).GetMethod(nameof(StreakGapController.RepairGap))!;
        action.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("repair-gap");
        action.GetCustomAttribute<DistributedRateLimitAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void Request_DatesDeserializeAndUnknownFieldsAreRejected()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<RepairStreakGapRequest>("{\"dates\":[\"2026-09-04\",\"2026-09-05\"]}", options);
        request!.Dates.Should().Equal(new DateOnly(2026, 9, 4), new DateOnly(2026, 9, 5));
        var deserialize = () => JsonSerializer.Deserialize<RepairStreakGapRequest>("{\"dates\":[],\"userId\":\"other\"}", options);
        deserialize.Should().Throw<JsonException>();
    }
}
