using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phipes.Assistant.WebhookHandler.Models;

namespace Phipes.Assistant.WebhookHandler.Services;

// MVP: cuando llega una notification de Teams chat message, usa el resourceData ya
// descifrado por NotificationDecrypter, decide si necesita responder, y manda una
// respuesta canned. La lÃ³gica real (LLM, reglas, etc.) reemplaza BuildResponseAsync.
public sealed class TeamsNotificationHandler : ITeamsNotificationHandler
{
    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly INotificationDecrypter _decrypter;
    private readonly IClaudeCodeInvoker _claude;
    private readonly ILogger<TeamsNotificationHandler> _logger;

    private string? _myUserId;
    private readonly SemaphoreSlim _myIdLock = new(1, 1);

    public TeamsNotificationHandler(
        HttpClient http,
        IGraphTokenProvider tokens,
        INotificationDecrypter decrypter,
        IClaudeCodeInvoker claude,
        ILogger<TeamsNotificationHandler> logger)
    {
        _http = http;
        _tokens = tokens;
        _decrypter = decrypter;
        _claude = claude;
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

        // 1. Descifrar el contenido del mensaje real.
        string messageJson;
        try
        {
            messageJson = _decrypter.Decrypt(notification.EncryptedContent);
            FileLog($"DECRYPT OK ({messageJson.Length} bytes)");
        }
        catch (Exception ex)
        {
            FileLog($"DECRYPT FAIL: {ex}");
            throw;
        }

        var message = JsonSerializer.Deserialize<GraphChatMessage>(messageJson);
        if (message is null)
        {
            FileLog("PARSE FAIL: GraphChatMessage null");
            return;
        }

        var senderId = message.From?.User?.Id;
        if (string.IsNullOrEmpty(senderId))
        {
            FileLog("IGNORADA: mensaje sin from.user.id (system message)");
            return;
        }

        string accessToken;
        try
        {
            accessToken = await _tokens.GetAccessTokenAsync(cancellationToken);
            FileLog($"ACCESS TOKEN OK (len={accessToken.Length})");
        }
        catch (Exception ex)
        {
            FileLog($"ACCESS TOKEN FAIL: {ex}");
            throw;
        }

        string myId;
        try
        {
            myId = await GetMyUserIdAsync(accessToken, cancellationToken);
        }
        catch (Exception ex)
        {
            FileLog($"GET /me FAIL: {ex}");
            throw;
        }

        if (string.Equals(senderId, myId, StringComparison.OrdinalIgnoreCase))
        {
            FileLog($"IGNORADA: mensaje propio (senderId={senderId})");
            return;
        }

        var chatId = ExtractChatId(notification.Resource ?? "");
        if (chatId is null)
        {
            FileLog($"PARSE FAIL: no pude extraer chatId de '{notification.Resource}'");
            return;
        }

        var reply = await BuildResponseAsync(chatId, message, cancellationToken);
        FileLog($"REPLY READY to chat={chatId}, sender={message.From?.User?.DisplayName} ({senderId})");
        try
        {
            await SendChatReplyAsync(chatId, reply, accessToken, cancellationToken);
            FileLog($"REPLY SENT to chat={chatId}");
        }
        catch (Exception ex)
        {
            FileLog($"REPLY SEND FAIL chat={chatId}: {ex}");
            throw;
        }
    }

        private static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Teams", message);

    // Genera la respuesta delegando a Claude Code en modo --print (Sarah hosted).
    // Fallback al canned reply si claude falla, para nunca dejar al interlocutor sin respuesta.
    private async Task<string> BuildResponseAsync(string chatId, GraphChatMessage incoming, CancellationToken cancellationToken)
    {
        var sender = incoming.From?.User?.DisplayName ?? "usted";
        var senderId = incoming.From?.User?.Id;
        var rawBody = incoming.Body?.Content?.Trim() ?? "";
        // Si viene HTML, lo limpiamos para que el LLM no vea markup ruidoso.
        var cleanBody = System.Text.RegularExpressions.Regex.Replace(rawBody, "<[^>]+>", " ");
        cleanBody = System.Net.WebUtility.HtmlDecode(cleanBody).Trim();

        var prompt = $"Acabas de recibir este mensaje en un chat de Microsoft Teams.\n" +
                     $"Remitente: {sender}" + (senderId is null ? "" : $" (id={senderId})") + "\n" +
                     $"chatId: {chatId}\n\n" +
                     $"Mensaje:\n{cleanBody}\n\n" +
                     $"Responde directamente como Sarah Connor en una o dos frases concisas. " +
                     $"Tu respuesta sera posteada literalmente al chat - no incluyas saludos largos, " +
                     $"firmas, ni marcadores tipo \"Sarah:\".";

        // SIN canned reply: Sarah NUNCA debe sonar a bot. Si claude.exe falla,
        // preferimos silencio - Felipe puede intervenir manualmente o el remitente
        // re-pingea mas tarde. Una respuesta robotica "recibi su mensaje y le respondo
        // en cuanto pueda" delata la naturaleza no-humana y rompe la regla inviolable
        // de no revelar AI.
        var reply = await _claude.AskAsync(chatId, prompt, cancellationToken);
        _logger.LogInformation("Claude respondio a {Sender} (chat {ChatId}) con {Len} chars", sender, chatId, reply.Length);
        return reply;
    }

    private async Task<string> GetMyUserIdAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_myUserId)) return _myUserId;
        await _myIdLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(_myUserId)) return _myUserId;
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=id");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var me = await resp.Content.ReadFromJsonAsync<GraphUserBrief>(cancellationToken: cancellationToken);
            _myUserId = me?.Id ?? throw new InvalidOperationException("No se pudo obtener id de /me");
            _logger.LogInformation("Identidad cacheada: id={Id}", _myUserId);
            return _myUserId;
        }
        finally { _myIdLock.Release(); }
    }

    private async Task SendChatReplyAsync(string chatId, string content, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages";
        var body = new
        {
            body = new { contentType = "text", content }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("POST reply fallÃ³: {Status} {Body}", (int)resp.StatusCode, err);
            resp.EnsureSuccessStatusCode();
        }
    }

    // Parsea "chats('19:xxx@unq.gbl.spaces')/messages('...')" o "chats/{id}/messages/{id}".
    private static string? ExtractChatId(string resource)
    {
        var i = resource.IndexOf("chats('", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var start = i + "chats('".Length;
            var end = resource.IndexOf('\'', start);
            return end > start ? resource[start..end] : null;
        }
        i = resource.IndexOf("chats/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var start = i + "chats/".Length;
            var end = resource.IndexOf('/', start);
            return end > start ? resource[start..end] : null;
        }
        return null;
    }

    private sealed class GraphUserBrief
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    private sealed class GraphChatMessage
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("from")] public GraphFrom? From { get; set; }
        [JsonPropertyName("body")] public GraphBody? Body { get; set; }
    }
    private sealed class GraphFrom
    {
        [JsonPropertyName("user")] public GraphUserDetailed? User { get; set; }
    }
    private sealed class GraphUserDetailed
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }
    private sealed class GraphBody
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    }
}
