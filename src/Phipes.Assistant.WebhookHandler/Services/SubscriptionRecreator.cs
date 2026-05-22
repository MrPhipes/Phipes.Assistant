using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

// Recrea una Graph subscription desde cero usando los parametros guardados en
// RenewerOptions.Subscriptions[]. Se invoca cuando el LifecycleHandler recibe un
// evento subscriptionRemoved y necesita reactivar el flow de notifications sin
// intervencion manual.
public interface ISubscriptionRecreator
{
    Task<SubscriptionRecreated?> RecreateAsync(SubscriptionDefinition def, CancellationToken cancellationToken = default);
}

public sealed record SubscriptionRecreated(string NewId, string Resource, DateTimeOffset ExpirationDateTime, string Label);

[SupportedOSPlatform("windows")]
public sealed class SubscriptionRecreator : ISubscriptionRecreator
{
    private readonly HttpClient _http;
    private readonly IWebhookAppTokenProvider _tokens;
    private readonly WebhookOptions _webhookOptions;
    private readonly EncryptionOptions _encryptionOptions;
    private readonly ILogger<SubscriptionRecreator> _logger;

    public SubscriptionRecreator(
        HttpClient http,
        IWebhookAppTokenProvider tokens,
        IOptions<WebhookOptions> webhookOptions,
        IOptions<EncryptionOptions> encryptionOptions,
        ILogger<SubscriptionRecreator> logger)
    {
        _http = http;
        _tokens = tokens;
        _webhookOptions = webhookOptions.Value;
        _encryptionOptions = encryptionOptions.Value;
        _logger = logger;
    }

    public async Task<SubscriptionRecreated?> RecreateAsync(SubscriptionDefinition def, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(def.Resource))
        {
            FileLog($"NO recrear sub label={def.Label}: Resource vacio en config");
            return null;
        }

        try
        {
            var accessToken = await _tokens.GetAccessTokenAsync(cancellationToken);
            var expirationDateTime = DateTimeOffset.UtcNow.AddMinutes(def.ExpirationMinutes);

            // Cargar el cert para extraer la public key en base64 (formato esperado por Graph
            // en el campo encryptionCertificate).
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                _encryptionOptions.PfxPath,
                _encryptionOptions.PfxPassword,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
            var certPublicBytes = cert.Export(X509ContentType.Cert);
            var encryptionCertificateBase64 = Convert.ToBase64String(certPublicBytes);

            var body = new
            {
                changeType = def.ChangeType,
                notificationUrl = _webhookOptions.NotificationUrl,
                lifecycleNotificationUrl = _webhookOptions.LifecycleNotificationUrl,
                resource = def.Resource,
                expirationDateTime = expirationDateTime.ToString("o"),
                clientState = _webhookOptions.ClientState,
                includeResourceData = true,
                encryptionCertificate = encryptionCertificateBase64,
                encryptionCertificateId = _encryptionOptions.CertificateId
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/subscriptions")
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, cancellationToken);
            var respBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog($"RECREATE FAIL label={def.Label} resource={def.Resource}: HTTP {(int)resp.StatusCode} {Truncate(respBody, 400)}");
                return null;
            }

            var parsed = System.Text.Json.JsonSerializer.Deserialize<SubscriptionResponse>(respBody);
            if (parsed?.Id is null)
            {
                FileLog($"RECREATE FAIL label={def.Label}: respuesta sin id. body={Truncate(respBody, 400)}");
                return null;
            }

            FileLog($"RECREATE OK label={def.Label} newId={parsed.Id} resource={def.Resource} expira={expirationDateTime:o}");
            return new SubscriptionRecreated(parsed.Id, def.Resource, expirationDateTime, def.Label);
        }
        catch (Exception ex)
        {
            FileLog($"RECREATE EXC label={def.Label}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
    private static void FileLog(string message) => FileLogger.Write("Recreator", message);

    private sealed class SubscriptionResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("resource")] public string? Resource { get; set; }
        [JsonPropertyName("expirationDateTime")] public DateTimeOffset ExpirationDateTime { get; set; }
    }
}
