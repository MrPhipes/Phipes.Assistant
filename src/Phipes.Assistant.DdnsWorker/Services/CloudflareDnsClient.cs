using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phipes.Assistant.DdnsWorker.Configuration;
using Phipes.Assistant.DdnsWorker.Utilities;

namespace Phipes.Assistant.DdnsWorker.Services;

public sealed class CloudflareDnsClient
{
    private readonly HttpClient _http;
    private readonly CloudflareOptions _options;
    private readonly ILogger<CloudflareDnsClient> _logger;
    private bool _authConfigured;

    public CloudflareDnsClient(
        HttpClient http,
        IOptions<DdnsOptions> options,
        ILogger<CloudflareDnsClient> logger)
    {
        _http = http;
        _options = options.Value.Cloudflare;
        _logger = logger;
    }

    /// <summary>
    /// Obtiene el contenido actual del A record.
    /// </summary>
    public async Task<string?> GetRecordContentAsync(ManagedRecord record, CancellationToken cancellationToken)
    {
        EnsureAuthHeader();

        var path = $"zones/{record.ZoneId}/dns_records/{record.RecordId}";
        var response = await _http.GetFromJsonAsync<CloudflareEnvelope<DnsRecord>>(
            path, cancellationToken);

        if (response is null || !response.Success || response.Result is null)
        {
            _logger.LogError("Cloudflare GET {Path} no devolvió result válido. errors={Errors}",
                path, string.Join("; ", response?.Errors ?? []));
            return null;
        }

        return response.Result.Content;
    }

    /// <summary>
    /// Actualiza el contenido del A record. Devuelve true si tuvo éxito.
    /// </summary>
    public async Task<bool> UpdateRecordContentAsync(
        ManagedRecord record,
        string newContent,
        CancellationToken cancellationToken)
    {
        EnsureAuthHeader();

        var path = $"zones/{record.ZoneId}/dns_records/{record.RecordId}";
        var body = new DnsRecordPatch(newContent, record.Proxied);

        using var response = await _http.PatchAsJsonAsync(path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Cloudflare PATCH {Path} falló: {Status} {Body}",
                path, (int)response.StatusCode, errorBody);
            return false;
        }

        var envelope = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<DnsRecord>>(
            cancellationToken: cancellationToken);

        if (envelope is null || !envelope.Success)
        {
            _logger.LogError("Cloudflare PATCH {Path} respondió success=false. errors={Errors}",
                path, string.Join("; ", envelope?.Errors ?? []));
            return false;
        }

        return true;
    }

    private void EnsureAuthHeader()
    {
        if (_authConfigured) return;

        var token = PsCredentialReader.ReadPassword(_options.ApiTokenPath);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _authConfigured = true;
    }

    private sealed record CloudflareEnvelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("errors")] IReadOnlyList<CloudflareError> Errors,
        [property: JsonPropertyName("result")] T? Result);

    private sealed record CloudflareError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message)
    {
        public override string ToString() => $"{Code}: {Message}";
    }

    private sealed record DnsRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("proxied")] bool Proxied);

    private sealed record DnsRecordPatch(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("proxied")] bool Proxied);
}
