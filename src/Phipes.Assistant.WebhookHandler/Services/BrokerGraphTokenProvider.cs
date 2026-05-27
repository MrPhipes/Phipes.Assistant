using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;

namespace Phipes.Assistant.WebhookHandler.Services;

// Implementación de IGraphTokenProvider que delega al servicio TokenBroker
// local. El broker corre como svc-token-broker (otro user) y maneja el cred.xml
// con DPAPI propio. Aunque Sarah o el handler intenten leer el cred.xml, ACL lo
// niega. Solo el broker tiene acceso.
//
// Si el broker está down, el handler degrada gracefully — el caller recibe
// excepción y reintenta (mismo comportamiento que tenía con red flap a Microsoft).
public sealed class BrokerGraphTokenProvider : IGraphTokenProvider
{
    private readonly HttpClient _http;
    private readonly BrokerClientOptions _options;
    private readonly ILogger<BrokerGraphTokenProvider> _logger;

    // Cache cliente-side ligero: 30s. Si el broker rota el AT, en peor caso
    // perdemos 30s de freshness. Para Token-cache real, el broker es la
    // fuente de verdad.
    private string? _cachedAt;
    private DateTimeOffset _cacheValidUntil = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BrokerGraphTokenProvider(
        HttpClient http,
        IOptions<BrokerClientOptions> options,
        ILogger<BrokerGraphTokenProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.ListenUrl))
            throw new InvalidOperationException("Broker:ListenUrl no configurado");
        if (string.IsNullOrWhiteSpace(_options.Secret))
            throw new InvalidOperationException("Broker:Secret no configurado");
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedAt is not null && DateTimeOffset.UtcNow < _cacheValidUntil)
        {
            return _cachedAt;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedAt is not null && DateTimeOffset.UtcNow < _cacheValidUntil)
                return _cachedAt;

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_options.ListenUrl.TrimEnd('/')}/token");
            req.Headers.Add("X-Broker-Secret", _options.Secret);

            using var resp = await _http.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Broker /token FAIL HTTP {Code} {Body}", (int)resp.StatusCode, body);
                throw new InvalidOperationException($"Broker /token devolvio HTTP {(int)resp.StatusCode}: {body}");
            }

            var parsed = System.Text.Json.JsonSerializer.Deserialize<BrokerTokenResponse>(body)
                ?? throw new InvalidOperationException("Broker devolvio JSON no parseable");

            if (string.IsNullOrEmpty(parsed.AccessToken))
                throw new InvalidOperationException("Broker devolvio access_token vacio");

            _cachedAt = parsed.AccessToken;
            // Cache local 30s; el broker maneja el cache real contra Microsoft.
            _cacheValidUntil = DateTimeOffset.UtcNow.AddSeconds(30);
            return _cachedAt;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed class BrokerTokenResponse
    {
        [JsonPropertyName("access_token")]    public string? AccessToken { get; set; }
        [JsonPropertyName("expires_at_utc")]  public string? ExpiresAtUtc { get; set; }
    }
}
