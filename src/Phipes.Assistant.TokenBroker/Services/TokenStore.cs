using Microsoft.Extensions.Options;
using Phipes.Assistant.TokenBroker.Configuration;
using Phipes.Assistant.TokenBroker.Utilities;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Phipes.Assistant.TokenBroker.Services;

// State machine del broker:
//   - Lee cred.xml DPAPI al arrancar (mantiene RT en memoria).
//   - Cuando alguien pide AT y el cache vence en menos del margen, refresh.
//   - Refresh hace POST /token con grant_type=refresh_token. Si Microsoft
//     devuelve un nuevo RT, lo persiste atomico al disco.
//   - Mutex interno garantiza UNA sola rotación a la vez (evita race condition
//     entre clientes concurrentes que tienen el RT viejo).
//
// SOLO svc-token-broker tiene acceso al cred.xml por ACL — esto es por lo que
// vale la pena el proceso separado.
[SupportedOSPlatform("windows")]
public sealed class TokenStore : IDisposable
{
    private readonly BrokerOptions _options;
    private readonly ILogger<TokenStore> _logger;
    private readonly IHttpClientFactory _httpFactory;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string _currentRefreshToken;
    private readonly string _userName;
    private string? _cachedAccessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.MinValue;
    private int _refreshCount;
    private string? _lastError;

    public TokenStore(IOptions<BrokerOptions> options, ILogger<TokenStore> logger, IHttpClientFactory httpFactory)
    {
        _options = options.Value;
        _logger = logger;
        _httpFactory = httpFactory;

        if (string.IsNullOrWhiteSpace(_options.CredXmlPath))
            throw new InvalidOperationException("Broker:CredXmlPath no configurado");

        if (!File.Exists(_options.CredXmlPath))
            throw new FileNotFoundException($"cred.xml no existe: {_options.CredXmlPath}");

        _currentRefreshToken = CredXmlIo.ReadRefreshToken(_options.CredXmlPath);
        _userName = CredXmlIo.ReadUserName(_options.CredXmlPath);
        _logger.LogInformation("TokenStore inicializado. cred.xml LastWrite={lw}",
            File.GetLastWriteTimeUtc(_options.CredXmlPath));
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Cache hit si vence en más del margen
        if (_cachedAccessToken is not null &&
            _accessTokenExpiresAt - DateTimeOffset.UtcNow > _options.AccessTokenCacheMargin)
        {
            return _cachedAccessToken;
        }
        await RefreshAsync(ct);
        return _cachedAccessToken!;
    }

    // Refresh forzado (sin importar cache) — útil para /refresh endpoint.
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Double-check: si otro hilo ya refreshó mientras esperabamos
            if (_cachedAccessToken is not null &&
                _accessTokenExpiresAt - DateTimeOffset.UtcNow > _options.AccessTokenCacheMargin)
            {
                return;
            }

            var http = _httpFactory.CreateClient();
            var tokenUrl = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _options.ClientId,
                ["refresh_token"] = _currentRefreshToken,
                ["scope"] = _options.Scope,
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = form };
            using var resp = await http.SendAsync(req, ct);
            var bodyJson = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _lastError = $"HTTP {(int)resp.StatusCode} {bodyJson}";
                _logger.LogError("Refresh FAIL: {err}", _lastError);
                throw new InvalidOperationException(_lastError);
            }

            using var doc = JsonDocument.Parse(bodyJson);
            var root = doc.RootElement;
            _cachedAccessToken = root.GetProperty("access_token").GetString()!;
            var expiresIn = root.GetProperty("expires_in").GetInt32();
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            // Rotación del RT: Microsoft puede devolver uno nuevo. Si es distinto,
            // persistir al disco atómico.
            if (root.TryGetProperty("refresh_token", out var rtElem))
            {
                var newRt = rtElem.GetString();
                if (!string.IsNullOrEmpty(newRt) && newRt != _currentRefreshToken)
                {
                    CredXmlIo.WriteRefreshToken(_options.CredXmlPath, _userName, newRt);
                    _currentRefreshToken = newRt;
                    _logger.LogInformation("RT rotado y persistido");
                }
            }

            _lastRefreshAt = DateTimeOffset.UtcNow;
            _refreshCount++;
            _lastError = null;
            _logger.LogInformation("Refresh OK count={c} expires_in={s}s", _refreshCount, expiresIn);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public HealthStatus GetHealth()
    {
        var rtMtime = File.Exists(_options.CredXmlPath)
            ? File.GetLastWriteTimeUtc(_options.CredXmlPath)
            : (DateTime?)null;

        return new HealthStatus(
            CredXmlExists: File.Exists(_options.CredXmlPath),
            CredXmlLastWriteUtc: rtMtime,
            LastRefreshUtc: _lastRefreshAt == DateTimeOffset.MinValue ? null : _lastRefreshAt,
            AccessTokenExpiresUtc: _accessTokenExpiresAt == DateTimeOffset.MinValue ? null : _accessTokenExpiresAt,
            RefreshCount: _refreshCount,
            LastError: _lastError
        );
    }

    public void Dispose() => _refreshLock.Dispose();
}

public sealed record HealthStatus(
    bool CredXmlExists,
    DateTime? CredXmlLastWriteUtc,
    DateTimeOffset? LastRefreshUtc,
    DateTimeOffset? AccessTokenExpiresUtc,
    int RefreshCount,
    string? LastError
);
