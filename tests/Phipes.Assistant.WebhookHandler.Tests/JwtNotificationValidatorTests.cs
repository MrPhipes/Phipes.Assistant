using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

// Tests del JwtNotificationValidator: inyectamos un HttpMessageHandler mockeado para que
// devuelva un JWKS controlado, asi podemos probar el flujo end-to-end sin tocar internet.
public sealed class JwtNotificationValidatorTests
{
    private const string TestTenantId = "12345678-1234-1234-1234-123456789012";
    private const string TestClientId = "test-app-client-id";
    private const string TestKid = "test-key-id";

    private readonly WebhookAppOptions _appOptions = new()
    {
        TenantId = TestTenantId,
        ClientId = TestClientId,
        RefreshTokenPath = "ignored",
        UserPrincipalName = "test@example.com"
    };

    [Fact]
    public async Task ReturnsInvalid_WhenTokensEmpty()
    {
        var validator = CreateValidator(jwksJson: "{\"keys\":[]}");
        var result = await validator.ValidateTokensAsync(new List<string>());
        Assert.False(result.IsValid);
        Assert.Contains("vacio", result.FailureReason ?? "");
    }

    [Fact]
    public async Task ReturnsInvalid_WhenTokenUnparseable()
    {
        var validator = CreateValidator(jwksJson: "{\"keys\":[]}");
        var result = await validator.ValidateTokensAsync(new[] { "not-a-jwt" });
        Assert.False(result.IsValid);
        Assert.Contains("no parseable", result.FailureReason ?? "");
    }

    [Fact]
    public async Task ReturnsInvalid_WhenKidOrTidMissing()
    {
        var validator = CreateValidator(jwksJson: "{\"keys\":[]}");
        // JWT sin kid en header ni tid en payload
        var bogusJwt = BuildHs256JwtNoKid();
        var result = await validator.ValidateTokensAsync(new[] { bogusJwt });
        Assert.False(result.IsValid);
        Assert.Contains("kid", result.FailureReason ?? "");
    }

    [Fact]
    public async Task ReturnsInvalid_WhenTidDoesNotMatch()
    {
        using var rsa = RSA.Create(2048);
        var jwt = BuildRsaJwt(rsa, audience: TestClientId, tid: "wrong-tenant-id");
        var jwks = BuildJwks(rsa);

        var validator = CreateValidator(jwks);
        var result = await validator.ValidateTokensAsync(new[] { jwt });

        Assert.False(result.IsValid);
        Assert.Contains("tid", result.FailureReason ?? "");
    }

    [Fact]
    public async Task ReturnsValid_WhenTokenSignedAndClaimsMatch()
    {
        using var rsa = RSA.Create(2048);
        var jwt = BuildRsaJwt(rsa, audience: TestClientId, tid: TestTenantId);
        var jwks = BuildJwks(rsa);

        var validator = CreateValidator(jwks);
        var result = await validator.ValidateTokensAsync(new[] { jwt });

        Assert.True(result.IsValid, $"Expected valid but got: {result.FailureReason}");
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task ReturnsInvalid_WhenSignatureFromDifferentKey()
    {
        using var rsaSigner = RSA.Create(2048);
        using var rsaPublished = RSA.Create(2048);
        var jwt = BuildRsaJwt(rsaSigner, audience: TestClientId, tid: TestTenantId);
        // JWKS publica otra public key con el mismo kid -> firma no verifica
        var jwks = BuildJwks(rsaPublished);

        var validator = CreateValidator(jwks);
        var result = await validator.ValidateTokensAsync(new[] { jwt });

        Assert.False(result.IsValid);
        Assert.Contains("invalidos", result.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsInvalid_WhenAudienceWrong()
    {
        using var rsa = RSA.Create(2048);
        var jwt = BuildRsaJwt(rsa, audience: "other-audience", tid: TestTenantId);
        var jwks = BuildJwks(rsa);

        var validator = CreateValidator(jwks);
        var result = await validator.ValidateTokensAsync(new[] { jwt });

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ReturnsInvalid_WhenTokenExpired()
    {
        using var rsa = RSA.Create(2048);
        var jwt = BuildRsaJwt(rsa,
            audience: TestClientId,
            tid: TestTenantId,
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1));
        var jwks = BuildJwks(rsa);

        var validator = CreateValidator(jwks);
        var result = await validator.ValidateTokensAsync(new[] { jwt });

        Assert.False(result.IsValid);
    }

    // === Helpers ===

    private JwtNotificationValidator CreateValidator(string jwksJson)
    {
        var handler = new StubHandler(jwksJson);
        var http = new HttpClient(handler);
        return new JwtNotificationValidator(http, Options.Create(_appOptions), NullLogger<JwtNotificationValidator>.Instance);
    }

    private static string BuildJwks(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        var jwk = new
        {
            kty = "RSA",
            kid = TestKid,
            use = "sig",
            alg = "RS256",
            n = Base64UrlEncoder.Encode(parameters.Modulus!),
            e = Base64UrlEncoder.Encode(parameters.Exponent!)
        };
        var doc = new { keys = new[] { jwk } };
        return JsonSerializer.Serialize(doc);
    }

    private static string BuildRsaJwt(
        RSA rsa,
        string audience,
        string tid,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        var nbf = notBefore ?? DateTime.UtcNow.AddMinutes(-5);
        var exp = expires ?? DateTime.UtcNow.AddHours(1);

        var key = new RsaSecurityKey(rsa) { KeyId = TestKid };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var jwt = new JwtSecurityToken(
            issuer: $"https://login.microsoftonline.com/{tid}/v2.0",
            audience: audience,
            claims: new[] { new Claim("tid", tid) },
            notBefore: nbf,
            expires: exp,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static string BuildHs256JwtNoKid()
    {
        // HMAC JWT sin kid en header ni tid en payload
        var keyBytes = Encoding.UTF8.GetBytes("a-256-bit-secret-key-for-hmac-jwts-x");
        var key = new SymmetricSecurityKey(keyBytes);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            claims: new[] { new Claim("sub", "test") },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _jwksJson;
        public StubHandler(string jwksJson) => _jwksJson = jwksJson;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jwksJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }
}
