using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkLogHabitsTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "bulk_log_habits";

    public string Description =>
        "Log multiple habits from the complete server-side set matching a filter in one operation. Use only for habits the user explicitly described completing. Reports applied, matched, skipped, and partial counts.";

    public object GetParameterSchema() => BulkHabitToolArguments.ActionFilterSchema(
        "Legacy array of habit IDs to log as completed.",
        "Date to log for in YYYY-MM-DD format. Defaults to today.");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (filter, filterError) = BulkHabitToolArguments.ParseActionFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);
        var targetDateResult = await ResolveDateAsync(args, userId, ct);
        if (targetDateResult.Error is not null)
            return new ToolResult(false, Error: targetDateResult.Error);

        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, ct);
        if (habits.Count == 0)
            return new ToolResult(false, Error: "No matching habits found to log.");

        return await BulkUpdateHabitsTool.ExecuteInChunksAsync(
            habits.Select(habit => new BulkLogItem(habit.Id, targetDateResult.Date)).ToList(),
            (items, cancellationToken) => mediator.Send(
                new BulkLogHabitsCommand(userId, items),
                cancellationToken),
            result => result.Results.Count(item => item.Status == BulkItemStatus.Success && item.LogId.HasValue),
            "Logged",
            ct);
    }

    private async Task<(DateOnly Date, string? Error)> ResolveDateAsync(
        JsonElement args,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        if (!args.TryGetProperty("date", out var dateElement) || dateElement.ValueKind == JsonValueKind.Null)
            return (today, null);
        if (dateElement.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (default, "date must use YYYY-MM-DD format.");
        return (date, null);
    }
}
