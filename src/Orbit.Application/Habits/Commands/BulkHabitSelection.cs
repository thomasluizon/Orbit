using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public sealed record BulkHabitFilter(
    bool All,
    IReadOnlyList<Guid> HabitIds,
    bool IncludeCompleted = false,
    string? Tag = null,
    string? Search = null,
    bool? IsGeneral = null,
    bool? IsBadHabit = null,
    FrequencyUnit? Frequency = null,
    bool OneTime = false)
{
    public bool HasSelector =>
        All || HabitIds.Count > 0 || !string.IsNullOrWhiteSpace(Tag) || !string.IsNullOrWhiteSpace(Search)
        || IsGeneral.HasValue || IsBadHabit.HasValue || Frequency.HasValue || OneTime;
}

internal static class BulkHabitSelection
{
    public static async Task<IReadOnlyList<Habit>> LoadAsync(
        IGenericRepository<Habit> habitRepository,
        Guid userId,
        BulkHabitFilter filter,
        CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindTrackedAsync(
            habit => habit.UserId == userId && (filter.IncludeCompleted || !habit.IsCompleted),
            query => query.Include(habit => habit.Tags),
            cancellationToken);

        var idSet = filter.HabitIds.ToHashSet();
        var tag = filter.Tag?.Trim();
        var search = filter.Search?.Trim();

        return habits
            .Where(habit => filter.All || idSet.Count == 0 || idSet.Contains(habit.Id))
            .Where(habit => string.IsNullOrWhiteSpace(tag)
                || habit.Tags.Any(candidate => candidate.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            .Where(habit => string.IsNullOrWhiteSpace(search)
                || habit.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || habit.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            .Where(habit => filter.IsGeneral is null || habit.IsGeneral == filter.IsGeneral)
            .Where(habit => filter.IsBadHabit is null || habit.IsBadHabit == filter.IsBadHabit)
            .Where(habit => !filter.OneTime || habit.FrequencyUnit is null)
            .Where(habit => filter.Frequency is null || habit.FrequencyUnit == filter.Frequency)
            .OrderBy(habit => habit.Position ?? int.MaxValue)
            .ThenBy(habit => habit.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
