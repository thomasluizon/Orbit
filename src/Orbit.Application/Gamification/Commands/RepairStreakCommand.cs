using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Commands;

public record RepairStreakCommand(Guid UserId)
    : IRequest<Result<StreakInfoResponse>>, IConcurrencyRetryable;

public class RepairStreakCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService,
    IUserStreakService userStreakService,
    IFeatureFlagService featureFlagService,
    IUnitOfWork unitOfWork,
    ISender sender,
    IProductAnalytics productAnalytics,
    ILogger<RepairStreakCommandHandler> logger)
    : IRequestHandler<RepairStreakCommand, Result<StreakInfoResponse>>
{
    public async Task<Result<StreakInfoResponse>> Handle(
        RepairStreakCommand request,
        CancellationToken cancellationToken)
    {
        // This repair reads the schedule and the freezes, then spends a banked freeze against what it
        // read. Eligibility and spending share ONE consistency boundary, and it is the boundary every
        // habit writer holds. See HabitCeilingLock.
        Result outcome;
        try
        {
            outcome = await HabitCeilingLock.ExecuteAsync(
                unitOfWork,
                request.UserId,
                transactionToken => RepairYesterdayAsync(request, transactionToken),
                cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            // The freeze row for this date already exists, so the repair is already done. The catch
            // sits OUTSIDE the transaction because a failed statement aborts the whole block: the
            // reply query below cannot run until that block has rolled back.
            unitOfWork.ResetTracking();
            return await sender.Send(new GetStreakInfoQuery(request.UserId), cancellationToken);
        }

        if (outcome.IsFailure)
            return outcome.PropagateError<StreakInfoResponse>();

        return await sender.Send(new GetStreakInfoQuery(request.UserId), cancellationToken);
    }

    private async Task<Result> RepairYesterdayAsync(
        RepairStreakCommand request,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            candidate => candidate.Id == request.UserId,
            cancellationToken: cancellationToken);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(
            request.UserId,
            cancellationToken);
        var unlocked = user.HasProAccess || enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier);
        if (!unlocked)
            return Result.PayGateFailure("Streak insights are a Pro feature. Upgrade to unlock!");

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var missedDate = today.AddDays(-1);
        var alreadyRepaired = await streakFreezeRepository.AnyAsync(
            freeze => freeze.UserId == request.UserId && freeze.UsedOnDate == missedDate,
            cancellationToken);
        if (alreadyRepaired)
            return Result.Success();

        var repair = await userStreakService.EvaluateRepairAsync(
            request.UserId,
            today,
            missedDate,
            cancellationToken);
        if (repair is not { IsAvailable: true, RepairedState: not null }
            || repair.MissedDate != missedDate)
        {
            return Result.Failure(ErrorMessages.StreakRepairUnavailable);
        }

        var consumeResult = user.ConsumeStreakFreeze();
        if (consumeResult.IsFailure)
            return Result.Failure(ErrorMessages.StreakRepairUnavailable);

        user.SetStreakState(
            repair.RepairedState.CurrentStreak,
            repair.RepairedState.LongestStreak,
            repair.RepairedState.LastActiveDate);
        await streakFreezeRepository.AddAsync(
            StreakFreeze.Create(request.UserId, missedDate),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        AnalyticsCapture.SafeCaptureUserEvent(
            productAnalytics,
            logger,
            user,
            "streak_repair_spent",
            new Dictionary<string, object>
            {
                ["missed_date"] = missedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["remaining_bank"] = user.StreakFreezesAccumulated
            });

        return Result.Success();
    }
}
