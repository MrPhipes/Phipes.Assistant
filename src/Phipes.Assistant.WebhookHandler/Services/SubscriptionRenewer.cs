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
    private readonly ILogger<SubscriptionRenewer> _logger;

    public SubscriptionRenewer(
        IServiceScopeFactory scopeFactory,
        IOptions<RenewerOptions> options,
        ILogger<SubscriptionRenewer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FileLog($"started, interval={_options.IntervalMinutes}min, watching {_options.SubscriptionIds.Length} subscriptions");

        // Primera pasada al arrancar (no esperamos al primer intervalo).
        await RenewAllAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RenewAllAsync(stoppingToken);
        }
    }

    private async Task RenewAllAsync(CancellationToken cancellationToken)
    {
        if (_options.SubscriptionIds.Length == 0)
        {
            FileLog("WARN: no subscription IDs configured");
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tokens = scope.ServiceProvider.GetRequiredService<IWebhookAppTokenProvider>();
            var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var http = httpFactory.CreateClient("renewer");

            var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            foreach (var subId in _options.SubscriptionIds)
            {
                try
                {
                    await RenewOneAsync(http, subId, cancellationToken);
                }
                catch (Exception ex)
                {
                    FileLog($"FAIL sub={subId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLog($"FAIL outer: {ex.Message}");
        }
    }

    private async Task RenewOneAsync(HttpClient http, string subscriptionId, CancellationToken cancellationToken)
    {
        // 1. GET para saber estado actual y resource.
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/subscriptions/{subscriptionId}");
        using var getResp = await http.SendAsync(getReq, cancellationToken);
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
        }
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
