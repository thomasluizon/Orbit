using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Daily background service that purges processed billing dedup records (Play RTDN messages and
/// Stripe webhook events) older than their provider redelivery windows, keeping the idempotency
/// tables bounded.
/// </summary>
public partial class PlayNotificationCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<PlayNotificationCleanupService> logger) : BackgroundService, IScheduledJob
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan PlayRetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan StripeRetentionPeriod = TimeSpan.FromDays(90);

    public string Name => "play-notification-cleanup";

    public string CronExpression => "0 4 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PurgeOldNotifications(cancellationToken);
        BackgroundServiceHealthCheck.RecordTick("PlayNotificationCleanup");
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
                    await PurgeOldNotifications(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("PlayNotificationCleanup");
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

    private async Task PurgeOldNotifications(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

#pragma warning disable ORBIT0004
        var playCutoff = DateTime.UtcNow - PlayRetentionPeriod;
#pragma warning restore ORBIT0004
        var purgedNotifications = await dbContext.ProcessedPlayNotifications
            .Where(n => n.ProcessedAtUtc < playCutoff)
            .ExecuteDeleteAsync(ct);

        if (purgedNotifications > 0)
            LogNotificationsPurged(logger, purgedNotifications);

#pragma warning disable ORBIT0004
        var stripeCutoff = DateTime.UtcNow - StripeRetentionPeriod;
#pragma warning restore ORBIT0004
        var purgedEvents = await dbContext.ProcessedStripeEvents
            .Where(e => e.ProcessedAtUtc < stripeCutoff)
            .ExecuteDeleteAsync(ct);

        if (purgedEvents > 0)
            LogStripeEventsPurged(logger, purgedEvents);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PlayNotificationCleanupService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PlayNotificationCleanupService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error purging processed Play notifications")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Purged {Count} processed Play notifications older than 30 days")]
    private static partial void LogNotificationsPurged(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Purged {Count} processed Stripe events older than 90 days")]
    private static partial void LogStripeEventsPurged(ILogger logger, int count);
}
