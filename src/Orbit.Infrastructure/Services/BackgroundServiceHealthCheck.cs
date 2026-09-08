using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Orbit.Infrastructure.Services;

public class BackgroundServiceHealthCheck : IHealthCheck
{
    private static readonly ConcurrentDictionary<string, DateTime> LastSuccessfulTicks = new();

    private static readonly Dictionary<string, TimeSpan> ExpectedIntervals = new()
    {
        ["ReminderScheduler"] = TimeSpan.FromMinutes(3),
        ["GoalDeadlineNotification"] = TimeSpan.FromMinutes(90),
        ["SlipAlertScheduler"] = TimeSpan.FromMinutes(15),
        ["ProactiveCheckinScheduler"] = TimeSpan.FromMinutes(180),
        ["HabitDueDateAdvancement"] = TimeSpan.FromMinutes(90),
        ["StreakGoalSync"] = TimeSpan.FromMinutes(90),
        ["StreakFreezeAutoActivation"] = TimeSpan.FromMinutes(180),
        ["AccountDeletion"] = TimeSpan.FromHours(72),
        ["SyncCleanup"] = TimeSpan.FromHours(48),
        ["PlayNotificationCleanup"] = TimeSpan.FromHours(48),
        ["CalendarAutoSync"] = TimeSpan.FromMinutes(45),
        ["OpenAiBatchPoller"] = TimeSpan.FromMinutes(10),
        ["AiUsageSummary"] = TimeSpan.FromMinutes(180)
    };

    public static void RecordTick(string serviceName)
    {
#pragma warning disable ORBIT0004
        LastSuccessfulTicks[serviceName] = DateTime.UtcNow;
#pragma warning restore ORBIT0004
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var unhealthy = new List<string>();
        var serviceTickStatuses = new Dictionary<string, object>();

        foreach (var (name, maxInterval) in ExpectedIntervals)
        {
            if (LastSuccessfulTicks.TryGetValue(name, out var lastTick))
            {
#pragma warning disable ORBIT0004
                var elapsed = DateTime.UtcNow - lastTick;
#pragma warning restore ORBIT0004
                serviceTickStatuses[name] = $"Last tick: {elapsed.TotalMinutes:F0}m ago";
                if (elapsed > maxInterval)
                    unhealthy.Add(name);
            }
            else
            {
                serviceTickStatuses[name] = "No tick recorded yet";
            }
        }

        if (unhealthy.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Stale services: {string.Join(", ", unhealthy)}", data: serviceTickStatuses));

        return Task.FromResult(HealthCheckResult.Healthy("All background services running", serviceTickStatuses));
    }
}
