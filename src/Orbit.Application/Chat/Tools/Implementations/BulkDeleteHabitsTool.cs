using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkDeleteHabitsTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository) : IAiTool
{
    public string Name => "bulk_delete_habits";

    public string Description =>
        "Permanently delete the complete server-side set of habits matching a filter in one operation. Only delete habits the user explicitly asked to remove. Reports applied, matched, skipped, and partial counts.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            filter = BulkHabitToolArguments.FilterSchema(),
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                description = "Legacy array of habit IDs to delete.",
                items = new { type = JsonSchemaTypes.String }
            }
        },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (filter, filterError) = BulkHabitToolArguments.ParseActionFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);

        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, ct);
        if (habits.Count == 0)
            return new ToolResult(false, Error: "No matching habits found to delete.");

        return await BulkUpdateHabitsTool.ExecuteInChunksAsync(
            habits.Select(habit => habit.Id).ToList(),
            (habitIds, cancellationToken) => mediator.Send(
                new BulkDeleteHabitsCommand(userId, habitIds),
                cancellationToken),
            result => result.Results.Count(item => item.Status == BulkItemStatus.Success),
            "Deleted",
            ct);
    }
}
