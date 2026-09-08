using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class AgentStepUpService(
    OrbitDbContext dbContext,
    IEmailService emailService,
    IOptions<AgentPlatformSettings> settings) : IAgentStepUpService
{
    private readonly AgentPlatformSettings _settings = settings.Value;

    public async Task<Result<AgentStepUpChallenge>> IssueChallengeAsync(
        Guid userId,
        Guid pendingOperationId,
        string language,
        CancellationToken cancellationToken = default)
    {
        var pendingOperation = await dbContext.PendingAgentOperations
            .FirstOrDefaultAsync(
                item => item.Id == pendingOperationId && item.UserId == userId,
                cancellationToken);

        if (pendingOperation is null || pendingOperation.IsExpired(DateTime.UtcNow) || pendingOperation.ConsumedAtUtc.HasValue)
            return Result.Failure<AgentStepUpChallenge>(ErrorMessages.PendingOperationNotFound);

        if (pendingOperation.ConfirmationRequirement != AgentConfirmationRequirement.StepUp)
            return Result.Failure<AgentStepUpChallenge>(ErrorMessages.StepUpNotRequired);

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);

        if (user is null)
            return Result.Failure<AgentStepUpChallenge>(ErrorMessages.UserNotFound);

#pragma warning disable ORBIT0004
        var cooldownBoundary = DateTime.UtcNow.AddSeconds(-Math.Max(1, _settings.StepUpChallengeCooldownSeconds));
#pragma warning restore ORBIT0004
        var recentChallenge = await dbContext.AgentStepUpChallenges
            .AsNoTracking()
            .Where(item =>
                item.UserId == userId &&
                item.PendingOperationId == pendingOperationId &&
                item.CreatedAtUtc >= cooldownBoundary &&
                item.VerifiedAtUtc == null &&
                item.ExpiresAtUtc > DateTime.UtcNow)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (recentChallenge is not null)
            return Result.Failure<AgentStepUpChallenge>(ErrorMessages.StepUpCooldown);

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var challenge = AgentStepUpChallengeState.Create(
            userId,
            pendingOperationId,
            HashToken(code),
#pragma warning disable ORBIT0004
            DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.StepUpChallengeTtlMinutes)));
#pragma warning restore ORBIT0004

        await emailService.SendVerificationCodeAsync(
            user.Email,
            code,
            string.IsNullOrWhiteSpace(language) ? "en" : language,
            cancellationToken);

        await dbContext.AgentStepUpChallenges.AddAsync(challenge, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new AgentStepUpChallenge(
            challenge.Id,
            pendingOperationId,
            challenge.ExpiresAtUtc));
    }

    public async Task<Result<PendingAgentOperation>> VerifyChallengeAsync(
        Guid userId,
        Guid pendingOperationId,
        Guid challengeId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var challenge = await dbContext.AgentStepUpChallenges
            .FirstOrDefaultAsync(
                item =>
                    item.Id == challengeId &&
                    item.UserId == userId &&
                    item.PendingOperationId == pendingOperationId,
                cancellationToken);

#pragma warning disable ORBIT0004
        if (challenge is null || !challenge.CanVerify(_settings.StepUpMaxAttempts, DateTime.UtcNow))
#pragma warning restore ORBIT0004
            return Result.Failure<PendingAgentOperation>(ErrorMessages.StepUpChallengeNotFound);

        if (!MatchesHash(challenge.CodeHash, code))
        {
            challenge.RecordFailedAttempt();
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result.Failure<PendingAgentOperation>(ErrorMessages.InvalidStepUpCode);
        }

        var pendingOperation = await dbContext.PendingAgentOperations
            .FirstOrDefaultAsync(
                item => item.Id == pendingOperationId && item.UserId == userId,
                cancellationToken);

        if (pendingOperation is null || pendingOperation.IsExpired(DateTime.UtcNow) || pendingOperation.ConsumedAtUtc.HasValue)
            return Result.Failure<PendingAgentOperation>(ErrorMessages.PendingOperationNotFound);

        challenge.MarkVerified();
        pendingOperation.MarkStepUpSatisfied();
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new PendingAgentOperation(
            pendingOperation.Id,
            pendingOperation.CapabilityId,
            pendingOperation.DisplayName,
            pendingOperation.Summary,
            pendingOperation.RiskClass,
            pendingOperation.ConfirmationRequirement,
            pendingOperation.ExpiresAtUtc));
    }

    private static string HashToken(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private static bool MatchesHash(string expectedHash, string code)
    {
        var actualHash = HashToken(code);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedHash),
            Encoding.UTF8.GetBytes(actualHash));
    }
}
