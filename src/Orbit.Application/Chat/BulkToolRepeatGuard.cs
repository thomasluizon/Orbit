using System.Text.Json;
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
        foreach (var toolGroup in calls.GroupBy(call => call.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!BulkAlternatives.TryGetValue(toolGroup.Key, out var bulkTool))
                continue;

            var remaining = toolGroup.ToList();
            while (remaining.Count > 0)
            {
                var seed = remaining[0];
                remaining.RemoveAt(0);
                var equivalent = new List<AiToolCall> { seed };
                for (var index = remaining.Count - 1; index >= 0; index--)
                {
                    if (!HaveEquivalentMutationArguments(seed.Args, remaining[index].Args))
                        continue;
                    equivalent.Add(remaining[index]);
                    remaining.RemoveAt(index);
                }

                if (equivalent.Count < Threshold)
                    continue;
                foreach (var call in equivalent)
                    redirects[call.Id] = bulkTool;
            }
        }
        return redirects;
    }

    private static bool HaveEquivalentMutationArguments(JsonElement left, JsonElement right) =>
        HaveEquivalentJson(left, right, ignoreHabitId: true);

    private static bool HaveEquivalentJson(JsonElement left, JsonElement right, bool ignoreHabitId = false)
    {
        if (left.ValueKind != right.ValueKind)
            return false;

        if (left.ValueKind == JsonValueKind.Object)
        {
            var leftProperties = left.EnumerateObject()
                .Where(property => !ignoreHabitId || property.Name != "habit_id")
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();
            var rightProperties = right.EnumerateObject()
                .Where(property => !ignoreHabitId || property.Name != "habit_id")
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();
            return leftProperties.Length == rightProperties.Length
                && leftProperties.Zip(rightProperties).All(pair =>
                    pair.First.Name == pair.Second.Name
                    && HaveEquivalentJson(pair.First.Value, pair.Second.Value));
        }

        if (left.ValueKind == JsonValueKind.Array)
        {
            var leftItems = left.EnumerateArray().ToArray();
            var rightItems = right.EnumerateArray().ToArray();
            return leftItems.Length == rightItems.Length
                && leftItems.Zip(rightItems).All(pair => HaveEquivalentJson(pair.First, pair.Second));
        }

        return left.GetRawText() == right.GetRawText();
    }
}
