using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed partial class BulkUpdateHabitEmojisTool(
    IGenericRepository<Habit> habitRepository,
    IHabitEmojiInferenceService inferenceService,
    IUnitOfWork unitOfWork,
    ILogger<BulkUpdateHabitEmojisTool> logger) : IAiTool
{
    internal const int InferenceChunkSize = 25;

    public string Name => "bulk_update_habit_emojis";

    public string Description =>
        "Update emojis for the complete server-side set of habits matching a filter. Infer distinct sensible emojis in bounded AI batches, or apply one explicitly requested emoji. Reports applied, matched, skipped, and partial counts.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            filter = BulkHabitToolArguments.FilterSchema(),
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String },
                description = "Legacy selection. Omit to update all active habits, or use filter for server-side predicates."
            },
            emoji = new
            {
                type = JsonSchemaTypes.String,
                description = "Optional emoji to apply to every selected habit. Set to null to clear. Omit when infer_from_title is true.",
                nullable = true
            },
            infer_from_title = new
            {
                type = JsonSchemaTypes.Boolean,
                description = "Infer one sensible emoji from each title and description. Defaults to true when emoji is omitted."
            },
            include_completed = new
            {
                type = JsonSchemaTypes.Boolean,
                description = "Legacy selection option. Include completed habits when filter is omitted. Defaults to false."
            }
        },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (filter, filterError) = BulkHabitToolArguments.ParseEmojiFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);

        var hasEmojiArgument = JsonArgumentParser.PropertyExists(args, "emoji");
        var requestedEmoji = hasEmojiArgument ? JsonArgumentParser.GetNullableString(args, "emoji") : null;
        var inferFromTitle = JsonArgumentParser.GetOptionalBool(args, "infer_from_title") ?? !hasEmojiArgument;
        if (!inferFromTitle && !hasEmojiArgument)
            return new ToolResult(false, Error: "Provide emoji or set infer_from_title to true.");

        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, ct);
        if (habits.Count == 0)
            return new ToolResult(false, Error: "No matching habits found to update.");

        var inputs = habits
            .Select(habit => new HabitEmojiInferenceInput(habit.Id, habit.Title, habit.Description))
            .ToList();
        var appliedCount = 0;
        var stopped = false;
        foreach (var chunk in inputs.Chunk(InferenceChunkSize))
        {
            IReadOnlyDictionary<Guid, string>? inferred = null;
            if (inferFromTitle)
            {
                var inferenceResult = await inferenceService.InferAsync(
                    userId,
                    chunk,
                    ct);
                if (inferenceResult.IsFailure)
                {
                    LogInferenceStopped(logger, appliedCount, habits.Count, inferenceResult.Error);
                    stopped = true;
                    break;
                }
                inferred = inferenceResult.Value;
            }

            try
            {
                var chunkIds = chunk.Select(input => input.HabitId).ToHashSet();
                var chunkApplied = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
                {
                    var trackedHabits = await habitRepository.FindTrackedAsync(
                        habit => habit.UserId == userId && chunkIds.Contains(habit.Id),
                        query => query,
                        transactionToken);
                    var attemptApplied = 0;
                    foreach (var habit in trackedHabits)
                    {
                        string? emoji;
                        if (inferFromTitle)
                        {
                            if (!inferred!.TryGetValue(habit.Id, out emoji) || !IsSingleEmojiGrapheme(emoji))
                                continue;
                        }
                        else
                        {
                            emoji = requestedEmoji;
                        }

                        if (ApplyEmoji(habit, emoji).IsSuccess)
                            attemptApplied++;
                    }

                    if (attemptApplied > 0)
                        await unitOfWork.SaveChangesAsync(transactionToken);

                    return attemptApplied;
                }, ct);
                appliedCount += chunkApplied;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unitOfWork.DiscardChanges();
                LogChunkFailed(logger, appliedCount, habits.Count, exception);
                stopped = true;
                break;
            }
        }

        var totalMatched = habits.Count;
        var skippedCount = totalMatched - appliedCount;
        var partial = stopped || skippedCount > 0;
        return BulkUpdateHabitsTool.BuildResult(
            new BulkHabitMutationResult(appliedCount, totalMatched, skippedCount, partial),
            "Updated emojis for",
            includeUpdatedCount: true);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Bulk emoji inference stopped after {AppliedCount} of {TotalMatched} matches: {Reason}")]
    private static partial void LogInferenceStopped(ILogger logger, int appliedCount, int totalMatched, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Bulk emoji update chunk failed after {AppliedCount} of {TotalMatched} matches")]
    private static partial void LogChunkFailed(ILogger logger, int appliedCount, int totalMatched, Exception ex);

    internal static bool IsSingleEmojiGrapheme(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return false;
        if (StringInfo.ParseCombiningCharacters(value).Length != 1)
            return false;

        return value.EnumerateRunes().Any(IsEmojiRune);
    }

    private static bool IsEmojiRune(Rune rune)
    {
        var value = rune.Value;
        return value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
            || value is >= 0x2194 and <= 0x21FF
            || value is >= 0x2300 and <= 0x23FF
            || value is >= 0x2600 and <= 0x27BF
            || value is >= 0x1F000 and <= 0x1FAFF;
    }

    private static Result ApplyEmoji(Habit habit, string? emoji)
    {
        return habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days.ToList(),
            habit.IsBadHabit,
            habit.DueDate,
            DueTime: habit.DueTime,
            DueEndTime: habit.DueEndTime,
            ReminderEnabled: habit.ReminderEnabled,
            ReminderTimes: habit.ReminderTimes,
            SlipAlertEnabled: habit.SlipAlertEnabled,
            ChecklistItems: habit.ChecklistItems,
            IsGeneral: habit.IsGeneral,
            IsFlexible: habit.IsFlexible,
            EndDate: habit.EndDate,
            ScheduledReminders: habit.ScheduledReminders,
            Emoji: emoji,
            IntervalWeeks: habit.IntervalWeeks));
    }
}
