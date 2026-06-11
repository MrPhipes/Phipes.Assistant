using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;

namespace Phipes.Assistant.WebhookHandler.Services;

// BackgroundService que mantiene vivas las Graph subscriptions con PATCH preventivo.
// No depende del lifecycle event de Microsoft (que tiene un bug 404 raro hoy).
// Despierta cada Renewer:IntervalMinutes, lista las subs configuradas, y a las que
// estan por expirar les extiende expirationDateTime.
[SupportedOSPlatform("windows")]
public sealed class SubscriptionRenewer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RenewerOptions _options;
    private readonly IAlertManager _alerts;
    private readonly ILogger<SubscriptionRenewer> _logger;

    public SubscriptionRenewer(
        IServiceScopeFactory scopeFactory,
        IOptions<RenewerOptions> options,
        IAlertManager alerts,
        ILogger<SubscriptionRenewer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _alerts = alerts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ids = ResolveSubscriptionIds();
        FileLog($"started, interval={_options.IntervalMinutes}min, watching {ids.Count} subscriptions");

        // Primera pasada al arrancar (no esperamos al primer intervalo).
        await RenewAllAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RenewAllAsync(stoppingToken);
        }
    }

    // Fuente de verdad de los IDs a renovar: si Subscriptions[] esta poblado (config
    // moderna, con resource/changeType/label completos para auto-recovery), gana sobre
    // SubscriptionIds[] (legacy, solo strings sueltos). Esto matchea la documentacion
    // en RenewerOptions.cs. Antes el Renewer solo miraba SubscriptionIds, lo cual
    // obligaba a mantener AMBOS arrays sincronizados en secrets.json.
    private List<string> ResolveSubscriptionIds()
    {
        if (_options.Subscriptions.Length > 0)
        {
            return _options.Subscriptions
                .Where(s => !string.IsNullOrEmpty(s.Id))
                .Select(s => s.Id)
                .ToList();
        }
        return _options.SubscriptionIds.Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    private async Task RenewAllAsync(CancellationToken cancellationToken)
    {
        var ids = ResolveSubscriptionIds();
        if (ids.Count == 0)
        {
            FileLog("WARN: no subscription IDs configured (ni Subscriptions[] ni SubscriptionIds[])");
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tokens = scope.ServiceProvider.GetRequiredService<IWebhookAppTokenProvider>();
            var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var recreator = scope.ServiceProvider.GetRequiredService<ISubscriptionRecreator>();
            var stateStore = scope.ServiceProvider.GetRequiredService<ISubscriptionStateStore>();
            var http = httpFactory.CreateClient("renewer");

            var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            if (_options.Subscriptions.Length > 0)
            {
                foreach (var def in _options.Subscriptions.Where(s => !string.IsNullOrEmpty(s.Id)))
                {
                    try
                    {
                        await RenewOneAsync(http, def, recreator, stateStore, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        FileLog($"FAIL sub={def.Id}: {ex.Message}");
                    }
                }
            }
            else
            {
                // Legacy: ids sueltos sin definicion. Sin Resource/ExpirationMinutes no hay
                // auto-recovery posible; solo renovacion.
                foreach (var subId in ids)
                {
                    try
                    {
                        await RenewOneAsync(http, def: null, recreator, stateStore, cancellationToken, legacyId: subId);
                    }
                    catch (Exception ex)
                    {
                        FileLog($"FAIL sub={subId}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog($"FAIL outer: {ex.Message}");
        }
    }

    private async Task RenewOneAsync(
        HttpClient http,
        SubscriptionDefinition? def,
        ISubscriptionRecreator recreator,
        ISubscriptionStateStore stateStore,
        CancellationToken cancellationToken,
        string? legacyId = null)
    {
        var subscriptionId = def?.Id ?? legacyId ?? "";

        // 1. GET para saber estado actual y resource.
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/subscriptions/{subscriptionId}");
        using var getResp = await http.SendAsync(getReq, cancellationToken);
        if (getResp.StatusCode == HttpStatusCode.NotFound)
        {
            // La sub ya no existe en Graph (expiro durante un outage prolongado, o
            // Microsoft la elimino sin mandar el lifecycle subscriptionRemoved).
            // Mismo auto-recovery que LifecycleHandler.HandleSubscriptionRemovedAsync.
            await RecreateAsync(def, subscriptionId, recreator, stateStore, cancellationToken);
            return;
        }
        if (!getResp.IsSuccessStatusCode)
        {
            var body = await getResp.Content.ReadAsStringAsync(cancellationToken);
            FileLog($"GET FAIL sub={subscriptionId}: HTTP {(int)getResp.StatusCode} {Truncate(body, 200)}");
            return;
        }

        var sub = await getResp.Content.ReadFromJsonAsync<SubscriptionInfo>(cancellationToken: cancellationToken);
        if (sub is null) { FileLog($"WARN sub={subscriptionId}: GET returned null"); return; }

        var marginUntilExpiry = sub.ExpirationDateTime - DateTimeOffset.UtcNow;
        FileLog($"CHECK sub={subscriptionId} resource={sub.Resource} expiresIn={marginUntilExpiry.TotalMinutes:0}min");

        // 2. PATCH preventivo SIEMPRE (no solo cuando esta por expirar). Razon: Microsoft
        // entra en silencio temporal aunque la sub siga activa, y el PATCH reactiva el
        // delivery (memoria project-microsoft-graph-subscription-silence.md).
        // PATCH cuesta cero y mantiene vivo el flow. RenewWhenLessThanMinutes queda solo
        // como threshold informativo para el log.
        var extendMinutes = (sub.Resource?.Contains("messages", StringComparison.OrdinalIgnoreCase) == true)
            ? _options.MailExtendMinutes
            : _options.ChatExtendMinutes;
        var newExpiry = DateTimeOffset.UtcNow.AddMinutes(extendMinutes);

        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"https://graph.microsoft.com/v1.0/subscriptions/{subscriptionId}")
        {
            Content = JsonContent.Create(new { expirationDateTime = newExpiry.ToString("o") })
        };
        using var patchResp = await http.SendAsync(patchReq, cancellationToken);
        var patchBody = await patchResp.Content.ReadAsStringAsync(cancellationToken);

        if (patchResp.IsSuccessStatusCode)
        {
            FileLog($"RENEWED sub={subscriptionId} extendedBy={extendMinutes}min newExpiry={newExpiry:o}");
        }
        else
        {
            FileLog($"PATCH FAIL sub={subscriptionId}: HTTP {(int)patchResp.StatusCode} {Truncate(patchBody, 300)}");
            _alerts.Record(AlertCategory.SubscriptionRenewalFailures,
                $"sub={subscriptionId} HTTP {(int)patchResp.StatusCode}");
        }
    }

    private async Task RecreateAsync(
        SubscriptionDefinition? def,
        string deadSubId,
        ISubscriptionRecreator recreator,
        ISubscriptionStateStore stateStore,
        CancellationToken cancellationToken)
    {
        if (def is null)
        {
            FileLog($"GET 404 sub={deadSubId} (legacy SubscriptionIds[], sin definicion). Auto-recovery imposible: migre el config a Subscriptions[] con Resource + ExpirationMinutes.");
            _alerts.Record(AlertCategory.SubscriptionRenewalFailures,
                $"sub={deadSubId} eliminada en Graph y sin definicion en config para auto-recreate");
            return;
        }

        FileLog($"GET 404 sub={deadSubId} label={def.Label}: la sub ya no existe en Graph. Recreando...");
        var recreated = await recreator.RecreateAsync(def, cancellationToken);
        if (recreated is null)
        {
            FileLog($"AUTO-RECOVERY FAIL para sub label={def.Label}. Intervencion manual requerida.");
            _alerts.Record(AlertCategory.SubscriptionRenewalFailures,
                $"AUTO-RECOVERY FAIL para sub label={def.Label} - intervencion manual requerida");
            return;
        }

        def.Id = recreated.NewId;
        await stateStore.SaveAsync(_options.Subscriptions, cancellationToken);
        FileLog($"AUTO-RECOVERY OK label={def.Label} oldId={deadSubId} newId={recreated.NewId} expira={recreated.ExpirationDateTime:o}");
        _alerts.Record(AlertCategory.SubscriptionRenewalFailures,
            $"AUTO-RECOVERY de sub label={recreated.Label}: oldId={deadSubId}, NEW id={recreated.NewId}. State persistido a disco - User Secrets puede actualizarse al ocio.");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

        private static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Renewer", message);

    private sealed class SubscriptionInfo
    {
        [JsonPropertyName("id")]                 public string? Id { get; set; }
        [JsonPropertyName("resource")]           public string? Resource { get; set; }
        [JsonPropertyName("expirationDateTime")] public DateTimeOffset ExpirationDateTime { get; set; }
        [JsonPropertyName("applicationId")]      public string? ApplicationId { get; set; }
    }
}
