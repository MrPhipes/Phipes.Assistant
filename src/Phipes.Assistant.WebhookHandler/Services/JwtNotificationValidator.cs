using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Phipes.Assistant.WebhookHandler.Configuration;

namespace Phipes.Assistant.WebhookHandler.Services;

// Valida los validationTokens que Microsoft Graph incluye en cada notification con
// includeResourceData=true. Cada token es un JWT firmado por Microsoft que prueba que
// la notification proviene legitimamente del backend Graph (no de un atacante que
// descubrio la URL).
//
// Algoritmo:
//   1. Para cada JWT en validationTokens[]:
//      a. Leer header sin verificar para sacar kid + tid.
//      b. Descargar JWKS de https://login.microsoftonline.com/{tid}/discovery/v2.0/keys
//         (con cache de 24h, las keys rotan rara vez).
//      c. Encontrar la JsonWebKey con ese kid.
//      d. Verificar firma RSA-SHA256.
//      e. Validar claims: aud == clientId esperado, iss == endpoint Microsoft del tid,
//         exp no vencido, tid coincide con el tenant esperado.
//   2. Si CUALQUIER token falla la validacion, la notification entera se rechaza.
//
// Modo de operacion: tiene flag "ShadowMode" para empezar logueando warnings sin
// rechazar - asi validamos que la logica esta OK antes de activar rejection.
public interface IJwtNotificationValidator
{
    Task<JwtValidationResult> ValidateTokensAsync(IEnumerable<string> tokens, CancellationToken cancellationToken = default);
}

public sealed record JwtValidationResult(bool IsValid, string? FailureReason);

public sealed class JwtNotificationValidator : IJwtNotificationValidator
{
    private readonly HttpClient _http;
    private readonly WebhookAppOptions _appOptions;
    private readonly ILogger<JwtNotificationValidator> _logger;

    // Cache JWKS por tenant - las keys de Microsoft rotan rara vez (semanas/meses).
    private readonly Dictionary<string, (JsonWebKeySet Keys, DateTimeOffset FetchedAt)> _jwksCache = new();
    private readonly SemaphoreSlim _jwksLock = new(1, 1);
    private static readonly TimeSpan JwksTtl = TimeSpan.FromHours(24);

    public JwtNotificationValidator(
        HttpClient http,
        IOptions<WebhookAppOptions> appOptions,
        ILogger<JwtNotificationValidator> logger)
    {
        _http = http;
        _appOptions = appOptions.Value;
        _logger = logger;
    }

    public async Task<JwtValidationResult> ValidateTokensAsync(IEnumerable<string> tokens, CancellationToken cancellationToken = default)
    {
        var list = tokens?.ToList() ?? new List<string>();
        if (list.Count == 0)
        {
            return new JwtValidationResult(false, "validationTokens vacio o ausente");
        }

        var jwtHandler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        foreach (var token in list)
        {
            JwtSecurityToken jwt;
            try
            {
                jwt = jwtHandler.ReadJwtToken(token);
            }
            catch (Exception ex)
            {
                return new JwtValidationResult(false, $"Token no parseable: {ex.Message}");
            }

            var kid = jwt.Header.Kid;
            var tid = jwt.Payload.TryGetValue("tid", out var tidObj) ? tidObj?.ToString() : null;
            if (string.IsNullOrEmpty(kid) || string.IsNullOrEmpty(tid))
            {
                return new JwtValidationResult(false, $"Token sin kid o tid (kid='{kid}' tid='{tid}')");
            }

            // Hard-check: tid debe coincidir con el tenant configurado.
            if (!string.Equals(tid, _appOptions.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                return new JwtValidationResult(false, $"tid={tid} no coincide con expected {_appOptions.TenantId}");
            }

            var jwks = await GetJwksAsync(tid, cancellationToken);
            var key = jwks.Keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));
            if (key is null)
            {
                // Refresh JWKS forzado por si rotaron y el cache esta stale.
                jwks = await GetJwksAsync(tid, cancellationToken, forceRefresh: true);
                key = jwks.Keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));
                if (key is null)
                {
                    return new JwtValidationResult(false, $"kid={kid} no encontrado en JWKS de tid={tid}");
                }
            }

            // Microsoft Graph emite tokens con iss v2.0 normalmente, pero ha habido casos
            // historicos con sts.windows.net. Aceptamos ambos.
            var validIssuers = new[]
            {
                $"https://login.microsoftonline.com/{tid}/v2.0",
                $"https://sts.windows.net/{tid}/",
            };

            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuers = validIssuers,
                ValidateAudience = true,
                ValidAudience = _appOptions.ClientId,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
            };

            try
            {
                jwtHandler.ValidateToken(token, parameters, out _);
            }
            catch (SecurityTokenException ex)
            {
                return new JwtValidationResult(false, $"Firma/claims invalidos: {ex.Message}");
            }
            catch (Exception ex)
            {
                return new JwtValidationResult(false, $"Error validando: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new JwtValidationResult(true, null);
    }

    private async Task<JsonWebKeySet> GetJwksAsync(string tenantId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        await _jwksLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh
                && _jwksCache.TryGetValue(tenantId, out var cached)
                && DateTimeOffset.UtcNow - cached.FetchedAt < JwksTtl)
            {
                return cached.Keys;
            }

            var url = $"https://login.microsoftonline.com/{tenantId}/discovery/v2.0/keys";
            var json = await _http.GetStringAsync(url, cancellationToken);
            var keySet = new JsonWebKeySet(json);
            _jwksCache[tenantId] = (keySet, DateTimeOffset.UtcNow);
            _logger.LogInformation("JWKS cargado para tenant {Tid}: {Count} keys", tenantId, keySet.Keys.Count);
            return keySet;
        }
        finally
        {
            _jwksLock.Release();
        }
    }
}
