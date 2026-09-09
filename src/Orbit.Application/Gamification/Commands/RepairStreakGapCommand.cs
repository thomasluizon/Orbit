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

public record RepairStreakGapCommand(Guid UserId, IReadOnlyCollection<DateOnly> Dates)
    : IRequest<Result<StreakInfoResponse>>, IConcurrencyRetryable;

public class RepairStreakGapCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService,
    IUserStreakService userStreakService,
    IFeatureFlagService featureFlagService,
    IUnitOfWork unitOfWork,
    ISender sender,
    ILogger<RepairStreakGapCommandHandler> logger) : IRequestHandler<RepairStreakGapCommand, Result<StreakInfoResponse>>
{
    public async Task<Result<StreakInfoResponse>> Handle(
        RepairStreakGapCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            candidate => candidate.Id == request.UserId, cancellationToken: cancellationToken);
        if (user is null)
            return Result.Failure<StreakInfoResponse>(ErrorMessages.UserNotFound);

        var flags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
        if (!user.HasProAccess && !flags.Contains(FeatureFlagKeys.GamificationFreeTier))
            return Result.PayGateFailure<StreakInfoResponse>("Streak insights are a Pro feature. Upgrade to unlock!");

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var gap = StreakFreeze.CreateGap(request.UserId, request.Dates, today);
        if (gap.IsFailure)
            return gap.PropagateError<StreakInfoResponse>();
        if (user.StreakFreezesAccumulated < gap.Value.Count)
            return Result.Failure<StreakInfoResponse>(DomainErrors.InsufficientStreakFreezes);

        var state = await userStreakService.EvaluateGapRepairAsync(
            request.UserId, today, request.Dates, cancellationToken);
        if (state is null)
            return Result.Failure<StreakInfoResponse>(ErrorMessages.StreakGapRepairUnavailable);

        var spent = user.ConsumeStreakFreezes(gap.Value.Count);
        if (spent.IsFailure)
            return spent.PropagateError<StreakInfoResponse>();

        /**
         * The SCHEDULED predecessor the service validated the gap against, never the previous calendar
         * day. On a sparse cadence those differ, the cursor match then failed, and the derived-cursor
         * fallback marked a newly crossed milestone as awarded without granting its freeze.
         */
        user.RestoreStreakAfterGapRepair(state.CurrentStreak, state.LongestStreak, state.LastActiveDate,
            state.PrecedingScheduledDate ?? gap.Value[0].UsedOnDate.AddDays(-1),
            state.PreGapStreak);
        /**
         * Restoring the cursor only makes a newly crossed milestone ELIGIBLE. Nothing else in this path
         * grants it: the response comes from GetStreakInfoQuery, which calls CalculateAsync rather than
         * RecalculateAsync, so an award earned by the repaired run would sit pending and be lost the
         * next time the streak reset. The repair grants it here, in the same save that spends the bank.
         */
        user.AwardStreakFreezeIfEligible(
            AppConstants.MaxStreakFreezesAccumulated,
            AppConstants.StreakDaysPerFreeze);
        foreach (var freeze in gap.Value)
            await streakFreezeRepository.AddAsync(freeze, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            unitOfWork.ResetTracking();
            if (DbUniqueViolation.IsUniqueViolation(exception))
                return Result.Failure<StreakInfoResponse>(ErrorMessages.StreakGapRepairUnavailable);
            throw;
        }

        logger.LogInformation("Streak gap repaired for {UserId} using {FreezeCount} freezes", request.UserId, gap.Value.Count);
        return await sender.Send(new GetStreakInfoQuery(request.UserId), cancellationToken);
    }
}
