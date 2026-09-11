using System.Globalization;
using System.Text.Json;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Enums;

namespace Orbit.Application.Chat.Tools.Implementations;

internal static class BulkHabitToolArguments
{
    public static (BulkHabitFilter? Filter, string? Error) ParseRequiredFilter(JsonElement args)
    {
        if (!args.TryGetProperty("filter", out var filterElement) || filterElement.ValueKind != JsonValueKind.Object)
            return (null, "filter is required and must be an object.");

        return ParseFilter(filterElement);
    }

    public static (BulkHabitFilter? Filter, string? Error) ParseEmojiFilter(JsonElement args)
    {
        if (args.TryGetProperty("filter", out var filterElement))
        {
            if (filterElement.ValueKind != JsonValueKind.Object)
                return (null, "filter must be an object.");

            return ParseFilter(filterElement);
        }

        var ids = new List<Guid>();
        if (args.TryGetProperty("habit_ids", out var idsElement))
        {
            if (idsElement.ValueKind != JsonValueKind.Array || idsElement.GetArrayLength() == 0)
                return (null, "habit_ids must be omitted or provided as a non-empty array of valid habit IDs.");

            foreach (var idElement in idsElement.EnumerateArray())
            {
                if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParse(idElement.GetString(), out var id))
                    return (null, "habit_ids must contain only valid habit IDs.");
                ids.Add(id);
            }
        }

