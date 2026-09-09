using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkDeleteHabitsCommand(
    Guid UserId,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result<BulkDeleteResult>>;

public record BulkDeleteResult(IReadOnlyList<BulkDeleteItemResult> Results);

public record BulkDeleteItemResult(
    int Index,
    BulkItemStatus Status,
    Guid HabitId,
    string? Error = null);

public class BulkDeleteHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUserStreakService userStreakService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<BulkDeleteHabitsCommand, Result<BulkDeleteResult>>
{
    public async Task<Result<BulkDeleteResult>> Handle(BulkDeleteHabitsCommand request, CancellationToken cancellationToken)
    {
        var results = new List<BulkDeleteItemResult>();

        var habits = await habitRepository.FindTrackedAsync(
            h => request.HabitIds.Contains(h.Id) && h.UserId == request.UserId,
            query => query.Include(h => h.Goals),
            cancellationToken);
        var habitDict = habits.ToDictionary(h => h.Id);

        await HabitCeilingLock.ExecuteAsync(unitOfWork, request.UserId, async ct =>
        {
            for (int i = 0; i < request.HabitIds.Count; i++)
            {
                var habitId = request.HabitIds[i];

                if (!habitDict.TryGetValue(habitId, out var habit))
                {
                    results.Add(new BulkDeleteItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Habit not found or not owned by user."));
                    continue;
                }

                habit.RemoveAllGoals();
                habit.SoftDelete();
                results.Add(new BulkDeleteItemResult(
                    Index: i,
                    Status: BulkItemStatus.Success,
                    HabitId: habitId));
            }

            await unitOfWork.SaveChangesAsync(ct);
            if (results.Any(r => r.Status == BulkItemStatus.Success))
            {
                await ConcurrencyRetry.SaveWithRetryAsync(
                    unitOfWork,
                    c => userStreakService.RecalculateAsync(request.UserId, cancellationToken: c),
                    ct);
            }
        }, cancellationToken);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success(new BulkDeleteResult(results));
    }
}
