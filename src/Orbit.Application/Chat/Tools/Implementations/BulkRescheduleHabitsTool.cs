using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkRescheduleHabitsTool(IMediator mediator) : IAiTool
{
    public string Name => "bulk_reschedule_habits";

    public string Description =>
        "Reschedule the complete server-side set of habits matching a filter to one due date. Use one call for every multi-habit reschedule instead of repeating update_habit.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            filter = BulkHabitToolArguments.FilterSchema(),
            due_date = new { type = JsonSchemaTypes.String, description = "New due date in YYYY-MM-DD format." }
        },
        required = new[] { "filter", "due_date" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (filter, filterError) = BulkHabitToolArguments.ParseRequiredFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);
        if (!args.TryGetProperty("due_date", out var dueDateElement)
            || dueDateElement.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(dueDateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate))
        {
            return new ToolResult(false, Error: "due_date must be a date in YYYY-MM-DD format.");
        }

        var changes = new BulkHabitChanges(HasDueDate: true, DueDate: dueDate);
        var result = await mediator.Send(new BulkUpdateHabitsCommand(userId, filter!, changes), ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);
        return BulkUpdateHabitsTool.BuildResult(result.Value, "Rescheduled");
    }
}
