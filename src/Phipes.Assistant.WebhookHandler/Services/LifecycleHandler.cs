using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
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
    private readonly ISubscriptionRecreator _recreator;
    private readonly ISubscriptionStateStore _stateStore;
    private readonly RenewerOptions _renewerOptions;
    private readonly ILogger<LifecycleHandler> _logger;

    // OJO: usamos IWebhookAppTokenProvider (no IGraphTokenProvider). Las subscriptions
    // con includeResourceData=true solo son administrables por la app que las creo
    // (el "applicationId" de la subscription). Como las creo la "Webhook app" dedicada,
    // PATCHearlas con sconnor delegated da 404 ExtensionError. Mismo token-provider que
    // usa SubscriptionRenewer.
    public LifecycleHandler(
        HttpClient http,
        IWebhookAppTokenProvider tokens,
        IAlertManager alerts,
        ISubscriptionRecreator recreator,
        ISubscriptionStateStore stateStore,
        IOptions<RenewerOptions> renewerOptions,
        ILogger<LifecycleHandler> logger)
    {
        _http = http;
        _tokens = tokens;
        _alerts = alerts;
        _recreator = recreator;
        _stateStore = stateStore;
        _renewerOptions = renewerOptions.Value;
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
                await HandleSubscriptionRemovedAsync(subId, cancellationToken);
                break;
            case "missed":
                FileLog($"WARN: Graph reporta notifications perdidas en sub {subId}");
                break;
            default:
                FileLog($"WARN: evento desconocido '{ev}'");
                break;
        }
    }

    private async Task HandleSubscriptionRemovedAsync(string removedSubId, CancellationToken cancellationToken)
    {
        // Buscar la definicion correspondiente en config. Sin ella no podemos recrear:
        // no sabemos cual era el resource original ni los minutos de expiracion.
        var def = _renewerOptions.Subscriptions.FirstOrDefault(s =>
            string.Equals(s.Id, removedSubId, StringComparison.OrdinalIgnoreCase));
        if (def is null)
        {
            FileLog($"REMOVED sub={removedSubId} pero NO esta en RenewerOptions.Subscriptions[]. Auto-recovery imposible: agregue la definicion al config (Resource + ExpirationMinutes) si quiere recovery automatico la proxima vez.");
            _alerts.Record(AlertCategory.SubscriptionRenewalFailures,
                $"subscriptionRemoved sub={removedSubId} sin definicion en config para auto-recreate");
            return;
        }

        FileLog($"REMOVED sub={removedSubId} label={def.Label} resource={def.Resource}. Recreando...");

        var recreated = await _recreator.RecreateAsync(def, cancellationToken);
        if (recreated is null)
        {
            FileLog($"AUTO-RECOVERY FAIL para sub label={def.Label}. Intervencion manual requerida.");
            _alerts.Record(AlertCategory.SubscriptionRenewalFailures,
                $"AUTO-RECOVERY FAIL para sub label={def.Label} - intervencion manual requerida");
            return;
        }

        // Actualizar la definicion in-memory con el id nuevo y persistir a disco. El
        // state file (ver SubscriptionStateStore) sobrevive a restarts del app pool y
        // se aplica sobre el config al startup, asi que el id nuevo se mantiene aunque
        // Felipe no toque el User Secrets.
        var oldId = def.Id;
        def.Id = recreated.NewId;
        await _stateStore.SaveAsync(_renewerOptions.Subscriptions, cancellationToken);
        FileLog($"AUTO-RECOVERY OK label={def.Label} oldId={oldId} newId={recreated.NewId} expira={recreated.ExpirationDateTime:o}");

        // Alertar a Felipe igual: el state file lo hace sobrevivir, pero conviene que
        // sepa que el id viejo en User Secrets quedo obsoleto.
        _alerts.Record(AlertCategory.SubscriptionRenewalFailures,
            $"AUTO-RECOVERY de sub label={recreated.Label}: oldId={oldId}, NEW id={recreated.NewId}. State persistido a disco - User Secrets puede actualizarse al ocio.");
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
