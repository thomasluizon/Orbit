using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orbit.Api.OAuth;

public sealed partial class OAuthAuthorizationStore : IDisposable
{
    private readonly ConcurrentDictionary<string, AuthorizationEntry> _codes = new();
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan CodeExpiry = TimeSpan.FromMinutes(5);

    public OAuthAuthorizationStore(ILogger<OAuthAuthorizationStore> logger)
    {
        _cleanupTimer = new Timer(_ =>
        {
            try { Cleanup(); }
            catch (Exception ex) { LogCleanupFailed(logger, ex); }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public string CreateCode(Guid userId, string codeChallenge, string redirectUri, string clientId, string? nonce = null)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

#pragma warning disable ORBIT0004
        var entry = new AuthorizationEntry(userId, codeChallenge, redirectUri, clientId, nonce, DateTime.UtcNow);
#pragma warning restore ORBIT0004
        _codes[code] = entry;
        return code;
    }

    public AuthorizationEntry? ExchangeCode(string code, string codeVerifier, string redirectUri)
    {
        if (!_codes.TryRemove(code, out var entry))
            return null;

#pragma warning disable ORBIT0004
        if (DateTime.UtcNow - entry.CreatedAt > CodeExpiry)
#pragma warning restore ORBIT0004
            return null;

        if (entry.RedirectUri != redirectUri)
            return null;

        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var computed = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        if (computed != entry.CodeChallenge)
            return null;

        return entry;
    }

    private void Cleanup()
    {
#pragma warning disable ORBIT0004
        var cutoff = DateTime.UtcNow - CodeExpiry;
#pragma warning restore ORBIT0004
        foreach (var kvp in _codes)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _codes.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "OAuth authorization store cleanup failed")]
    private static partial void LogCleanupFailed(ILogger logger, Exception ex);
}

public record AuthorizationEntry(
    Guid UserId,
    string CodeChallenge,
    string RedirectUri,
    string ClientId,
    string? Nonce,
    DateTime CreatedAt);
