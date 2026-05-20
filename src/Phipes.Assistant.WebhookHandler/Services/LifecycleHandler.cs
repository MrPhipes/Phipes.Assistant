using System.Net.Http.Headers;
using System.Net.Http.Json;
using Phipes.Assistant.WebhookHandler.Models;

namespace Phipes.Assistant.WebhookHandler.Services;

// Procesa notifications que Microsoft Graph manda al lifecycleNotificationUrl.
// Eventos posibles:
//   - reauthorizationRequired: renovar antes que expire (PATCH expirationDateTime).
//   - subscriptionRemoved:     la subscription murio, hay que recrearla (logueo y aviso).
//   - missed:                  Graph perdio una notification; logueamos para auditoria.
public interface ILifecycleHandler
{
    Task HandleAsync(ChangeNotification notification, CancellationToken cancellationToken = default);
}

public sealed class LifecycleHandler : ILifecycleHandler
{
    private readonly HttpClient _http;
    private readonly IWebhookAppTokenProvider _tokens;
    private readonly IAlertManager _alerts;
    private readonly ILogger<LifecycleHandler> _logger;

    // OJO: usamos IWebhookAppTokenProvider (no IGraphTokenProvider). Las subscriptions
    // con includeResourceData=true solo son administrables por la app que las creo
    // (el "applicationId" de la subscription). Como las creo la "Webhook app" dedicada,
    // PATCHearlas con sconnor delegated da 404 ExtensionError. Mismo token-provider que
    // usa SubscriptionRenewer.
    public LifecycleHandler(HttpClient http, IWebhookAppTokenProvider tokens, IAlertManager alerts, ILogger<LifecycleHandler> logger)
    {
        _http = http;
        _tokens = tokens;
        _alerts = alerts;
        _logger = logger;
    }

    public async Task HandleAsync(ChangeNotification notification, CancellationToken cancellationToken = default)
    {
        var ev = notification.LifecycleEvent ?? "(null)";
        var subId = notification.SubscriptionId ?? "(null)";
        FileLog($"event={ev} sub={subId}");

        switch (ev)
        {
            case "reauthorizationRequired":
                await RenewSubscriptionAsync(subId, cancellationToken);
                break;
            case "subscriptionRemoved":
                FileLog($"WARN: subscription {subId} fue removida por Microsoft. Hay que recrearla manualmente.");
                break;
            case "missed":
                FileLog($"WARN: Graph reporta notifications perdidas en sub {subId}");
                break;
            default:
                FileLog($"WARN: evento desconocido '{ev}'");
                break;
        }
    }

    private async Task RenewSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        // Renovamos extendiendo expirationDateTime al maximo que Microsoft permite
        // para esta resource (60 min para /me/chats/getAllMessages).
        var newExpiry = DateTimeOffset.UtcNow.AddMinutes(55).ToString("o");

        var accessToken = await _tokens.GetAccessTokenAsync(cancellationToken);
        var url = $"https://graph.microsoft.com/v1.0/subscriptions/{subscriptionId}";

        using var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(new { expirationDateTime = newExpiry })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (resp.IsSuccessStatusCode)
        {
            FileLog($"RENEW OK sub={subscriptionId} hasta {newExpiry}");
        }
        else
        {
            FileLog($"RENEW FAIL sub={subscriptionId}: HTTP {(int)resp.StatusCode} {body}");
            _alerts.Record(AlertCategory.SubscriptionRenewalFailures,
                $"lifecycle sub={subscriptionId} HTTP {(int)resp.StatusCode}");
            resp.EnsureSuccessStatusCode();
        }
    }

        private static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Lifecycle", message);
}
