using System.Security.Cryptography;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Commands;

public record SendCodeCommand(string Email, string Language = "en")
    : IRequest<Result>;

public record VerificationEntry(string Code, int Attempts, DateTime CreatedAt);

public class SendCodeCommandHandler(
    IMemoryCache cache,
    IBackgroundJobClient backgroundJobClient) : IRequestHandler<SendCodeCommand, Result>
{
    private const int SmokeCodeLength = 6;

    public Task<Result> Handle(SendCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var cacheKey = $"verify:{email}";

        var aspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isProduction = string.Equals(aspNetEnv, "Production", StringComparison.OrdinalIgnoreCase);

        if (isProduction)
        {
            if (TrySeedProductionSmokeCode(email, cacheKey))
                return Task.FromResult(Result.Success());
        }
        else if (TrySeedTestAccountCode(email, cacheKey))
        {
            return Task.FromResult(Result.Success());
        }

        if (cache.TryGetValue(cacheKey, out VerificationEntry? existing) && existing is not null)
        {
#pragma warning disable ORBIT0004
            var elapsed = DateTime.UtcNow - existing.CreatedAt;
#pragma warning restore ORBIT0004
            if (elapsed.TotalSeconds < 60)
                return Task.FromResult(Result.Failure(ErrorMessages.CodeRequestCooldown));
        }

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

#pragma warning disable ORBIT0004
        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
#pragma warning restore ORBIT0004
        cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });

        backgroundJobClient.Enqueue<SendVerificationCodeEmailJob>(
            job => job.ExecuteAsync(email, code, request.Language));

        return Task.FromResult(Result.Success());
    }

    private bool TrySeedTestAccountCode(string email, string cacheKey)
    {
        var testAccountsEnv = Environment.GetEnvironmentVariable("TEST_ACCOUNTS");
        if (string.IsNullOrEmpty(testAccountsEnv))
            return false;

        foreach (var pair in testAccountsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':', 2);
            if (parts.Length != 2 || !string.Equals(parts[0].Trim(), email, StringComparison.OrdinalIgnoreCase))
                continue;

#pragma warning disable ORBIT0004
            var entry = new VerificationEntry(parts[1].Trim(), 0, DateTime.UtcNow);
#pragma warning restore ORBIT0004
            cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });
            return true;
        }

        return false;
    }

    private bool TrySeedProductionSmokeCode(string email, string cacheKey)
    {
        var smokeEmail = Environment.GetEnvironmentVariable("SMOKE_TEST_EMAIL");
        var smokeCode = Environment.GetEnvironmentVariable("SMOKE_TEST_CODE");

        if (string.IsNullOrEmpty(smokeEmail) || string.IsNullOrEmpty(smokeCode))
            return false;

        if (smokeCode.Length != SmokeCodeLength || !smokeCode.All(char.IsDigit))
            return false;

        if (!string.Equals(smokeEmail.Trim(), email, StringComparison.OrdinalIgnoreCase))
            return false;

#pragma warning disable ORBIT0004
        var entry = new VerificationEntry(smokeCode, 0, DateTime.UtcNow);
#pragma warning restore ORBIT0004
        cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
        });

        return true;
    }
}
