using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkSkipHabitsTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "bulk_skip_habits";

    public string Description =>
        "Skip multiple habits from the complete server-side set matching a filter in one operation. Use only for habits the user explicitly described skipping. Reports applied, matched, skipped, and partial counts.";

    public object GetParameterSchema() => BulkHabitToolArguments.ActionFilterSchema(
        "Legacy array of habit IDs to skip.",
        "Date to skip in YYYY-MM-DD format. Defaults to today.");

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
            return new ToolResult(false, Error: "No matching habits found to skip.");

        return await BulkUpdateHabitsTool.ExecuteInChunksAsync(
            habits.Select(habit => new BulkSkipItem(habit.Id, targetDateResult.Date)).ToList(),
            (items, cancellationToken) => mediator.Send(
                new BulkSkipHabitsCommand(userId, items),
                cancellationToken),
            result => result.Results.Count(item => item.Status == BulkItemStatus.Success),
            "Skipped",
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
