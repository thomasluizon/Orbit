using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class BulkToolRepeatGuard
{
    public const int Threshold = 3;

    private static readonly IReadOnlyDictionary<string, string> BulkAlternatives =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["update_habit"] = "bulk_update_habits",
            ["log_habit"] = "bulk_log_habits",
            ["skip_habit"] = "bulk_skip_habits",
            ["delete_habit"] = "bulk_delete_habits"
        };

    public static IReadOnlyDictionary<string, string> FindRedirects(IReadOnlyList<AiToolCall> calls)
    {
        var redirects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in calls.GroupBy(call => call.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < Threshold || !BulkAlternatives.TryGetValue(group.Key, out var bulkTool))
                continue;
            foreach (var call in group)
                redirects[call.Id] = bulkTool;
        }
        return redirects;
    }
}
