using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;

namespace Orbit.Api.Controllers;

public partial class SyncController
{
    private async Task ProcessMutation(Guid userId, SyncMutation mutation, CancellationToken ct)
    {
        switch (mutation.Entity.ToLowerInvariant())
        {
            case "habit":
                await AcquireHabitCeilingLockAsync(userId, ct);
                await ApplyEntityMutationAsync(
                    mutation, "habit", "delete", dbContext.Habits,
                    h => h.Id == mutation.Id && h.UserId == userId,
                    h => h.SoftDelete(), ct);
                break;
            case "goal":
                await ApplyEntityMutationAsync(
                    mutation, "goal", "delete", dbContext.Goals,
                    g => g.Id == mutation.Id && g.UserId == userId,
                    g => g.SoftDelete(), ct);
                break;
            case "tag":
                await ApplyEntityMutationAsync(
                    mutation, "tag", "delete", dbContext.Tags,
                    t => t.Id == mutation.Id && t.UserId == userId,
                    t => t.SoftDelete(), ct);
                break;
            case "notification":
                await ApplyEntityMutationAsync(
                    mutation, "notification", "read", dbContext.Notifications,
                    n => n.Id == mutation.Id && n.UserId == userId,
                    n => n.MarkAsRead(), ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown entity type: {mutation.Entity}");
        }
    }

    /// <summary>
    /// Joins the same per-user boundary every habit writer holds, so a replayed offline delete cannot
    /// land between a streak repair's eligibility read and its spend. The caller already opened the
    /// transaction the lock lives in. It is taken through raw SQL rather than through
    /// <c>IUnitOfWork</c> because this controller holds the DbContext directly.
    /// </summary>
    private Task AcquireHabitCeilingLockAsync(Guid userId, CancellationToken ct)
    {
        if (dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return Task.CompletedTask;

        return dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext({0}))",
            [HabitCeilingLock.ForUser(userId)],
            ct);
    }

    private static async Task ApplyEntityMutationAsync<TEntity>(
        SyncMutation mutation,
        string entityNoun,
        string supportedAction,
        DbSet<TEntity> set,
        Expression<Func<TEntity, bool>> ownedById,
        Action<TEntity> mutate,
        CancellationToken ct) where TEntity : class
    {
        if (!string.Equals(mutation.Action, supportedAction, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported action: {mutation.Action} for {entityNoun}.");

        if (mutation.Id is null)
            throw new InvalidOperationException($"Id is required for {supportedAction}.");

        var entity = await set.FirstOrDefaultAsync(ownedById, ct);
        if (entity is not null) mutate(entity);
    }
}
