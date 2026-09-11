using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Api.Mcp;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP habit tools. Mutations route through <see cref="McpExecutorBridge"/> →
/// <see cref="Orbit.Domain.Interfaces.IAgentOperationExecutor"/> with
/// <see cref="Orbit.Domain.Models.AgentExecutionSurface.Mcp"/>, so they share the policy
/// evaluation (read-only-credential denial, ownership pre-check, confirmation gating) and the
/// <c>AgentAuditLogs</c> trail used by every other agent surface; each mutation forwards a
/// snake_case argument object matching its backing <c>IAiTool</c> schema and formats the
/// returned <see cref="McpExecutorResult"/> into the legacy string contract. Read/query tools
/// stay on MediatR. Other MCP toolsets mirror this routing for the same policy + audit coverage.
/// </summary>
[McpServerToolType]
public class HabitTools(IMediator mediator, IUserDateService userDateService, McpExecutorBridge executorBridge)
{
    private const string HabitIdDescription = "The habit ID (GUID)";
    private const string DateFromDescription = "Start date in YYYY-MM-DD format";
    private const string DateToDescription = "End date in YYYY-MM-DD format";
    [McpServerTool(Name = "list_habits"), Description("List habits for a date range with schedule info. Returns paginated results with scheduled dates and overdue status.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> ListHabits(
        ClaimsPrincipal user,
        [Description(DateFromDescription)] string dateFrom,
        [Description(DateToDescription)] string dateTo,
        [Description("Include overdue habits")] bool includeOverdue = true,
        [Description("Search term to filter habits")] string? search = null,
        [Description("Page number (default 1)")] int page = 1,
        [Description("Page size (default 50)")] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetHabitScheduleQuery(
            userId,
            McpInputParser.ParseDate(dateFrom, "dateFrom"),
            McpInputParser.ParseDate(dateTo, "dateTo"),
            includeOverdue,
            search,
            Page: page,
            PageSize: pageSize);

        var result = await mediator.Send(query, cancellationToken);
        if (result.IsFailure)
            return $"Error: {result.Error}";

        var items = result.Value.Items;
        if (items.Count == 0)
            return "No habits found for the given date range.";

        var lines = new List<string>();
        foreach (var h in items)
        {
            lines.Add(McpToolHelpers.FormatHabitLine(new McpToolHelpers.HabitLineData(h.Id, h.Title, h.FrequencyUnit, h.FrequencyQuantity,
                h.DueTime, h.IsCompleted, h.IsOverdue, h.IsBadHabit, h.IsGeneral, h.IsFlexible,
                h.ChecklistItems, h.Tags), indent: 0));
            McpToolHelpers.AppendChildren(lines, h.Children, indent: 1);
        }

        return $"Habits (page {result.Value.Page}/{result.Value.TotalPages}, total: {result.Value.TotalCount}):\n" +
               string.Join("\n", lines);
    }

    [McpServerTool(Name = "get_habit"), Description("Get detailed information about a specific habit by ID.")]
    public async Task<string> GetHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetHabitByIdQuery(userId, McpInputParser.ParseGuid(habitId, "habitId"));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var h = result.Value;
        var habitSummary = $"Title: {h.Title}\nID: {h.Id}\n" +
                   $"Status: {(h.IsCompleted ? "Completed" : "Active")}\n" +
                   (h.Description is not null ? $"Description: {h.Description}\n" : "") +
                   (h.FrequencyUnit is not null ? $"Frequency: {h.FrequencyQuantity}x per {h.FrequencyUnit}\n" : "Type: One-time task\n") +
                   $"Due Date: {h.DueDate}\n" +
                   (h.DueTime is not null ? $"Due Time: {h.DueTime:HH:mm}\n" : "") +
                   (h.Days.Count > 0 ? $"Days: {string.Join(", ", h.Days)}\n" : "") +
                   (h.IsBadHabit ? "Bad Habit: Yes\n" : "") +
                   (h.IsGeneral ? "General: Yes\n" : "") +
                   (h.IsFlexible ? "Flexible: Yes\n" : "") +
                   (h.ChecklistItems.Count > 0 ? $"Checklist: {h.ChecklistItems.Count(i => i.IsChecked)}/{h.ChecklistItems.Count} items\n" : "") +
                   $"Created: {h.CreatedAtUtc:yyyy-MM-dd}\n" +
                   (h.Children.Count > 0 ? $"Sub-habits: {string.Join(", ", h.Children.Select(c => c.Title))}\n" : "");
        return habitSummary;
    }

