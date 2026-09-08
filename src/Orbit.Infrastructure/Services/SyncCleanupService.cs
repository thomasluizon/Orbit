using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Daily background service that purges soft-deleted entities once they fall outside the
/// incremental-sync window. The cutoff carries a one-day margin beyond the window a client
/// is allowed to request (<see cref="AppConstants.MaxSyncWindowDays"/>) so a row is never
/// purged on the exact boundary a slow client may still request it via /sync/changes.
/// </summary>
public partial class SyncCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncCleanupService> logger) : BackgroundService, IScheduledJob
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetentionPeriod =
        TimeSpan.FromDays(AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays);
    private static readonly TimeSpan SuggestionRetentionPeriod = TimeSpan.FromDays(14);

    public string Name => "sync-cleanup";

    public string CronExpression => "30 3 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PurgeSoftDeletedEntities(cancellationToken);
        BackgroundServiceHealthCheck.RecordTick("SyncCleanup");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PurgeSoftDeletedEntities(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("SyncCleanup");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogServiceError(logger, ex);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        finally
        {
            LogServiceStopped(logger);
        }
    }

    internal async Task PurgeSoftDeletedEntities(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

#pragma warning disable ORBIT0004
        var cutoff = DateTime.UtcNow - RetentionPeriod;
#pragma warning restore ORBIT0004
        var totalPurged = 0;

        totalPurged += await PurgeAsync(dbContext.Habits, h => h.IsDeleted && h.DeletedAtUtc < cutoff, ct);
        totalPurged += await PurgeAsync(dbContext.Goals, g => g.IsDeleted && g.DeletedAtUtc < cutoff, ct);
        totalPurged += await PurgeAsync(dbContext.Tags, t => t.IsDeleted && t.DeletedAtUtc < cutoff, ct);
        totalPurged += await PurgeAsync(dbContext.UserFacts, f => f.IsDeleted && f.DeletedAtUtc < cutoff, ct);
        totalPurged += await PurgeAsync(dbContext.HabitLogs, l => l.IsDeleted && l.DeletedAtUtc < cutoff, ct);
        totalPurged += await PurgeAsync(dbContext.GoalProgressLogs, l => l.IsDeleted && l.DeletedAtUtc < cutoff, ct);
        totalPurged += await PurgeAsync(dbContext.Notifications, n => n.IsDeleted && n.DeletedAtUtc < cutoff, ct);
        totalPurged += await PurgeAsync(dbContext.ChecklistTemplates, c => c.IsDeleted && c.DeletedAtUtc < cutoff, ct);

#pragma warning disable ORBIT0004
        var suggestionCutoff = DateTime.UtcNow - SuggestionRetentionPeriod;
#pragma warning restore ORBIT0004
        var abandonedSuggestions = await dbContext.GoogleCalendarSyncSuggestions
            .Where(s => s.DiscoveredAtUtc < suggestionCutoff && s.ImportedAtUtc == null)
            .ToListAsync(ct);

        if (abandonedSuggestions.Count > 0)
        {
            dbContext.GoogleCalendarSyncSuggestions.RemoveRange(abandonedSuggestions);
            totalPurged += abandonedSuggestions.Count;
        }

        if (totalPurged > 0)
        {
            await dbContext.SaveChangesAsync(ct);
            LogEntitiesPurged(logger, totalPurged);
        }
    }

    private static async Task<int> PurgeAsync<TEntity>(
        DbSet<TEntity> set, Expression<Func<TEntity, bool>> predicate, CancellationToken ct)
        where TEntity : class
    {
        var deleted = await set.IgnoreQueryFilters().Where(predicate).ToListAsync(ct);
        if (deleted.Count == 0)
            return 0;

        set.RemoveRange(deleted);
        return deleted.Count;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "SyncCleanupService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "SyncCleanupService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in sync cleanup")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Purged {Count} soft-deleted entities outside the sync window")]
    private static partial void LogEntitiesPurged(ILogger logger, int count);
}
