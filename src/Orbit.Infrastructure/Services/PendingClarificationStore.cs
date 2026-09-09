using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class PendingClarificationStore(OrbitDbContext dbContext) : IPendingClarificationStore
{
    private static readonly JsonSerializerOptions QuickActionDeserializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<Guid> CreateAsync(
        Guid userId,
        string toolName,
        string partialArgumentsJson,
        string missingArgumentKey,
        string question,
        string quickActionsJson,
        CancellationToken cancellationToken = default)
    {
        var entity = PendingClarification.Create(
            userId,
            toolName,
            partialArgumentsJson,
            missingArgumentKey,
            question,
            quickActionsJson,
#pragma warning disable ORBIT0004
            DateTime.UtcNow.AddMinutes(AppConstants.PendingClarificationTtlMinutes));
#pragma warning restore ORBIT0004

        dbContext.PendingClarifications.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task<PendingClarificationData?> GetForResolutionAsync(
        Guid operationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable ORBIT0004
        var now = DateTime.UtcNow;
#pragma warning restore ORBIT0004
        var entity = await dbContext.PendingClarifications
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == operationId
                    && item.UserId == userId
                    && item.ResolvedAtUtc == null
                    && item.ExpiresAtUtc > now,
                cancellationToken);

        if (entity is null)
            return null;

        return new PendingClarificationData(
            entity.ToolName,
            entity.PartialArgumentsJson,
            entity.MissingArgumentKey,
            ExtractQuickActionValues(entity.QuickActionsJson),
            entity.ExpiresAtUtc);
    }

    public async Task<bool> MarkResolvedAsync(
        Guid operationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.PendingClarifications
            .Where(item =>
                item.Id == operationId &&
                item.UserId == userId &&
                item.ResolvedAtUtc == null &&
                item.ExpiresAtUtc > DateTime.UtcNow)
            .ExecuteUpdateAsync(
                setter => setter.SetProperty(item => item.ResolvedAtUtc, DateTime.UtcNow),
                cancellationToken);

        return rows > 0;
    }

    private static IReadOnlyList<string> ExtractQuickActionValues(string quickActionsJson)
    {
        if (string.IsNullOrWhiteSpace(quickActionsJson))
            return Array.Empty<string>();

        try
        {
            var actions = JsonSerializer.Deserialize<List<QuickAction>>(quickActionsJson, QuickActionDeserializerOptions);
            if (actions is null) return Array.Empty<string>();

            return actions
                .Where(action => !string.IsNullOrEmpty(action?.Value))
                .Select(action => action.Value)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
