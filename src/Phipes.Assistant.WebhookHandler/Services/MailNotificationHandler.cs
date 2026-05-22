using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Models;

namespace Phipes.Assistant.WebhookHandler.Services;

public interface IMailNotificationHandler
{
    Task HandleAsync(ChangeNotification notification, CancellationToken cancellationToken = default);
}

// Procesa notifications de /me/messages (correos). Filtra ruido (notificaciones
// automaticas, meeting invites, etc.) y para correos reales invoca Claude para
// decidir si responder (Claude puede responder [SKIP] para no responder).
[SupportedOSPlatform("windows")]
public sealed class MailNotificationHandler : IMailNotificationHandler
{
    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly INotificationDecrypter _decrypter;
    private readonly IClaudeCodeInvoker _claude;
    private readonly IAlertManager _alerts;
    private readonly GraphOptions _graphOptions;
    private readonly ILogger<MailNotificationHandler> _logger;

    // Dominios/addresses que NUNCA responden (notificaciones automaticas).
    private static readonly HashSet<string> NoReplyAddresses = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-reply@teams.mail.microsoft", "noreply@microsoft.com", "no-reply@microsoft.com",
        "mailer-daemon@", "postmaster@",
    };

    public MailNotificationHandler(
        HttpClient http,
        IGraphTokenProvider tokens,
        INotificationDecrypter decrypter,
        IClaudeCodeInvoker claude,
        IAlertManager alerts,
        IOptions<GraphOptions> graphOptions,
        ILogger<MailNotificationHandler> logger)
    {
        _http = http;
        _tokens = tokens;
        _decrypter = decrypter;
        _claude = claude;
        _alerts = alerts;
        _graphOptions = graphOptions.Value;
        _logger = logger;
    }

    public async Task HandleAsync(ChangeNotification notification, CancellationToken cancellationToken = default)
    {
        if (notification.EncryptedContent is null)
        {
            FileLog($"DESCARTADA sub={notification.SubscriptionId}: sin encryptedContent");
            return;
        }

        if (!string.Equals(notification.ChangeType, "created", StringComparison.OrdinalIgnoreCase))
        {
            FileLog($"IGNORADA changeType={notification.ChangeType}");
            return;
        }

        // 1. Descifrar
        string messageJson;
        try
        {
            messageJson = _decrypter.Decrypt(notification.EncryptedContent);
            FileLog($"DECRYPT OK ({messageJson.Length} bytes)");
        }
        catch (Exception ex)
        {
            FileLog($"DECRYPT FAIL: {ex}");
            _alerts.Record(AlertCategory.DecryptFailures, $"mail sub={notification.SubscriptionId}: {ex.Message}");
            throw;
        }

        var message = JsonSerializer.Deserialize<GraphEmailMessage>(messageJson);
        if (message is null)
        {
            FileLog($"PARSE FAIL: GraphEmailMessage null. JSON head: {Truncate(messageJson, 400)}");
            return;
        }

        // El JSON descifrado NO incluye el id cuando la subscription usa $select sin id.
        // Extraemos el id del notification.Resource (ej "Users('...')/messages('AAMkAD...AAA=')").
        if (string.IsNullOrEmpty(message.Id))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                notification.Resource ?? "",
                @"messages\(['""]([^'""]+)['""]\)");
            if (match.Success)
            {
                message.Id = match.Groups[1].Value;
            }
            else
            {
                FileLog($"PARSE FAIL: sin id en JSON ni en Resource '{notification.Resource}'");
                return;
            }
        }

        var fromAddr = message.From?.EmailAddress?.Address ?? message.Sender?.EmailAddress?.Address ?? "";
        var fromName = message.From?.EmailAddress?.Name ?? "(sin nombre)";
        FileLog($"EMAIL id={message.Id} from={fromAddr} subject={Truncate(message.Subject ?? "", 60)}");

        // 2. Filtros de no respuesta automatica
        if (IsNoReply(fromAddr))
        {
            FileLog($"SKIP no-reply sender: {fromAddr}");
            return;
        }

        // Solo respondemos si la asistente estaba en TO directamente (no en CC ni BCC).
        var assistantAddr = _graphOptions.UserPrincipalName;
        var inTo = message.ToRecipients?.Any(r =>
            string.Equals(r.EmailAddress?.Address, assistantAddr, StringComparison.OrdinalIgnoreCase)) ?? false;
        if (!inTo)
        {
            FileLog($"SKIP asistente no esta en TO (probablemente CC o lista grande)");
            return;
        }

        // 3. Necesitamos el body completo si el preview esta truncado (>240 chars suele ser truncado).
        var accessToken = await _tokens.GetAccessTokenAsync(cancellationToken);
        var fullBody = message.BodyPreview ?? "";
        if ((message.BodyPreview?.Length ?? 0) >= 240)
        {
            try
            {
                fullBody = await FetchFullBodyAsync(message.Id, accessToken, cancellationToken);
                FileLog($"FETCHED full body ({fullBody.Length} bytes)");
            }
            catch (Exception ex)
            {
                FileLog($"FETCH body fallo, uso preview: {ex.Message}");
            }
        }

        // 4. Prompt a Claude. Le pedimos que decida: [SKIP] o cuerpo del reply.
        var prompt =
            $"Acabas de recibir este correo electronico (dirigido directamente a usted como destinataria principal).\n" +
            $"De: {fromName} <{fromAddr}>\n" +
            $"Asunto: {message.Subject ?? "(sin asunto)"}\n" +
            $"Recibido: {message.ReceivedDateTime:yyyy-MM-dd HH:mm}\n\n" +
            $"Cuerpo:\n{fullBody}\n\n" +
            $"Decida si responder al correo. Si NO requiere respuesta (informacion, FYI, " +
            $"newsletter, notificacion del sistema, invitacion de reunion ya manejada por " +
            $"otro flujo, mensaje irrelevante), responda EXACTAMENTE con el token `[SKIP]` " +
            $"(sin nada mas, sin explicar).\n\n" +
            $"Si requiere respuesta, escriba SOLAMENTE el cuerpo del reply (texto plano, sin " +
            $"asunto, sin saludo de apertura redundante, sin firma manual - la firma ya esta " +
            $"configurada en Outlook). MantÃ©ngase concisa, profesional y en su tono Sarah Connor.";

        // Usamos un session-id distinto al de chats: usamos conversationId para que toda
        // la hebra de correos con el mismo remitente comparta contexto.
        var sessionKey = "mail:" + (message.ConversationId ?? fromAddr);

        string claudeOutput;
        try
        {
            claudeOutput = await _claude.AskAsync(sessionKey, prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            FileLog($"CLAUDE FAIL para email {message.Id}: {ex.Message}");
            throw;
        }

        if (string.IsNullOrWhiteSpace(claudeOutput) ||
            claudeOutput.Trim().Equals("[SKIP]", StringComparison.OrdinalIgnoreCase) ||
            claudeOutput.Trim().StartsWith("[SKIP]"))
        {
            FileLog($"SKIP decidido por Claude para {message.Id}");
            return;
        }

        // 5. Postear reply.
        try
        {
            await ReplyToMessageAsync(message.Id, claudeOutput, accessToken, cancellationToken);
            FileLog($"REPLY SENT a email {message.Id} ({claudeOutput.Length} chars)");
        }
        catch (Exception ex)
        {
            FileLog($"REPLY SEND FAIL: {ex.Message}");
            throw;
        }
    }

    private async Task<string> FetchFullBodyAsync(string messageId, string accessToken, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/me/messages/{messageId}?$select=body,subject");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var msg = await resp.Content.ReadFromJsonAsync<GraphEmailMessage>(cancellationToken: cancellationToken);
        var raw = msg?.Body?.Content ?? "";
        if (msg?.Body?.ContentType?.Equals("html", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Strip HTML tags y decode entities.
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "<style[^>]*>[\\s\\S]*?</style>", "");
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "<script[^>]*>[\\s\\S]*?</script>", "");
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]+>", " ");
            raw = System.Net.WebUtility.HtmlDecode(raw);
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "\\s+", " ").Trim();
        }
        return raw;
    }

    private async Task ReplyToMessageAsync(string messageId, string body, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"https://graph.microsoft.com/v1.0/me/messages/{messageId}/reply";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                comment = body
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"reply HTTP {(int)resp.StatusCode}: {Truncate(err, 300)}");
        }
    }

    private static bool IsNoReply(string from)
    {
        if (string.IsNullOrEmpty(from)) return true;
        foreach (var pattern in NoReplyAddresses)
        {
            if (from.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // Reglas heuristicas adicionales
        var local = from.Split('@')[0].ToLowerInvariant();
        if (local.StartsWith("no-reply") || local.StartsWith("noreply") ||
            local.StartsWith("donotreply") || local.StartsWith("notifications") ||
            local.Contains("mailer-daemon") || local.Contains("postmaster"))
        {
            return true;
        }
        return false;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

        private static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Mail", message);
}
