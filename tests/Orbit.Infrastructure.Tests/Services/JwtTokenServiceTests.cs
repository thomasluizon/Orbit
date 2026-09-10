using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class JwtTokenServiceTests
{
    private readonly JwtTokenService _sut;
    private readonly JwtSettings _settings;

    public JwtTokenServiceTests()
    {
        _settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience",
            ExpiryHours = 168,
            ExpiryMinutes = 0
        };

        _sut = new JwtTokenService(Options.Create(_settings));
    }

    [Fact]
    public void GenerateToken_ReturnsNonEmptyString()
    {
        var userId = Guid.NewGuid();
        var email = "test@example.com";

        var token = _sut.GenerateToken(userId, email);

        token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateToken_ContainsUserIdClaim()
    {
        var userId = Guid.NewGuid();
        var email = "test@example.com";

        var token = _sut.GenerateToken(userId, email);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Claims
            .Should().Contain(c =>
                c.Type == ClaimTypes.NameIdentifier && c.Value == userId.ToString());
    }

    [Fact]
    public void GenerateToken_ContainsEmailClaim()
    {
        var userId = Guid.NewGuid();
        var email = "user@orbit.test";

        var token = _sut.GenerateToken(userId, email);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Claims
            .Should().Contain(c =>
                c.Type == ClaimTypes.Email && c.Value == email);
    }

    [Fact]
    public void GenerateToken_OmitsAdminClaim()
    {
        var token = _sut.GenerateToken(Guid.NewGuid(), "user@orbit.test");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Claims.Should().NotContain(c =>
            c.Type.Contains("admin", StringComparison.OrdinalIgnoreCase)
            || c.Value.Contains("admin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateToken_IncludesGuidJtiClaim()
    {
        var token = _sut.GenerateToken(Guid.NewGuid(), "user@orbit.test");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var jti = jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        Guid.TryParse(jti, out _).Should().BeTrue();
    }

    [Fact]
    public void GenerateToken_ProducesDistinctJtiPerToken()
    {
        var handler = new JwtSecurityTokenHandler();

        var firstJti = handler.ReadJwtToken(_sut.GenerateToken(Guid.NewGuid(), "a@orbit.test"))
            .Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        var secondJti = handler.ReadJwtToken(_sut.GenerateToken(Guid.NewGuid(), "b@orbit.test"))
            .Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;

        secondJti.Should().NotBe(firstJti);
    }

    /// <summary>
    /// Pins the token payload used by getUserFromPayload in apps/mobile/stores/auth-store.ts
    /// and OrbitWidgetModule.accountId in the Android widget.
    /// </summary>
    [Fact]
    public void GenerateToken_RawPayloadPreservesClientContract()
    {
        var userId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        const string email = "probe@orbit.test";

        var token = _sut.GenerateToken(userId, email);

        var segments = token.Split('.');
        segments.Should().HaveCount(3);

        var encodedPayload = segments[1]
            .Replace('-', '+')
            .Replace('_', '/');
        var paddingLength = (4 - (encodedPayload.Length % 4)) % 4;
        var payloadBytes = Convert.FromBase64String(
            encodedPayload.PadRight(encodedPayload.Length + paddingLength, '='));

        using var document = JsonDocument.Parse(payloadBytes);
        var properties = document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value);

        properties.Keys.Should().BeEquivalentTo(
        [
            "aud",
            "iss",
            "exp",
            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
            "jti",
            "iat",
            "nbf"
        ]);

        properties["aud"].ValueKind.Should().Be(JsonValueKind.String);
        properties["aud"].GetString().Should().Be(_settings.Audience);
        properties["iss"].ValueKind.Should().Be(JsonValueKind.String);
        properties["iss"].GetString().Should().Be(_settings.Issuer);
        properties["exp"].ValueKind.Should().Be(JsonValueKind.Number);
        properties["exp"].TryGetInt64(out _).Should().BeTrue();
        properties["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"]
            .ValueKind.Should().Be(JsonValueKind.String);
        properties["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"]
            .GetString().Should().Be(userId.ToString());
        properties["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"]
            .ValueKind.Should().Be(JsonValueKind.String);
        properties["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"]
            .GetString().Should().Be(email);
        properties["jti"].ValueKind.Should().Be(JsonValueKind.String);
        Guid.TryParse(properties["jti"].GetString(), out _).Should().BeTrue();
        properties["iat"].ValueKind.Should().Be(JsonValueKind.Number);
        properties["iat"].TryGetInt64(out _).Should().BeTrue();
        properties["nbf"].ValueKind.Should().Be(JsonValueKind.Number);
        properties["nbf"].TryGetInt64(out _).Should().BeTrue();
    }

    [Fact]
    public void GenerateToken_SetsCorrectExpiry()
    {
        var userId = Guid.NewGuid();
        var email = "test@example.com";
        var beforeGeneration = DateTime.UtcNow;

        var token = _sut.GenerateToken(userId, email);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        var expectedExpiry = _settings.ExpiryMinutes > 0
            ? beforeGeneration.AddMinutes(_settings.ExpiryMinutes)
            : beforeGeneration.AddHours(_settings.ExpiryHours);

        jwt.ValidTo.Should().BeCloseTo(expectedExpiry, TimeSpan.FromSeconds(5));
    }
}
