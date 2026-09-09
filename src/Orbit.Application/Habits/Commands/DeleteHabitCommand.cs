using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record DeleteHabitCommand(
    Guid UserId,
    Guid HabitId) : IRequest<Result>;

public class DeleteHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUserStreakService userStreakService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<DeleteHabitCommand, Result>
{
    public async Task<Result> Handle(DeleteHabitCommand request, CancellationToken cancellationToken)
    {
        /**
         * A soft delete removes occurrences from the schedule a streak repair decides eligibility
         * from, so it commits inside HabitCeilingLock like every other writer of that state.
         */
        var result = await HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            request.UserId,
            transactionToken => DeleteSubtreeAsync(request, transactionToken),
            cancellationToken);

        if (result.IsFailure)
            return result;

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }

    private async Task<Result> DeleteSubtreeAsync(DeleteHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.GetByIdAsync(request.HabitId, cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.NoPermission);

        var userHabits = await habitRepository.FindTrackedAsync(
            h => h.UserId == request.UserId,
            query => query.Include(h => h.Goals),
            cancellationToken);
        var childrenByParentId = userHabits.ToLookup(h => h.ParentHabitId);

        var deletedAtUtc = DateTime.UtcNow;
        foreach (var inSubtree in HabitHierarchy.SelfAndDescendants(habit, childrenByParentId))
        {
            inSubtree.RemoveAllGoals();
            inSubtree.SoftDelete(deletedAtUtc);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await ConcurrencyRetry.SaveWithRetryAsync(
            unitOfWork,
            ct => userStreakService.RecalculateAsync(request.UserId, cancellationToken: ct),
            cancellationToken);

        return Result.Success();
    }
}
