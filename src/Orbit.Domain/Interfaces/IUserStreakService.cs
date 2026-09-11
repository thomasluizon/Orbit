using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IUserStreakService
{
    Task<IReadOnlyList<DateOnly>> GetRepairableGapDatesAsync(
        Guid userId,
        DateOnly userToday,
        CancellationToken cancellationToken = default);

    Task<UserStreakState?> EvaluateGapRepairAsync(
        Guid userId,
        DateOnly userToday,
        IReadOnlyCollection<DateOnly> dates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recompute the user's streak state from logs, freezes, and habit schedules.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="awardFreezeIfEligible">
    /// When true (default), milestones reached during recalc grant a streak freeze.
    /// Pass false from unlog or other passive recompute paths to avoid awarding
    /// freezes from streaks the user is no longer credited for.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<UserStreakState?> RecalculateAsync(
        Guid userId,
        bool awardFreezeIfEligible = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the user's current streak from persisted data without changing tracked state.
    /// </summary>
    Task<UserStreakState?> CalculateAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether local yesterday can be repaired with one banked freeze without changing state.
    /// </summary>
    Task<StreakRepairEvaluation?> EvaluateRepairAsync(
        Guid userId,
        DateOnly userToday,
        DateOnly missedDate,
        CancellationToken cancellationToken = default);
}