        var includeCompleted = JsonArgumentParser.GetOptionalBool(args, "include_completed") ?? false;
        return (new BulkHabitFilter(ids.Count == 0, ids, includeCompleted), null);
    }

    public static (BulkHabitFilter? Filter, string? Error) ParseActionFilter(JsonElement args)
    {
        var hasFilter = args.TryGetProperty("filter", out var filterElement)
            && filterElement.ValueKind != JsonValueKind.Null;
        var hasHabitIds = args.TryGetProperty("habit_ids", out var habitIdsElement)
            && habitIdsElement.ValueKind != JsonValueKind.Null;
        if (hasFilter && hasHabitIds)
            return (null, "filter cannot combine with habit_ids.");
        if (hasFilter)
            return ParseRequiredFilter(args);
        if (!hasHabitIds)
            return (null, "Provide filter or habit_ids.");

        var (filter, error) = ParseEmojiFilter(args);
        return error is null && filter is not null
            ? (filter with { IncludeCompleted = true }, null)
            : (null, error);
    }

    public static object ActionFilterSchema(string idsDescription, string dateDescription) => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            filter = FilterSchema(),
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String },
                description = idsDescription
            },
            date = new
            {
                type = JsonSchemaTypes.String,
                nullable = true,
                description = dateDescription
            }
        },
        required = Array.Empty<string>()
    };

    public static (BulkHabitChanges? Changes, string? Error) ParseChanges(JsonElement args)
    {
        if (!args.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Object)
            return (null, "updates is required and must be an object.");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "title", "description", "emoji", "frequency_unit", "frequency_quantity", "interval_weeks",
            "days", "due_date", "end_date", "due_time", "is_bad_habit", "is_flexible",
            "reminder_enabled", "reminder_times", "checklist_items", "scheduled_reminders"
        };
        if (updates.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            return (null, "updates contains an unsupported field.");
        var kindError = ValidateUpdateKinds(updates);
        if (kindError is not null)
            return (null, kindError);

        var dateResult = ParseNullableDate(updates, "due_date", allowNull: false);
        if (dateResult.Error is not null)
            return (null, dateResult.Error);
        var endDateResult = ParseNullableDate(updates, "end_date", allowNull: true);
        if (endDateResult.Error is not null)
            return (null, endDateResult.Error);
        var timeResult = ParseNullableTime(updates, "due_time");
        if (timeResult.Error is not null)
            return (null, timeResult.Error);
        var frequencyResult = ParseFrequency(updates);
        if (frequencyResult.Error is not null)
            return (null, frequencyResult.Error);

        var changes = new BulkHabitChanges(
            HasTitle: updates.TryGetProperty("title", out _),
            Title: JsonArgumentParser.GetNullableString(updates, "title"),
            HasDescription: updates.TryGetProperty("description", out _),
            Description: JsonArgumentParser.GetNullableString(updates, "description"),
            HasEmoji: updates.TryGetProperty("emoji", out _),
            Emoji: JsonArgumentParser.GetNullableString(updates, "emoji"),
            HasFrequencyUnit: frequencyResult.IsSpecified,
            FrequencyUnit: frequencyResult.Value,
            HasFrequencyQuantity: updates.TryGetProperty("frequency_quantity", out _),
            FrequencyQuantity: JsonArgumentParser.GetOptionalInt(updates, "frequency_quantity"),
            HasIntervalWeeks: updates.TryGetProperty("interval_weeks", out _),
            IntervalWeeks: JsonArgumentParser.GetOptionalInt(updates, "interval_weeks"),
            HasDays: updates.TryGetProperty("days", out _),
            Days: updates.TryGetProperty("days", out _) ? JsonArgumentParser.ParseDays(updates) ?? [] : null,
            HasDueDate: dateResult.IsSpecified,
            DueDate: dateResult.Value,
            HasEndDate: endDateResult.IsSpecified,
            EndDate: endDateResult.Value,
            HasDueTime: timeResult.IsSpecified,
            DueTime: timeResult.Value,
            HasIsBadHabit: updates.TryGetProperty("is_bad_habit", out _),
            IsBadHabit: JsonArgumentParser.GetOptionalBool(updates, "is_bad_habit") ?? false,
            HasIsFlexible: updates.TryGetProperty("is_flexible", out _),
            IsFlexible: JsonArgumentParser.GetOptionalBool(updates, "is_flexible") ?? false,
            HasReminderEnabled: updates.TryGetProperty("reminder_enabled", out _),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(updates, "reminder_enabled") ?? false,
            HasReminderTimes: updates.TryGetProperty("reminder_times", out _),
            ReminderTimes: updates.TryGetProperty("reminder_times", out _) ? JsonArgumentParser.ParseIntArray(updates, "reminder_times") ?? [] : null,
            HasChecklistItems: updates.TryGetProperty("checklist_items", out _),
            ChecklistItems: updates.TryGetProperty("checklist_items", out _) ? JsonArgumentParser.ParseChecklistItems(updates) ?? [] : null,
            HasScheduledReminders: updates.TryGetProperty("scheduled_reminders", out _),
            ScheduledReminders: updates.TryGetProperty("scheduled_reminders", out _) ? JsonArgumentParser.ParseScheduledReminders(updates) ?? [] : null);

        return (changes, null);
    }

    public static object FilterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        description = "Server-side selection. Set all=true for the complete matching set, or provide habit_ids, tag, search, or another predicate.",
        properties = new
        {
            all = new { type = JsonSchemaTypes.Boolean, description = "Select the complete matching set." },
            habit_ids = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } },
            tag = new { type = JsonSchemaTypes.String, description = "Exact tag name, case-insensitive." },
            search = new { type = JsonSchemaTypes.String, description = "Title or description contains text, case-insensitive." },
            is_completed = new { type = JsonSchemaTypes.Boolean, description = "Select completed habits when true or active habits when false. Active habits are the default." },
            is_general = new { type = JsonSchemaTypes.Boolean },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean },
            frequency = new { type = JsonSchemaTypes.String, @enum = new[] { "Day", "Week", "Month", "Year", "OneTime" } }
        }
    };

    private static (BulkHabitFilter? Filter, string? Error) ParseFilter(JsonElement filter)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "all", "habit_ids", "tag", "search", "is_completed", "is_general", "is_bad_habit", "frequency"
        };
        if (filter.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            return (null, "filter contains an unsupported field.");
        foreach (var booleanName in new[] { "all", "is_completed", "is_general", "is_bad_habit" })
        {
            if (filter.TryGetProperty(booleanName, out var booleanValue)
                && booleanValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                return (null, $"filter.{booleanName} must be a boolean.");
        }
        foreach (var stringName in new[] { "tag", "search" })
        {
            if (filter.TryGetProperty(stringName, out var stringValue) && stringValue.ValueKind != JsonValueKind.String)
                return (null, $"filter.{stringName} must be a string.");
        }

        var ids = new List<Guid>();
        if (filter.TryGetProperty("habit_ids", out var idsElement))
        {
            if (idsElement.ValueKind != JsonValueKind.Array)
                return (null, "filter.habit_ids must be an array of valid habit IDs.");
            foreach (var idElement in idsElement.EnumerateArray())
            {
                if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParse(idElement.GetString(), out var id))
                    return (null, "filter.habit_ids must contain only valid habit IDs.");
                ids.Add(id);
            }
        }

        FrequencyUnit? frequency = null;
        var oneTime = false;
        if (filter.TryGetProperty("frequency", out var frequencyElement))
        {
            if (frequencyElement.ValueKind != JsonValueKind.String)
                return (null, "filter.frequency must be Day, Week, Month, Year, or OneTime.");
            var value = frequencyElement.GetString();
            if (string.Equals(value, "OneTime", StringComparison.OrdinalIgnoreCase))
                oneTime = true;
            else if (Enum.TryParse<FrequencyUnit>(value, true, out var parsedFrequency))
                frequency = parsedFrequency;
            else
                return (null, "filter.frequency must be Day, Week, Month, Year, or OneTime.");
        }

        var result = new BulkHabitFilter(
            All: JsonArgumentParser.GetOptionalBool(filter, "all") ?? false,
            HabitIds: ids,
            Tag: JsonArgumentParser.GetNullableString(filter, "tag"),
            Search: JsonArgumentParser.GetNullableString(filter, "search"),
            IsGeneral: JsonArgumentParser.GetOptionalBool(filter, "is_general"),
            IsBadHabit: JsonArgumentParser.GetOptionalBool(filter, "is_bad_habit"),
            Frequency: frequency,
            OneTime: oneTime,
            IsCompleted: JsonArgumentParser.GetOptionalBool(filter, "is_completed"));

        if (result.All && result.HabitIds.Count > 0)
            return (null, "filter cannot combine all=true with habit_ids.");

        return result.HasSelector
            ? (result, null)
            : (null, "filter must explicitly select all habits or provide at least one predicate.");
    }

    private static (bool IsSpecified, DateOnly? Value, string? Error) ParseNullableDate(
        JsonElement element,
        string propertyName,
        bool allowNull)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return (false, null, null);
        if (property.ValueKind == JsonValueKind.Null && allowNull)
            return (true, null, null);
        if (property.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(property.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return (false, null, $"{propertyName} must be a date in YYYY-MM-DD format{(allowNull ? " or null" : string.Empty)}.");
        }
        return (true, value, null);
    }

    private static (bool IsSpecified, TimeOnly? Value, string? Error) ParseNullableTime(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return (false, null, null);
        if (property.ValueKind == JsonValueKind.Null)
            return (true, null, null);
        if (property.ValueKind != JsonValueKind.String
            || !TimeOnly.TryParseExact(property.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return (false, null, $"{propertyName} must use HH:mm format or null.");
        return (true, value, null);
    }

    private static (bool IsSpecified, FrequencyUnit? Value, string? Error) ParseFrequency(JsonElement element)
    {
        if (!element.TryGetProperty("frequency_unit", out var property))
            return (false, null, null);
        if (property.ValueKind == JsonValueKind.Null)
            return (true, null, null);
        if (property.ValueKind != JsonValueKind.String
            || !Enum.TryParse<FrequencyUnit>(property.GetString(), true, out var value))
            return (false, null, "frequency_unit must be Day, Week, Month, Year, or null.");
        return (true, value, null);
    }

    private static string? ValidateUpdateKinds(JsonElement updates)
    {
        return ValidateStringKinds(updates)
            ?? ValidateIntegerKinds(updates)
            ?? ValidateBooleanKinds(updates)
            ?? ValidateArrayKinds(updates)
            ?? ValidateArrayContents(updates);
    }

    private static string? ValidateStringKinds(JsonElement updates)
    {
        if (!HasKind(updates, "title", JsonValueKind.String))
            return "title must be a string.";
        foreach (var nullableString in new[] { "description", "emoji" })
        {
            if (!HasKind(updates, nullableString, JsonValueKind.String, JsonValueKind.Null))
                return $"{nullableString} must be a string or null.";
        }
        return null;
    }

    private static string? ValidateIntegerKinds(JsonElement updates)
    {
        foreach (var nullableInteger in new[] { "frequency_quantity", "interval_weeks" })
        {
            if (!HasKind(updates, nullableInteger, JsonValueKind.Number, JsonValueKind.Null))
                return $"{nullableInteger} must be an integer or null.";
            if (updates.TryGetProperty(nullableInteger, out var number)
                && number.ValueKind == JsonValueKind.Number
                && !number.TryGetInt32(out _))
                return $"{nullableInteger} must be an integer or null.";
        }
        return null;
    }

    private static string? ValidateBooleanKinds(JsonElement updates)
    {
        foreach (var booleanName in new[] { "is_bad_habit", "is_flexible", "reminder_enabled" })
        {
            if (!HasKind(updates, booleanName, JsonValueKind.True, JsonValueKind.False))
                return $"{booleanName} must be a boolean.";
        }
        return null;
    }

    private static string? ValidateArrayKinds(JsonElement updates)
    {
        foreach (var arrayName in new[] { "days", "reminder_times", "checklist_items", "scheduled_reminders" })
        {
            if (!HasKind(updates, arrayName, JsonValueKind.Array))
                return $"{arrayName} must be an array.";
        }
        return null;
    }

    private static string? ValidateArrayContents(JsonElement updates)
    {
        return ValidateDays(updates)
            ?? ValidateReminderTimes(updates)
            ?? ValidateChecklistItems(updates)
            ?? ValidateScheduledReminders(updates);
    }

    private static string? ValidateDays(JsonElement updates)
    {
        if (updates.TryGetProperty("days", out var days)
            && days.EnumerateArray().Any(day => day.ValueKind != JsonValueKind.String
                || !Enum.TryParse<DayOfWeek>(day.GetString(), true, out _)))
            return "days must contain valid weekday names.";
        return null;
    }

    private static string? ValidateReminderTimes(JsonElement updates)
    {
        if (updates.TryGetProperty("reminder_times", out var reminderTimes)
            && reminderTimes.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
            return "reminder_times must contain only integers.";
        return null;
    }

    private static string? ValidateChecklistItems(JsonElement updates)
    {
        if (updates.TryGetProperty("checklist_items", out var checklistItems)
            && checklistItems.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                || (item.TryGetProperty("is_checked", out var isChecked)
                    && isChecked.ValueKind is not JsonValueKind.True and not JsonValueKind.False)))
            return "checklist_items contains an invalid item.";
        return null;
    }

    private static string? ValidateScheduledReminders(JsonElement updates)
    {
        if (updates.TryGetProperty("scheduled_reminders", out var scheduledReminders)
            && scheduledReminders.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.String
                || !JsonArgumentParser.TryParseScheduledReminderWhen(when.GetString() ?? string.Empty, out _)
                || !item.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.String
                || !TimeOnly.TryParseExact(time.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            return "scheduled_reminders contains an invalid item.";
        return null;
    }

    private static bool HasKind(JsonElement element, string propertyName, params JsonValueKind[] allowedKinds)
    {
        return !element.TryGetProperty(propertyName, out var property)
            || allowedKinds.Contains(property.ValueKind);
    }
}
