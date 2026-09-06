using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Api.Controllers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepairStreakGapRequest(IReadOnlyCollection<DateOnly> Dates);

[Authorize]
[ApiController]
[Route("api/gamification/streak")]
public class StreakGapController(ISender sender) : ControllerBase
{
    [HttpPost("repair-gap")]
    [DistributedRateLimit("streak-repair")]
    [ProducesResponseType(typeof(StreakInfoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RepairGap(
        [FromBody] RepairStreakGapRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new RepairStreakGapCommand(HttpContext.GetUserId(), request.Dates), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }
}
