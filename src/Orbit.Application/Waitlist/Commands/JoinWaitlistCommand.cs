using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Waitlist.Commands;

public record JoinWaitlistCommand(string Email, string Language = "en") : IRequest<Result>;

public class JoinWaitlistCommandHandler(
    IMemoryCache cache,
    IWaitlistConfirmationTokenService tokenService,
    IEmailService emailService,
    IOptions<WaitlistSettings> waitlistOptions) : IRequestHandler<JoinWaitlistCommand, Result>
{
    private readonly WaitlistSettings _settings = waitlistOptions.Value;

    public async Task<Result> Handle(JoinWaitlistCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var cacheKey = $"waitlist:{email}";

        if (cache.TryGetValue(cacheKey, out DateTime lastSentAt) &&
#pragma warning disable ORBIT0004
            (DateTime.UtcNow - lastSentAt).TotalSeconds < 60)
#pragma warning restore ORBIT0004
            return Result.Success();

        var token = tokenService.CreateToken(email, request.Language);
        var confirmUrl = $"{_settings.ApiBaseUrl.TrimEnd('/')}/api/waitlist/confirm?token={Uri.EscapeDataString(token)}";

        await emailService.SendWaitlistConfirmationAsync(email, confirmUrl, request.Language, cancellationToken);

        cache.Set(cacheKey, DateTime.UtcNow, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });

        return Result.Success();
    }
}
