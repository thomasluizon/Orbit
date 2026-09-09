using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public class JwtTokenService(IOptions<JwtSettings> options) : ITokenService
{
    private readonly JwtSettings _settings = options.Value;

    public string GenerateToken(Guid userId, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
#pragma warning disable ORBIT0004
            Expires = DateTime.UtcNow.Add(
#pragma warning restore ORBIT0004
                _settings.ExpiryMinutes > 0
                    ? TimeSpan.FromMinutes(_settings.ExpiryMinutes)
                    : TimeSpan.FromHours(_settings.ExpiryHours)),
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }
}
