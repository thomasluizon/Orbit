using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class SyncController(OrbitDbContext dbContext, ILogger<SyncController> logger) : ControllerBase
{
    private static readonly TimeSpan MaxSyncWindow = TimeSpan.FromDays(AppConstants.MaxSyncWindowDays);

    public record SyncDeletedRef(Guid Id, DateTime DeletedAtUtc);

    public record SyncChangesV2Response(
        SyncEntitySetV2<SyncHabitDto> Habits,
        SyncEntitySetV2<SyncHabitLogDto> HabitLogs,
        SyncEntitySetV2<SyncGoalDto> Goals,
        SyncEntitySetV2<SyncGoalProgressLogDto> GoalProgressLogs,
        SyncEntitySetV2<SyncTagDto> Tags,
        SyncEntitySetV2<SyncNotificationDto> Notifications,
        SyncEntitySetV2<SyncChecklistTemplateDto> ChecklistTemplates,
        DateTime ServerTimestamp,
        int Version = 2);

    public record SyncEntitySetV2<T>(
        IReadOnlyList<T> Updated,
        IReadOnlyList<SyncDeletedRef> Deleted);

    public record SyncHabitDto(
        Guid Id,
        string Title,
        string? Description,
        Domain.Enums.FrequencyUnit? FrequencyUnit,
        int? FrequencyQuantity,
        bool IsBadHabit,
        bool IsCompleted,
        DateOnly DueDate,
        TimeOnly? DueTime,
        TimeOnly? DueEndTime,
        bool ReminderEnabled,
        IReadOnlyList<int> ReminderTimes,
        bool IsGeneral,
        bool IsFlexible,
        bool SlipAlertEnabled,
        IReadOnlyList<Orbit.Domain.ValueObjects.ChecklistItem> ChecklistItems,
        IReadOnlyList<Orbit.Domain.ValueObjects.ScheduledReminderTime> ScheduledReminders,
        DateOnly? EndDate,
        int? Position,
        Guid? ParentHabitId,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        string? Emoji = null,
        int? IntervalWeeks = null);

    public record SyncHabitLogDto(Guid Id, Guid HabitId, DateOnly Date, decimal Value, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

    public record SyncGoalDto(
        Guid Id,
        string Title,
        string? Description,
        decimal TargetValue,
        decimal CurrentValue,
        string Unit,
        GoalStatus Status,
        GoalType Type,
        DateOnly? Deadline,
        int Position,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        DateTime? CompletedAtUtc,
        DateTime? StreakSyncedAtUtc,
        bool? IsProgressDerived = null);

    public record SyncGoalProgressLogDto(
        Guid Id,
        Guid GoalId,
        decimal Value,
        decimal PreviousValue,
        string? Note,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public record SyncTagDto(Guid Id, string Name, string Color, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

    public record SyncNotificationDto(
        Guid Id,
        string Title,
        string Body,
        string? Url,
        Guid? HabitId,
        bool IsRead,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public record SyncChecklistTemplateDto(
        Guid Id,
        string Name,
        IReadOnlyList<string> Items,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public record SyncBatchRequest(IReadOnlyList<SyncMutation> Mutations);

    public record SyncMutation(
        string Entity,
        string Action,
        Guid? Id,
        Dictionary<string, object?>? Data);

    public record SyncBatchResponse(
        int Processed,
        int Failed,
        IReadOnlyList<SyncMutationResult> Results);

    public record SyncMutationResult(
        int Index,
        string Status,
        string? Error = null);

    /// <summary>
    /// Returns the redacted sync DTOs modified since the given timestamp. The response projects to
    /// dedicated DTOs and never serializes EF entities directly. Returns 410 Gone if since &gt; 30 days old.
    /// </summary>
    [HttpGet("v2/changes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> GetChangesV2(
        [FromQuery] DateTime since,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();

#pragma warning disable ORBIT0004
        if (DateTime.UtcNow - since > MaxSyncWindow)
#pragma warning restore ORBIT0004
            return StatusCode(StatusCodes.Status410Gone, ErrorMessages.SyncWindowExceeded.ToErrorBody());

        var sinceUtc = DateTime.SpecifyKind(since, DateTimeKind.Utc);

        var habits = await dbContext.Habits
            .IgnoreQueryFilters()
            .Where(h => h.UserId == userId && h.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var habitIds = habits.Select(h => h.Id).ToList();

        var habitLogs = await dbContext.HabitLogs
            .IgnoreQueryFilters()
            .Where(l => habitIds.Contains(l.HabitId) && l.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var goals = await dbContext.Goals
            .IgnoreQueryFilters()
            .Where(g => g.UserId == userId && g.UpdatedAtUtc > sinceUtc)
            .Include(g => g.Habits)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var goalIds = goals.Select(g => g.Id).ToList();

        var goalProgressLogs = await dbContext.GoalProgressLogs
            .IgnoreQueryFilters()
            .Where(l => goalIds.Contains(l.GoalId) && l.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var tags = await dbContext.Tags
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var notifications = await dbContext.Notifications
            .IgnoreQueryFilters()
            .Where(n => n.UserId == userId && n.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var checklistTemplates = await dbContext.ChecklistTemplates
            .IgnoreQueryFilters()
            .Where(ct => ct.UserId == userId && ct.UpdatedAtUtc > sinceUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var response = new SyncChangesV2Response(
            Habits: new SyncEntitySetV2<SyncHabitDto>(
                habits.Where(h => !h.IsDeleted).Select(MapHabit).ToList(),
                habits.Where(h => h.IsDeleted).Select(h => new SyncDeletedRef(h.Id, h.DeletedAtUtc!.Value)).ToList()),
            HabitLogs: new SyncEntitySetV2<SyncHabitLogDto>(
                habitLogs.Where(l => !l.IsDeleted).Select(MapHabitLog).ToList(),
                habitLogs.Where(l => l.IsDeleted).Select(l => new SyncDeletedRef(l.Id, l.DeletedAtUtc!.Value)).ToList()),
            Goals: new SyncEntitySetV2<SyncGoalDto>(
                goals.Where(g => !g.IsDeleted).Select(MapGoal).ToList(),
                goals.Where(g => g.IsDeleted).Select(g => new SyncDeletedRef(g.Id, g.DeletedAtUtc!.Value)).ToList()),
            GoalProgressLogs: new SyncEntitySetV2<SyncGoalProgressLogDto>(
                goalProgressLogs.Where(l => !l.IsDeleted).Select(MapGoalProgressLog).ToList(),
                goalProgressLogs.Where(l => l.IsDeleted).Select(l => new SyncDeletedRef(l.Id, l.DeletedAtUtc!.Value)).ToList()),
            Tags: new SyncEntitySetV2<SyncTagDto>(
                tags.Where(t => !t.IsDeleted).Select(MapTag).ToList(),
                tags.Where(t => t.IsDeleted).Select(t => new SyncDeletedRef(t.Id, t.DeletedAtUtc!.Value)).ToList()),
            Notifications: new SyncEntitySetV2<SyncNotificationDto>(
                notifications.Where(n => !n.IsDeleted).Select(MapNotification).ToList(),
                notifications.Where(n => n.IsDeleted).Select(n => new SyncDeletedRef(n.Id, n.DeletedAtUtc!.Value)).ToList()),
            ChecklistTemplates: new SyncEntitySetV2<SyncChecklistTemplateDto>(
                checklistTemplates.Where(ct => !ct.IsDeleted).Select(MapChecklistTemplate).ToList(),
                checklistTemplates.Where(ct => ct.IsDeleted).Select(ct => new SyncDeletedRef(ct.Id, ct.DeletedAtUtc!.Value)).ToList()),
#pragma warning disable ORBIT0004
            ServerTimestamp: DateTime.UtcNow);
#pragma warning restore ORBIT0004

        LogSyncChanges(logger, userId, since);
        return Ok(response);
    }

    /// <summary>
    /// Accepts an array of mutations, processes in order with server-wins conflict resolution.
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ProcessBatch(
        [FromBody] SyncBatchRequest request,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();

        if (request.Mutations.Count == 0)
            return BadRequest(ErrorMessages.NoMutations.ToErrorBody());

        if (request.Mutations.Count > 100)
            return BadRequest(ErrorMessages.TooManyMutations.Format(100).ToErrorBody());

        var results = new List<SyncMutationResult>();
        var processed = 0;
        var failed = 0;

        var supportsTransactions = dbContext.Database.IsRelational();
        var strategy = dbContext.Database.CreateExecutionStrategy();
        var mutationCount = Math.Min(request.Mutations.Count, 100);
        for (int i = 0; i < mutationCount; i++)
        {
            var mutation = request.Mutations[i];
            var index = i;
            try
            {
                await ApplyMutationWithRetryAsync(userId, mutation, supportsTransactions, strategy, cancellationToken);
                results.Add(new SyncMutationResult(index, "success"));
                processed++;
            }
            catch (Exception ex)
            {
                LogMutationFailed(logger, index, mutation.Entity, mutation.Action, ex);
                results.Add(new SyncMutationResult(index, "failed", ErrorMessages.MutationFailed.Message));
                failed++;
            }
        }

        LogSyncBatch(logger, userId, processed, failed);
        return Ok(new SyncBatchResponse(processed, failed, results));
    }

    private const int MaxMutationAttempts = 3;

    private async Task ApplyMutationWithRetryAsync(
        Guid userId,
        SyncMutation mutation,
        bool supportsTransactions,
        IExecutionStrategy strategy,
        CancellationToken cancellationToken)
    {
        var attempt = 1;
        while (true)
        {
            try
            {
                if (supportsTransactions)
                {
                    await strategy.ExecuteAsync(async () =>
                    {
                        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                        try
                        {
                            await ProcessMutation(userId, mutation, cancellationToken);
                            await dbContext.SaveChangesAsync(cancellationToken);
                            await tx.CommitAsync(cancellationToken);
                        }
                        catch
                        {
                            dbContext.ChangeTracker.Clear();
                            throw;
                        }
                    });
                }
                else
                {
                    await ProcessMutation(userId, mutation, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxMutationAttempts)
            {
                dbContext.ChangeTracker.Clear();
            }

            attempt++;
        }
    }

    private static SyncHabitDto MapHabit(Habit habit)
    {
        return new SyncHabitDto(
            habit.Id,
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.IsBadHabit,
            habit.IsCompleted,
            habit.DueDate,
            habit.DueTime,
            habit.DueEndTime,
            habit.ReminderEnabled,
            habit.ReminderTimes,
            habit.IsGeneral,
            habit.IsFlexible,
            habit.SlipAlertEnabled,
            habit.ChecklistItems,
            habit.ScheduledReminders,
            habit.EndDate,
            habit.Position,
            habit.ParentHabitId,
            habit.CreatedAtUtc,
            habit.UpdatedAtUtc,
            Emoji: habit.Emoji,
            IntervalWeeks: habit.IntervalWeeks);
    }

    private static SyncHabitLogDto MapHabitLog(HabitLog habitLog)
    {
        return new SyncHabitLogDto(
            habitLog.Id,
            habitLog.HabitId,
            habitLog.Date,
            habitLog.Value,
            habitLog.CreatedAtUtc,
            habitLog.UpdatedAtUtc);
    }

    private static SyncGoalDto MapGoal(Goal goal)
    {
        return new SyncGoalDto(
            goal.Id,
            goal.Title,
            goal.Description,
            goal.TargetValue,
            goal.CurrentValue,
            goal.Unit,
            goal.Status,
            goal.Type,
            goal.Deadline,
            goal.Position,
            goal.CreatedAtUtc,
            goal.UpdatedAtUtc,
            goal.CompletedAtUtc,
            goal.StreakSyncedAtUtc,
            goal.IsProgressDerived);
    }

    private static SyncGoalProgressLogDto MapGoalProgressLog(GoalProgressLog goalProgressLog)
    {
        return new SyncGoalProgressLogDto(
            goalProgressLog.Id,
            goalProgressLog.GoalId,
            goalProgressLog.Value,
            goalProgressLog.PreviousValue,
            goalProgressLog.Note,
            goalProgressLog.CreatedAtUtc,
            goalProgressLog.UpdatedAtUtc);
    }

    private static SyncTagDto MapTag(Tag tag)
    {
        return new SyncTagDto(tag.Id, tag.Name, tag.Color, tag.CreatedAtUtc, tag.UpdatedAtUtc);
    }

    private static SyncNotificationDto MapNotification(Notification notification)
    {
        return new SyncNotificationDto(
            notification.Id,
            notification.Title,
            notification.Body,
            notification.Url,
            notification.HabitId,
            notification.IsRead,
            notification.CreatedAtUtc,
            notification.UpdatedAtUtc);
    }

    private static SyncChecklistTemplateDto MapChecklistTemplate(ChecklistTemplate checklistTemplate)
    {
        return new SyncChecklistTemplateDto(
            checklistTemplate.Id,
            checklistTemplate.Name,
            checklistTemplate.Items,
            checklistTemplate.CreatedAtUtc,
            checklistTemplate.UpdatedAtUtc);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Sync changes requested by user {UserId} since {Since}")]
    private static partial void LogSyncChanges(ILogger logger, Guid userId, DateTime since);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Sync batch processed for user {UserId}: {Processed} succeeded, {Failed} failed")]
    private static partial void LogSyncBatch(ILogger logger, Guid userId, int processed, int failed);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Sync mutation {Index} failed for entity {Entity} action {Action}")]
    private static partial void LogMutationFailed(ILogger logger, int index, string entity, string action, Exception ex);
}
