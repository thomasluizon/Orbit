using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Api.Extensions;

public static class ResultActionResultExtensions
{
    private static readonly Dictionary<string, int> StatusByErrorCode = new()
    {
        [Result.PayGateErrorCode] = StatusCodes.Status403Forbidden,
        [ErrorCodes.NoPermission] = StatusCodes.Status403Forbidden,
        [ErrorCodes.HabitNotOwned] = StatusCodes.Status403Forbidden,

        [ErrorCodes.UserNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.HabitNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.ParentHabitNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.TargetParentNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.TagNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.FactNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.GoalNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.ApiKeyNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.NotificationNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.SubscriptionNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.NoActiveSubscription] = StatusCodes.Status404NotFound,
        [ErrorCodes.TemplateNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.SuggestionNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.ClarificationNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.PendingOperationNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.StepUpChallengeNotFound] = StatusCodes.Status404NotFound,

        [ErrorCodes.DuplicateTagName] = StatusCodes.Status409Conflict,
        [ErrorCodes.DuplicateFact] = StatusCodes.Status409Conflict,
        [ErrorCodes.AlreadyReferred] = StatusCodes.Status409Conflict,
        [ErrorCodes.ConcurrentUpdateConflict] = StatusCodes.Status409Conflict,
        [ErrorCodes.HandleTaken] = StatusCodes.Status409Conflict,
        [ErrorCodes.AlreadyFriends] = StatusCodes.Status409Conflict,
        [ErrorCodes.FriendLimitReached] = StatusCodes.Status409Conflict,
        [ErrorCodes.AlreadyPaired] = StatusCodes.Status409Conflict,
        [ErrorCodes.PairLimitReached] = StatusCodes.Status409Conflict,
        [ErrorCodes.AlreadyCheckedIn] = StatusCodes.Status409Conflict,
        [ErrorCodes.StreakRepairUnavailable] = StatusCodes.Status409Conflict,
        [ErrorCodes.StreakGapRepairUnavailable] = StatusCodes.Status409Conflict,
        [DomainErrors.InsufficientStreakFreezes.Code] = StatusCodes.Status409Conflict,

        [ErrorCodes.SocialDisabled] = StatusCodes.Status403Forbidden,
        [ErrorCodes.Blocked] = StatusCodes.Status403Forbidden,
        [ErrorCodes.NotChallengeParticipant] = StatusCodes.Status403Forbidden,
        [ErrorCodes.ApiKeyCreationChallengeRequired] = StatusCodes.Status428PreconditionRequired,

        [ErrorCodes.FriendRequestNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.ChallengeNotFound] = StatusCodes.Status404NotFound,
        [ErrorCodes.InvalidJoinCode] = StatusCodes.Status404NotFound,
        [ErrorCodes.PairNotFound] = StatusCodes.Status404NotFound,

        [ErrorCodes.ChallengeFull] = StatusCodes.Status409Conflict,
        [ErrorCodes.AlreadyJoinedChallenge] = StatusCodes.Status409Conflict,
        [ErrorCodes.ChallengeClosed] = StatusCodes.Status409Conflict,

        [ErrorCodes.InternalServerError] = StatusCodes.Status500InternalServerError,
        [ErrorCodes.PaymentServiceUnavailable] = StatusCodes.Status503ServiceUnavailable,
        [ErrorCodes.BillingDetailsUnavailable] = StatusCodes.Status503ServiceUnavailable,
    };

    public static IActionResult ToPayGateAwareResult(
        this Result result,
        Func<IActionResult> onSuccess,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        return result.IsSuccess ? onSuccess() : result.ToErrorResult(failureStatusCode);
    }

    public static IActionResult ToPayGateAwareResult(
        this Result result,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        return result.IsSuccess ? new OkResult() : result.ToErrorResult(failureStatusCode);
    }

    public static IActionResult ToPayGateAwareResult<T>(
        this Result<T> result,
        Func<T, IActionResult> onSuccess,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        return result.IsSuccess ? onSuccess(result.Value) : result.ToErrorResult(failureStatusCode);
    }

    /// <summary>
    /// Serializes a failed result as the uniform error payload. The HTTP status is
    /// resolved from the error code via the authoritative map (404 for not-found,
    /// 403 for forbidden/pay-gate, 409 for conflicts, 500 for server faults); codes
    /// without an intrinsic status fall back to <paramref name="failureStatusCode"/>.
    /// Every failure carries both the English fallback message and its stable errorCode.
    /// </summary>
    public static IActionResult ToErrorResult(
        this Result result,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        return new ObjectResult(new { error = result.Error, errorCode = result.ErrorCode })
        {
            StatusCode = result.ResolveErrorStatus(failureStatusCode)
        };
    }

    /// <summary>
    /// The HTTP status a failed result maps to, using the authoritative error-code map.
    /// Codes without an intrinsic status fall back to <paramref name="failureStatusCode"/>.
    /// Use when emitting a failure outside the <see cref="IActionResult"/> path (e.g. an SSE event).
    /// </summary>
    public static int ResolveErrorStatus(
        this Result result,
        int failureStatusCode = StatusCodes.Status400BadRequest) =>
        result.ErrorCode is not null && StatusByErrorCode.TryGetValue(result.ErrorCode, out var status)
            ? status
            : failureStatusCode;

    /// <summary>Uniform error body for controller-authored failures that bypass Result.</summary>
    public static object ToErrorBody(this AppError error) =>
        new { error = error.Message, errorCode = error.Code };
}
