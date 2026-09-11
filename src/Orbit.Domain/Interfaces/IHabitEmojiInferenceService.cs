using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public sealed record HabitEmojiInferenceInput(
    Guid HabitId,
    string Title,
    string? Description);

public interface IHabitEmojiInferenceService
{
    Task<Result<IReadOnlyDictionary<Guid, string>>> InferAsync(
        Guid userId,
        IReadOnlyList<HabitEmojiInferenceInput> habits,
        CancellationToken cancellationToken = default);
}
