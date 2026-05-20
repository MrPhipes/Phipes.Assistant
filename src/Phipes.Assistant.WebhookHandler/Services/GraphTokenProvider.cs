using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

[SupportedOSPlatform("windows")]
public sealed class GraphTokenProvider : IGraphTokenProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly GraphOptions _options;
    private readonly ILogger<GraphTokenProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Cache del access token. Microsoft suele dar tokens de 60-90 min; refrescamos
    // proactivamente 5 min antes para evitar el caso borde donde expire en pleno uso.
    private string? _cachedAccessToken;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public GraphTokenProvider(
        HttpClient http,
        IOptions<GraphOptions> options,
        ILogger<GraphTokenProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
        {
            return _cachedAccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check: otro thread pudo haber refrescado mientras esperábamos.
            if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return _cachedAccessToken;
            }

            var refreshToken = PsCredentialReader.ReadPassword(_options.RefreshTokenPath);
            var resp = await ExchangeRefreshTokenAsync(refreshToken, cancellationToken);

            _cachedAccessToken = resp.AccessToken
                ?? throw new InvalidOperationException("Microsoft no devolvió access_token");
            _cacheExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, resp.ExpiresIn - 300));

            // Si Microsoft devolvió un nuevo refresh token, persistimos rotando el XML.
            if (!string.IsNullOrEmpty(resp.RefreshToken) && resp.RefreshToken != refreshToken)
            {
                PsCredentialReader.WritePassword(_options.RefreshTokenPath, _options.UserPrincipalName, resp.RefreshToken);
                _logger.LogInformation("Refresh token rotado y persistido");
            }

            _logger.LogInformation("Access token refrescado, vigente hasta {Expires}", _cacheExpiresAt);
            return _cachedAccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<TokenResponse> ExchangeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var url = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type",   "refresh_token"),
            new KeyValuePair<string, string>("client_id",    _options.ClientId),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("scope",        _options.Scopes),
        });

        using var response = await _http.PostAsync(url, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OAuth refresh falló: HTTP {Status} {Body}", (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        return System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new InvalidOperationException("Respuesta OAuth no parseable");
    }

    public void Dispose() => _refreshLock.Dispose();

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]  public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")]    public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")]    public string? TokenType { get; set; }
    }
}
