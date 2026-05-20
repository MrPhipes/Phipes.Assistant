using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

// Categorias de eventos que pueden disparar alertas. Mapean 1:1 a las reglas en
// MonitoringOptions (AlertRule por categoria).
public enum AlertCategory
{
    ClaudeTimeouts,
    DecryptFailures,
    JwtInvalid,
    SubscriptionRenewalFailures,
    ClaudeApiErrors
}

public interface IAlertManager
{
    // Registra una ocurrencia de la categoria. Si los thresholds se exceden y la
    // suppression list lo permite, dispara una alerta a Teams.
    void Record(AlertCategory category, string? detail = null);
}

[SupportedOSPlatform("windows")]
public sealed class AlertManager : IAlertManager
{
    private readonly MonitoringOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlertManager> _logger;

    // Rolling window de timestamps por categoria (last hour worth).
    private readonly ConcurrentDictionary<AlertCategory, ConcurrentQueue<DateTimeOffset>> _events = new();

    // Ultima alerta enviada por categoria (anti-spam).
    private readonly ConcurrentDictionary<AlertCategory, DateTimeOffset> _lastAlertSent = new();

    // chatId resuelto del AlertTarget (cache - se calcula una vez).
    private string? _alertChatId;
    private readonly SemaphoreSlim _chatIdLock = new(1, 1);

