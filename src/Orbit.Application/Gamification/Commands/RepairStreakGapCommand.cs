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
        /**
         * Eligibility and spending share ONE consistency boundary, and it is the boundary every habit
         * writer already holds.
         *
         * EvaluateGapRepairAsync decides from the habits, the completion logs, the freezes and the
         * schedule derived from them; the save below then spends the freeze bank. Those were two
         * separate snapshots, and User.xmin cannot hold them together: a schedule-only habit edit
         * commits WITHOUT touching the user row, so the optimistic token this command retries on never
         * fires. A cadence or due-date change landing between the two left a freeze spent on a date the
         * committed schedule no longer accepts, with the bank charged all the same.
         *
         * HabitCeilingLock is that boundary, and this change extends it to cover every writer rather
         * than only the ceiling-sensitive ones it already held. Create, update, move, restore, log,
         * unlog, delete, skip, their four bulk twins, the yesterday repair, the chat tools' single
         * commit point and the offline-sync delete replay now all persist inside it. A writer only has
         * to hold it across the save, because the lock is transaction scoped and what it serializes is
         * the interval from acquisition to commit.
         *
         * The user is loaded INSIDE the lock for the same reason: a load taken before it would carry a
         * pre-lock snapshot of the bank into a post-lock decision.
         */
        var repaired = await HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            request.UserId,
            async transactionToken =>
            {
                var user = await userRepository.FindOneTrackedAsync(
                    candidate => candidate.Id == request.UserId, cancellationToken: transactionToken);
                if (user is null)
                    return Result.Failure<int>(ErrorMessages.UserNotFound);

                var flags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, transactionToken);
                if (!user.HasProAccess && !flags.Contains(FeatureFlagKeys.GamificationFreeTier))
                    return Result.PayGateFailure<int>("Streak insights are a Pro feature. Upgrade to unlock!");

                var today = await userDateService.GetUserTodayAsync(request.UserId, transactionToken);
                var gap = StreakFreeze.CreateGap(request.UserId, request.Dates, today);
                if (gap.IsFailure)
                    return gap.PropagateError<int>();
                if (user.StreakFreezesAccumulated < gap.Value.Count)
                    return Result.Failure<int>(DomainErrors.InsufficientStreakFreezes);

                var state = await userStreakService.EvaluateGapRepairAsync(
                    request.UserId, today, request.Dates, transactionToken);
                if (state is null)
                    return Result.Failure<int>(ErrorMessages.StreakGapRepairUnavailable);

                var spent = user.ConsumeStreakFreezes(gap.Value.Count);
                if (spent.IsFailure)
                    return spent.PropagateError<int>();

                /**
                 * The SCHEDULED predecessor the service validated the gap against, never the previous
                 * calendar day. On a sparse cadence those differ, the cursor match then failed, and the
                 * derived-cursor fallback marked a newly crossed milestone as awarded without granting
                 * its freeze.
                 */
                user.RestoreStreakAfterGapRepair(state.CurrentStreak, state.LongestStreak, state.LastActiveDate,
                    state.PrecedingScheduledDate ?? gap.Value[0].UsedOnDate.AddDays(-1),
                    state.PreGapStreak);
                /**
                 * Restoring the cursor only makes a newly crossed milestone ELIGIBLE. Nothing else in
                 * this path grants it: the response comes from GetStreakInfoQuery, which calls
                 * CalculateAsync rather than RecalculateAsync, so an award earned by the repaired run
                 * would sit pending and be lost the next time the streak reset. The repair grants it
                 * here, in the same save that spends the bank.
                 */
                user.AwardStreakFreezeIfEligible(
                    AppConstants.MaxStreakFreezesAccumulated,
                    AppConstants.StreakDaysPerFreeze);
                foreach (var freeze in gap.Value)
                    await streakFreezeRepository.AddAsync(freeze, transactionToken);

                try
                {
                    await unitOfWork.SaveChangesAsync(transactionToken);
                }
                catch (DbUpdateException exception)
                {
                    unitOfWork.ResetTracking();
                    if (DbUniqueViolation.IsUniqueViolation(exception))
                        return Result.Failure<int>(ErrorMessages.StreakGapRepairUnavailable);
                    throw;
                }

                return Result.Success(gap.Value.Count);
            },
            cancellationToken);

        if (repaired.IsFailure)
            return repaired.PropagateError<StreakInfoResponse>();

        logger.LogInformation("Streak gap repaired for {UserId} using {FreezeCount} freezes", request.UserId, repaired.Value);
        return await sender.Send(new GetStreakInfoQuery(request.UserId), cancellationToken);
    }
}