    [McpServerTool(Name = "create_habit"), Description("Create a new habit or one-time task.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> CreateHabit(
        ClaimsPrincipal user,
        [Description("Name of the habit")] string title,
        [Description("Due date in YYYY-MM-DD format")] string dueDate,
        [Description("Optional description")] string? description = null,
        [Description("Frequency unit: Day, Week, Month, or Year. Omit for one-time tasks.")] string? frequencyUnit = null,
        [Description("Frequency quantity (default 1). Omit for one-time tasks.")] int? frequencyQuantity = null,
        [Description("Whether this is a bad habit to avoid")] bool isBadHabit = false,
        [Description("Whether this is a general habit with no schedule")] bool isGeneral = false,
        [Description("Whether this is a flexible frequency habit")] bool isFlexible = false,
        [Description("Due time in HH:mm format")] string? dueTime = null,
        [Description("Repeat interval in weeks. Omit for every week.")] int? intervalWeeks = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "create_habit", new
        {
            title,
            due_date = dueDate,
            description,
            frequency_unit = frequencyUnit,
            frequency_quantity = frequencyQuantity,
            is_bad_habit = isBadHabit,
            is_general = isGeneral,
            is_flexible = isFlexible,
            due_time = dueTime,
            interval_weeks = intervalWeeks
        }, confirmationToken: null, cancellationToken);

        if (!result.Succeeded)
            return result.Message;

        return result.TargetId is not null
            ? $"Created habit '{title}' (id: {result.TargetId})"
            : $"More detail needed before creating '{title}': specify a frequency (e.g. Day, Week) or mark it as a one-time task.";
    }

