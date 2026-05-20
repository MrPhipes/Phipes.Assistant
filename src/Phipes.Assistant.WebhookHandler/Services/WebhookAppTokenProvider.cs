using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

// Token provider para la "Webhook app" propia (Entra app registration con
// Chat.ReadWrite.All admin-consent) bajo cuyo contexto se crean las Graph subscriptions
// con encryption. El SubscriptionRenewer necesita este token (NO el de una public client
// app) porque cada app context ve solo sus propias subscriptions.
public interface IWebhookAppTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed class WebhookAppTokenProvider : IWebhookAppTokenProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly WebhookAppOptions _options;
    private readonly ILogger<WebhookAppTokenProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _cachedAccessToken;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public WebhookAppTokenProvider(
        HttpClient http,
        IOptions<WebhookAppOptions> options,
        ILogger<WebhookAppTokenProvider> logger)
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
            if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return _cachedAccessToken;
            }

            var refreshToken = PsCredentialReader.ReadPassword(_options.RefreshTokenPath);

            var url = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type",    "refresh_token"),
                new KeyValuePair<string, string>("client_id",     _options.ClientId),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("scope",         _options.Scopes),
            });

            using var response = await _http.PostAsync(url, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("WebhookApp OAuth refresh fallo: HTTP {Status} {Body}", (int)response.StatusCode, body);
                response.EnsureSuccessStatusCode();
            }

            var resp = System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
                ?? throw new InvalidOperationException("Respuesta OAuth no parseable");

            _cachedAccessToken = resp.AccessToken
                ?? throw new InvalidOperationException("Microsoft no devolvio access_token");
            _cacheExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, resp.ExpiresIn - 300));

            if (!string.IsNullOrEmpty(resp.RefreshToken) && resp.RefreshToken != refreshToken)
            {
                PsCredentialReader.WritePassword(_options.RefreshTokenPath, _options.UserPrincipalName, resp.RefreshToken);
                _logger.LogInformation("WebhookApp refresh token rotado y persistido");
            }

            _logger.LogInformation("WebhookApp access token refrescado, vigente hasta {Expires}", _cacheExpiresAt);
            return _cachedAccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose() => _refreshLock.Dispose();

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]  public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")]    public int ExpiresIn { get; set; }
    }
}
