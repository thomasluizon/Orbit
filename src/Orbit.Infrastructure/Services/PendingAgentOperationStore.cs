using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class PendingAgentOperationStore(
    OrbitDbContext dbContext,
    IOptions<AgentPlatformSettings> settings) : IPendingAgentOperationStore
{
    private readonly AgentPlatformSettings _settings = settings.Value;

    public PendingAgentOperation Create(
        Guid userId,
        AgentCapability capability,
        string operationId,
        string argumentsJson,
        string summary,
        string operationFingerprint,
        AgentExecutionSurface surface)
    {
        var existing = dbContext.PendingAgentOperations
            .Where(item =>
                item.UserId == userId &&
                item.CapabilityId == capability.Id &&
                item.OperationFingerprint == operationFingerprint &&
                item.ConsumedAtUtc == null &&
                item.ExpiresAtUtc > DateTime.UtcNow)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault();

        if (existing is not null)
            return Map(existing);

        var entity = PendingAgentOperationState.Create(new PendingAgentOperationStateCreateRequest
        {
            UserId = userId,
            Capability = capability,
            OperationId = operationId,
            ArgumentsJson = argumentsJson,
            Summary = summary,
            OperationFingerprint = operationFingerprint,
            Surface = surface,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.PendingOperationTtlMinutes))
        });

        dbContext.PendingAgentOperations.Add(entity);
        dbContext.SaveChanges();

        return Map(entity);
    }

    public PendingAgentOperationConfirmation? Confirm(Guid userId, Guid pendingOperationId)
    {
        var entity = dbContext.PendingAgentOperations
            .FirstOrDefault(item => item.Id == pendingOperationId && item.UserId == userId);

        if (entity is null || entity.IsExpired(DateTime.UtcNow) || entity.ConsumedAtUtc.HasValue)
            return null;

        var confirmationToken = GenerateToken();
        entity.SetConfirmationTokenHash(HashToken(confirmationToken));
        dbContext.SaveChanges();

        return new PendingAgentOperationConfirmation(entity.Id, confirmationToken, entity.ExpiresAtUtc);
    }

    public PendingAgentOperationExecution? GetExecution(Guid userId, Guid pendingOperationId)
    {
        var entity = dbContext.PendingAgentOperations
            .AsNoTracking()
            .FirstOrDefault(item => item.Id == pendingOperationId && item.UserId == userId);

        if (entity is null || entity.IsExpired(DateTime.UtcNow) || entity.ConsumedAtUtc.HasValue)
            return null;

        var parsedArguments = ParseArguments(entity.ArgumentsJson);

        return new PendingAgentOperationExecution(
            entity.Id,
            entity.CapabilityId,
            entity.OperationId,
            parsedArguments,
            entity.Surface,
            entity.ConfirmationRequirement);
    }

    public bool TryConsumeFreshConfirmation(
        Guid userId,
        string capabilityId,
        string operationFingerprint,
        string confirmationToken,
        bool requireStepUp)
    {
        var tokenHash = HashToken(confirmationToken);
        var entity = dbContext.PendingAgentOperations
            .Where(item =>
                item.UserId == userId &&
                item.CapabilityId == capabilityId &&
                item.OperationFingerprint == operationFingerprint &&
                item.ConfirmationTokenHash == tokenHash)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault();

#pragma warning disable ORBIT0004
        if (entity is null || !entity.IsUsable(capabilityId, operationFingerprint, requireStepUp, DateTime.UtcNow))
#pragma warning restore ORBIT0004
            return false;

        entity.MarkConsumed();
        dbContext.SaveChanges();
        return true;
    }

    private static PendingAgentOperation Map(PendingAgentOperationState entity)
    {
        return new PendingAgentOperation(
            entity.Id,
            entity.CapabilityId,
            entity.DisplayName,
            entity.Summary,
            entity.RiskClass,
            entity.ConfirmationRequirement,
            entity.ExpiresAtUtc);
    }

    private static JsonElement ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return JsonDocument.Parse("{}").RootElement.Clone();

        try
        {
            return JsonDocument.Parse(argumentsJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static string GenerateToken()
    {
        var random = RandomNumberGenerator.GetBytes(24);
        return $"agc_{Convert.ToBase64String(random).Replace("+", "-").Replace("/", "_").TrimEnd('=')}";
    }

    private static string HashToken(string confirmationToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(confirmationToken));
        return Convert.ToHexString(bytes);
    }
}