    [McpServerTool(Name = "update_habit"), Description("Update an existing habit's properties. Title is required (pass current title if unchanged). Only change fields you need to modify.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> UpdateHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("Habit title (required, pass current title if not changing)")] string title,
        [Description("New description")] string? description = null,
        [Description("New frequency unit: Day, Week, Month, Year")] string? frequencyUnit = null,
        [Description("New frequency quantity")] int? frequencyQuantity = null,
        [Description("New due date in YYYY-MM-DD format")] string? dueDate = null,
        [Description("New due time in HH:mm format")] string? dueTime = null,
        [Description("New repeat interval in weeks. Use 1 for every week.")] int? intervalWeeks = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "update_habit", new
        {
            habit_id = habitId,
            title,
            description,
            frequency_unit = frequencyUnit,
            frequency_quantity = frequencyQuantity,
            due_date = dueDate,
            due_time = dueTime,
            interval_weeks = intervalWeeks
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Updated habit {habitId}" : result.Message;
    }

    [McpServerTool(Name = "delete_habit"), Description("Delete a habit by ID.")]
    public async Task<string> DeleteHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: deleting a habit is destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "delete_habit", new
        {
            habit_id = habitId
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? $"Deleted habit {habitId}" : result.Message;
    }

    [McpServerTool(Name = "log_habit"), Description("Log a habit as completed (or toggle it off if already logged today).")]
    public async Task<string> LogHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("Date to log for in YYYY-MM-DD format (defaults to today)")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "log_habit", new
        {
            habit_id = habitId,
            date
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Logged habit {habitId}" : result.Message;
    }

    [McpServerTool(Name = "get_habit_metrics"), Description("Get streak, completion rate, and other metrics for a habit.")]
    public async Task<string> GetHabitMetrics(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetHabitMetricsQuery(userId, McpInputParser.ParseGuid(habitId, "habitId"));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var m = result.Value;
        return $"Metrics for habit {habitId}:\n" +
               $"Current Streak: {m.CurrentStreak} days\n" +
               $"Longest Streak: {m.LongestStreak} days\n" +
               $"Total Completions: {m.TotalCompletions}\n" +
               $"Weekly Completion Rate: {m.WeeklyCompletionRate:P0}\n" +
               $"Monthly Completion Rate: {m.MonthlyCompletionRate:P0}\n" +
               (m.LastCompletedDate is not null ? $"Last Completed: {m.LastCompletedDate}" : "Never completed");
    }

    [McpServerTool(Name = "skip_habit"), Description("Skip a habit for today (or a specific date). Advances to next scheduled date without logging completion. Only works on recurring habits.")]
    public async Task<string> SkipHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("Date to skip in YYYY-MM-DD format (defaults to today)")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "skip_habit", new
        {
            habit_id = habitId,
            date
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Skipped habit {habitId}" : result.Message;
    }

    [McpServerTool(Name = "update_checklist"), Description("Update the checklist items for a habit. Pass the full list of items with their checked state.")]
    public async Task<string> UpdateChecklist(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("JSON array of checklist items, each with 'text' (string) and 'isChecked' (boolean)")] string checklistItemsJson,
        CancellationToken cancellationToken = default)
    {
        var items = McpToolHelpers.DeserializeJson<List<ChecklistItem>>(checklistItemsJson) ?? [];

        var result = await executorBridge.ExecuteAsync(user, "update_checklist", new
        {
            habit_id = habitId,
            checklist_items = items.Select(item => new { text = item.Text, is_checked = item.IsChecked })
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Updated checklist for habit {habitId} ({items.Count} items)" : result.Message;
    }

    [McpServerTool(Name = "get_habit_logs"), Description("Get completion logs for a specific habit (last 365 days).")]
    public async Task<string> GetHabitLogs(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetHabitLogsQuery(userId, McpInputParser.ParseGuid(habitId, "habitId"));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var logs = result.Value;
        if (logs.Count == 0)
            return "No logs found for this habit.";

        var returnedLogs = logs.Take(50).ToList();
        var partial = returnedLogs.Count < logs.Count;
        var lines = returnedLogs.Select(l =>
            $"- {l.Date:yyyy-MM-dd}" +
            $" (id: {l.Id})");

        return $"Logs (total: {logs.Count}, returned: {returnedLogs.Count}, partial: {partial.ToString().ToLowerInvariant()}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "get_all_habit_logs"), Description("Get completion logs for all habits within a date range, grouped by habit ID.")]
    public async Task<string> GetAllHabitLogs(
        ClaimsPrincipal user,
        [Description(DateFromDescription)] string dateFrom,
        [Description(DateToDescription)] string dateTo,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetAllHabitLogsQuery(userId, McpInputParser.ParseDate(dateFrom, "dateFrom"), McpInputParser.ParseDate(dateTo, "dateTo"));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var grouped = result.Value;
        if (grouped.Count == 0)
            return "No logs found for the given date range.";

        var totalLogs = grouped.Sum(group => group.Value.Count);
        var returnedLogCount = grouped.Sum(group => Math.Min(group.Value.Count, 10));
        var partial = returnedLogCount < totalLogs;
        var lines = grouped.Select(group =>
        {
            var returned = group.Value.Take(10).ToList();
            return $"Habit {group.Key}: total {group.Value.Count}, returned {returned.Count} ({string.Join(", ", returned.Select(log => log.Date.ToString("yyyy-MM-dd")))})";
        });

        return $"Logs for {grouped.Count} habits (total_logs: {totalLogs}, returned_logs: {returnedLogCount}, partial: {partial.ToString().ToLowerInvariant()}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "create_sub_habit"), Description("Create a sub-habit under an existing parent habit. Requires Pro subscription.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> CreateSubHabit(
        ClaimsPrincipal user,
        [Description("The parent habit ID (GUID)")] string parentHabitId,
        [Description("Name of the sub-habit")] string title,
        [Description("Optional description")] string? description = null,
        [Description("Frequency unit override: Day, Week, Month, Year")] string? frequencyUnit = null,
        [Description("Frequency quantity override")] int? frequencyQuantity = null,
        [Description("Due time in HH:mm format")] string? dueTime = null,
        [Description("Whether this is a bad habit")] bool isBadHabit = false,
        [Description("Whether this is a flexible frequency habit")] bool isFlexible = false,
        [Description("Due date override in YYYY-MM-DD format")] string? dueDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "create_sub_habit", new
        {
            parent_habit_id = parentHabitId,
            title,
            description,
            frequency_unit = frequencyUnit,
            frequency_quantity = frequencyQuantity,
            due_time = dueTime,
            is_bad_habit = isBadHabit,
            is_flexible = isFlexible,
            due_date = dueDate
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded
            ? $"Created sub-habit '{title}' (id: {result.TargetId}) under parent {parentHabitId}"
            : result.Message;
    }

    [McpServerTool(Name = "duplicate_habit"), Description("Duplicate a habit (including its sub-habits and tags).")]
    public async Task<string> DuplicateHabit(
        ClaimsPrincipal user,
        [Description("The habit ID to duplicate (GUID)")] string habitId,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "duplicate_habit", new
        {
            habit_id = habitId
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Duplicated habit {habitId} (new id: {result.TargetId})" : result.Message;
    }

    [McpServerTool(Name = "bulk_create_habits"), Description("Create multiple habits at once. Each habit can have sub-habits.")]
    public async Task<string> BulkCreateHabits(
        ClaimsPrincipal user,
        [Description("JSON array of habit objects. Each with: title (required), description, frequencyUnit (Day/Week/Month/Year), frequencyQuantity, isBadHabit, dueDate (YYYY-MM-DD), dueTime (HH:mm), isGeneral, isFlexible")] string habitsJson,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: bulk create is a destructive batch operation)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var parsedHabits = McpToolHelpers.DeserializeJson<List<McpToolHelpers.BulkHabitItemDto>>(habitsJson) ?? [];

        var result = await executorBridge.ExecuteAsync(user, "bulk_create_habits", new
        {
            habits = parsedHabits.Select(McpToolHelpers.ToBulkHabitArgs)
        }, confirmationToken, cancellationToken);

        if (!result.Succeeded)
            return result.Message;

        if (result.Payload is not BulkCreateResult bulk)
            return $"Bulk create: {result.TargetName}";

        var successCount = bulk.Results.Count(x => x.Status == BulkItemStatus.Success);
        var failCount = bulk.Results.Count(x => x.Status == BulkItemStatus.Failed);
        var lines = bulk.Results.Select(x =>
            x.Status == BulkItemStatus.Success
                ? $"- OK: {x.Title} (id: {x.HabitId})"
                : $"- FAILED: {x.Title} - {x.Error}");

        return $"Bulk create: {successCount} succeeded, {failCount} failed\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "bulk_delete_habits"), Description("Delete multiple habits at once.")]
    public async Task<string> BulkDeleteHabits(
        ClaimsPrincipal user,
        [Description("Comma-separated habit IDs (GUIDs). Omit when filterJson is provided.")] string? habitIds = null,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: bulk delete is destructive)")] string? confirmationToken = null,
        [Description("Optional JSON server-side filter used instead of habitIds")] string? filterJson = null,
        CancellationToken cancellationToken = default)
    {
        var ids = string.IsNullOrWhiteSpace(habitIds) ? [] : McpToolHelpers.ParseGuidCsv(habitIds);
        var filter = string.IsNullOrWhiteSpace(filterJson) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(filterJson);

        var result = await executorBridge.ExecuteAsync(user, "bulk_delete_habits", new
        {
            habit_ids = ids.Count > 0 ? ids.Select(id => id.ToString()) : null,
            filter
        }, confirmationToken, cancellationToken);

        if (!result.Succeeded)
            return result.Message;

        return result.TargetName ?? "Bulk delete completed.";
    }

    [McpServerTool(Name = "bulk_update_habits"), Description("Update the full server-side set of habits matching a JSON filter in one operation.")]
    public async Task<string> BulkUpdateHabits(
        ClaimsPrincipal user,
        [Description("JSON filter with all, habit_ids, tag, search, include_completed, is_general, is_bad_habit, or frequency")] string filterJson,
        [Description("JSON object containing the habit fields to update")] string updatesJson,
        [Description("Confirmation token returned by confirm_agent_operation_v2")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var filter = JsonSerializer.Deserialize<JsonElement>(filterJson);
        var updates = JsonSerializer.Deserialize<JsonElement>(updatesJson);
        var result = await executorBridge.ExecuteAsync(user, "bulk_update_habits", new
        {
            filter,
            updates
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? result.TargetName ?? "Bulk habit update completed." : result.Message;
    }

    [McpServerTool(Name = "bulk_reschedule_habits"), Description("Reschedule the full server-side set of habits matching a JSON filter in one operation.")]
    public async Task<string> BulkRescheduleHabits(
        ClaimsPrincipal user,
        [Description("JSON filter with all, habit_ids, tag, search, include_completed, is_general, is_bad_habit, or frequency")] string filterJson,
        [Description("New due date in YYYY-MM-DD format")] string dueDate,
        [Description("Confirmation token returned by confirm_agent_operation_v2")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var filter = JsonSerializer.Deserialize<JsonElement>(filterJson);
        var result = await executorBridge.ExecuteAsync(user, "bulk_reschedule_habits", new
        {
            filter,
            due_date = dueDate
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? result.TargetName ?? "Bulk habit reschedule completed." : result.Message;
    }

    [McpServerTool(Name = "bulk_log_habits"), Description("Log multiple habits as completed at once.")]
    public Task<string> BulkLogHabits(
        ClaimsPrincipal user,
        [Description("Comma-separated habit IDs (GUIDs). Omit when filterJson is provided.")] string? habitIds = null,
        [Description("Date to log for in YYYY-MM-DD format (defaults to today)")] string? date = null,
        [Description("Optional JSON server-side filter used instead of habitIds")] string? filterJson = null,
        CancellationToken cancellationToken = default) =>
        ExecuteBulkHabitOperationAsync(user, "bulk_log_habits", habitIds, date, filterJson, cancellationToken);

    [McpServerTool(Name = "bulk_skip_habits"), Description("Skip multiple habits at once.")]
    public Task<string> BulkSkipHabits(
        ClaimsPrincipal user,
        [Description("Comma-separated habit IDs (GUIDs). Omit when filterJson is provided.")] string? habitIds = null,
        [Description("Date to skip in YYYY-MM-DD format (defaults to today)")] string? date = null,
        [Description("Optional JSON server-side filter used instead of habitIds")] string? filterJson = null,
        CancellationToken cancellationToken = default) =>
        ExecuteBulkHabitOperationAsync(user, "bulk_skip_habits", habitIds, date, filterJson, cancellationToken);

    private async Task<string> ExecuteBulkHabitOperationAsync(
        ClaimsPrincipal user,
        string operation,
        string? habitIds,
        string? date,
        string? filterJson,
        CancellationToken cancellationToken)
    {
        var ids = string.IsNullOrWhiteSpace(habitIds) ? [] : McpToolHelpers.ParseGuidCsv(habitIds);
        var filter = string.IsNullOrWhiteSpace(filterJson) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(filterJson);

        var result = await executorBridge.ExecuteAsync(user, operation, new
        {
            habit_ids = ids.Count > 0 ? ids.Select(id => id.ToString()) : null,
            filter,
            date
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? result.TargetName ?? "Bulk habit operation completed." : result.Message;
    }

    [McpServerTool(Name = "reorder_habits"), Description("Reorder habits by setting new positions.")]
    public async Task<string> ReorderHabits(
        ClaimsPrincipal user,
        [Description("JSON array of objects with 'habitId' (GUID) and 'position' (int)")] string positionsJson,
        CancellationToken cancellationToken = default)
    {
        var positions = McpToolHelpers.DeserializeJson<List<McpToolHelpers.HabitPositionDto>>(positionsJson) ?? [];

        var result = await executorBridge.ExecuteAsync(user, "reorder_habits", new
        {
            positions = positions.Select(p => new { habit_id = p.HabitId, position = p.Position })
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Reordered {positions.Count} habits" : result.Message;
    }

    [McpServerTool(Name = "move_habit_parent"), Description("Move a habit under a different parent, or promote it to top-level by passing null parentId.")]
    public async Task<string> MoveHabitParent(
        ClaimsPrincipal user,
        [Description("The habit ID to move (GUID)")] string habitId,
        [Description("New parent habit ID (GUID), or omit/null to make top-level")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "move_habit_parent", new
        {
            habit_id = habitId,
            parent_id = parentId
        }, confirmationToken: null, cancellationToken);

        if (!result.Succeeded)
            return result.Message;

        return parentId is not null
            ? $"Moved habit {habitId} under parent {parentId}"
            : $"Promoted habit {habitId} to top-level";
    }

    [McpServerTool(Name = "link_goals_to_habit"), Description("Link goals to a habit. Pass the full list of goal IDs (replaces existing links).")]
    public async Task<string> LinkGoalsToHabit(
        ClaimsPrincipal user,
        [Description(HabitIdDescription)] string habitId,
        [Description("Comma-separated goal IDs (GUIDs)")] string goalIds,
        CancellationToken cancellationToken = default)
    {
        var ids = McpToolHelpers.ParseGuidCsv(goalIds);

        var result = await executorBridge.ExecuteAsync(user, "link_goals_to_habit", new
        {
            habit_id = habitId,
            goal_ids = ids.Select(id => id.ToString())
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Linked {ids.Count} goals to habit {habitId}" : result.Message;
    }

    [McpServerTool(Name = "get_daily_summary"), Description("Get an AI-generated daily summary of habits. Requires Pro subscription and AI summary enabled.")]
    public async Task<string> GetDailySummary(
        ClaimsPrincipal user,
        [Description(DateFromDescription)] string dateFrom,
        [Description(DateToDescription)] string dateTo,
        [Description("Language code (en, pt-BR)")] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetDailySummaryQuery(
            userId,
            McpInputParser.ParseDate(dateFrom, "dateFrom"),
            McpInputParser.ParseDate(dateTo, "dateTo"),
            language);

        var result = await mediator.Send(query, cancellationToken);
        if (result.IsFailure)
            return $"Error: {result.Error}";

        var s = result.Value;
        return $"Summary{(s.FromCache ? " (cached)" : "")}:\n{s.Summary}";
    }

    [McpServerTool(Name = "get_retrospective"), Description("Get an AI-generated retrospective analysis of habit performance. Requires Pro subscription.")]
    public async Task<string> GetRetrospective(
        ClaimsPrincipal user,
        [Description("Period: week, month, quarter, semester, or year")] string period = "week",
        [Description("Language code (en, pt-BR)")] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve(period, today, weekStartDay);

        var query = new GetRetrospectiveQuery(userId, dateFrom, dateTo, period, language);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var r = result.Value;
        var n = r.Narrative;
        var narrativeText = string.Join(
            "\n\n",
            new[] { n.Highlights, n.Missed, n.Trends, n.Suggestion }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return $"Retrospective ({period}){(r.FromCache ? " (cached)" : "")}:\n{narrativeText}";
    }
}