    public AlertManager(
        IOptions<MonitoringOptions> options,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILogger<AlertManager> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public void Record(AlertCategory category, string? detail = null)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var rule = ResolveRule(category);
        if (rule is null || !rule.Enabled)
        {
            return;
        }

        // Agregar timestamp al rolling window
        var queue = _events.GetOrAdd(category, _ => new ConcurrentQueue<DateTimeOffset>());
        var now = DateTimeOffset.UtcNow;
        queue.Enqueue(now);

        // Limpiar entries fuera de la ventana
        var cutoff = now.AddMinutes(-rule.WindowMinutes);
        while (queue.TryPeek(out var oldest) && oldest < cutoff)
        {
            queue.TryDequeue(out _);
        }

        var count = queue.Count;
        FileLogger.Write("Alert", $"category={category} count={count}/{rule.Threshold} window={rule.WindowMinutes}min detail={detail ?? "-"}");

        if (count < rule.Threshold)
        {
            return;
        }

        // Anti-spam: si ya alertamos esta categoria recientemente, suprimir.
        if (_lastAlertSent.TryGetValue(category, out var last)
            && (now - last).TotalMinutes < _options.SuppressionMinutes)
        {
            FileLogger.Write("Alert", $"SUPPRESSED category={category} (ultima alerta hace {(now - last).TotalMinutes:0} min < {_options.SuppressionMinutes} min)");
            return;
        }

        // Disparar alerta en background (no bloquear al caller).
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAlertAsync(category, rule, count, detail);
                _lastAlertSent[category] = now;
            }
            catch (Exception ex)
            {
                FileLogger.Write("Alert", $"SEND FAIL category={category}: {ex.Message}");
            }
        });
    }

    private async Task SendAlertAsync(AlertCategory category, AlertRule rule, int count, string? detail)
    {
        var chatId = await ResolveChatIdAsync();
        if (chatId is null)
        {
            FileLogger.Write("Alert", "SEND FAIL: AlertTarget no resoluble");
            return;
        }

        var msg = $"{_options.AlertPrefix} {rule.Label}: {count} ocurrencias en los últimos {rule.WindowMinutes} min."
                + (string.IsNullOrEmpty(detail) ? "" : $" Detalle: {detail}");

        using var scope = _scopeFactory.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IGraphTokenProvider>();
        var accessToken = await tokens.GetAccessTokenAsync();
        var http = _httpFactory.CreateClient("alerts");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages")
        {
            Content = JsonContent.Create(new { body = new { contentType = "text", content = msg } })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"POST chat failed: {(int)resp.StatusCode} {body}");
        }
        FileLogger.Write("Alert", $"SENT category={category} ({count} ocurrencias)");
    }

    private async Task<string?> ResolveChatIdAsync()
    {
        if (!string.IsNullOrEmpty(_alertChatId)) return _alertChatId;

        await _chatIdLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_alertChatId)) return _alertChatId;

            var target = _options.AlertTarget;
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            // chatId directo
            if (target.StartsWith("19:", StringComparison.Ordinal))
            {
                _alertChatId = target;
                return _alertChatId;
            }

            // UPN: crear/encontrar chat 1:1 con esa persona
            using var scope = _scopeFactory.CreateScope();
            var tokens = scope.ServiceProvider.GetRequiredService<IGraphTokenProvider>();
            var accessToken = await tokens.GetAccessTokenAsync();
            var http = _httpFactory.CreateClient("alerts");

            // 1. Resolver userId del UPN
            using var userReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(target)}?$select=id");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var userResp = await http.SendAsync(userReq);
            userResp.EnsureSuccessStatusCode();
            var userPayload = await userResp.Content.ReadFromJsonAsync<UserBrief>();
            var targetUserId = userPayload?.Id ?? throw new InvalidOperationException("No userId");

            // 2. Mi propio userId
            using var meReq = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=id");
            meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var meResp = await http.SendAsync(meReq);
            meResp.EnsureSuccessStatusCode();
            var mePayload = await meResp.Content.ReadFromJsonAsync<UserBrief>();
            var myId = mePayload?.Id ?? throw new InvalidOperationException("No /me id");

            // 3. POST /chats con oneOnOne (Graph retorna chat existente si ya existe)
            var chatBody = new
            {
                chatType = "oneOnOne",
                members = new[]
                {
                    new
                    {
                        odataType = "#microsoft.graph.aadUserConversationMember",
                        roles = new[] { "owner" },
                        userOdataBind = $"https://graph.microsoft.com/v1.0/users('{myId}')"
                    },
                    new
                    {
                        odataType = "#microsoft.graph.aadUserConversationMember",
                        roles = new[] { "owner" },
                        userOdataBind = $"https://graph.microsoft.com/v1.0/users('{targetUserId}')"
                    }
                }
            };
            // System.Text.Json no maneja @odata bien con nombres .NET — escribir JSON manual
            var jsonStr = $@"{{
              ""chatType"": ""oneOnOne"",
              ""members"": [
                {{
                  ""@odata.type"": ""#microsoft.graph.aadUserConversationMember"",
                  ""roles"": [""owner""],
                  ""user@odata.bind"": ""https://graph.microsoft.com/v1.0/users('{myId}')""
                }},
                {{
                  ""@odata.type"": ""#microsoft.graph.aadUserConversationMember"",
                  ""roles"": [""owner""],
                  ""user@odata.bind"": ""https://graph.microsoft.com/v1.0/users('{targetUserId}')""
                }}
              ]
            }}";
            using var chatReq = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/chats")
            {
                Content = new StringContent(jsonStr, System.Text.Encoding.UTF8, "application/json")
            };
            chatReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var chatResp = await http.SendAsync(chatReq);
            chatResp.EnsureSuccessStatusCode();
            var chatPayload = await chatResp.Content.ReadFromJsonAsync<ChatBrief>();
            _alertChatId = chatPayload?.Id ?? throw new InvalidOperationException("No chat id");
            FileLogger.Write("Alert", $"chatId resuelto para {_options.AlertTarget}: {_alertChatId}");
            return _alertChatId;
        }
        finally
        {
            _chatIdLock.Release();
        }
    }

    private AlertRule? ResolveRule(AlertCategory category) => category switch
    {
        AlertCategory.ClaudeTimeouts                => _options.ClaudeTimeouts,
        AlertCategory.DecryptFailures               => _options.DecryptFailures,
        AlertCategory.JwtInvalid                    => _options.JwtInvalid,
        AlertCategory.SubscriptionRenewalFailures   => _options.SubscriptionRenewalFailures,
        AlertCategory.ClaudeApiErrors               => _options.ClaudeApiErrors,
        _ => null
    };

    private sealed class UserBrief
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string? Id { get; set; }
    }
    private sealed class ChatBrief
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string? Id { get; set; }
    }
}
