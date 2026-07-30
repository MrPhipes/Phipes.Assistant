using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

// Moderación asistida de redes Meta (Fase MetaSocial):
//
// El sitio meta.pacificdev.cl recibe los webhooks de Facebook/Instagram y los persiste
// idempotentes en PacificDevMeta.dbo.WebhookEvents (ProcessedUtc NULL = pendiente).
// Este servicio:
//   1. Vigila esa cola cada IntervalSeconds.
//   2. Extrae los COMENTARIOS de terceros (ignora los propios y los eventos sin acción).
//   3. Registra cada uno en dbo.MetaModerationPending (BD PhipesAssistant) — la cola que
//      la skill /meta-comentario de Sarah usa para ejecutar instrucciones de Felipe.
//   4. Avisa a Felipe por Teams: autor, texto, plataforma y el ID [M{n}] con el menú de
//      acciones ("responde con #promo", "ocúltalo", etc.).
//   5. Marca el WebhookEvent como procesado (despachado).
//
// Patrón calcado de WaProactiveNotifier: PeriodicTimer + scopes por tick.
[SupportedOSPlatform("windows")]
public sealed class MetaProactiveNotifier : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MetaNotifierOptions _options;
    private readonly string _assistantConnectionString;
    private readonly ILogger<MetaProactiveNotifier> _logger;

    private string? _felipeChatId;
    private readonly SemaphoreSlim _chatIdLock = new(1, 1);

    public MetaProactiveNotifier(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<MetaNotifierOptions> options,
        IOptions<IdempotencyOptions> idempotency,
        ILogger<MetaProactiveNotifier> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _options = options.Value;
        _assistantConnectionString = idempotency.Value.ConnectionString;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            FileLog("disabled (MetaNotifier:Enabled=false) — no se arma el timer");
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.MetaConnectionString))
        {
            FileLog("disabled: MetaConnectionString vacia");
            return;
        }

        var interval = Math.Max(10, _options.IntervalSeconds);
        FileLog($"started, interval={interval}s");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { FileLog($"FAIL outer tick: {ex.Message}"); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var events = await LoadPendingEventsAsync(ct);
        if (events.Count == 0) return;

        foreach (var ev in events)
        {
            try
            {
                var comments = ExtractComments(ev);

                if (comments.Count == 0)
                {
                    // Evento sin comentarios accionables (post propio, reacción, echo de
                    // nuestra propia respuesta, etc.): se despacha en silencio.
                    await MarkEventProcessedAsync(ev.Id, "sin-accion", ct);
                    continue;
                }

                foreach (var c in comments)
                {
                    var pendingId = await InsertPendingAsync(ev.Id, c, ct);
                    if (pendingId is null) continue; // duplicado (EventId+CommentId ya avisado)

                    var platform = c.Platform == "instagram" ? "Instagram" : "Facebook";
                    var texto = string.IsNullOrWhiteSpace(c.Text) ? "(sin texto: media/sticker)" : c.Text;
                    var msg =
                        $"🌊 Comentario nuevo en {platform} — {c.Author}: «{Truncate(texto, 400)}»\n" +
                        $"[M{pendingId}] Dime qué hago: “responde con #promo”, “responde: <texto>”, " +
                        $"“ocúltalo”, “bórralo” o “ignóralo”. Si no indicas el ID, tomo el último pendiente.";

                    await SendToFelipeAsync(msg, ct);
                    FileLog($"NOTIFIED M{pendingId} event={ev.Id} platform={c.Platform} author={c.Author}");
                }

                await MarkEventProcessedAsync(ev.Id, null, ct);
            }
            catch (Exception ex)
            {
                // Un evento que falla no bloquea el resto; queda pendiente y se reintenta
                // el próximo tick (el INSERT de MetaModerationPending es idempotente).
                FileLog($"FAIL event={ev.Id}: {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Parseo de payloads de Meta
    // -------------------------------------------------------------------------

    private sealed record PendingComment(
        string Platform, string CommentId, string? ParentId, string Author,
        string? AuthorId, string Text);

    private List<PendingComment> ExtractComments(MetaEvent ev)
    {
        var result = new List<PendingComment>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(ev.Payload); }
        catch (JsonException) { return result; }

        using (doc)
        {
            var root = doc.RootElement;
            var objectType = root.TryGetProperty("object", out var o) ? o.GetString() : null;
            if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var change in changes.EnumerateArray())
                {
                    var field = change.TryGetProperty("field", out var f) ? f.GetString() : null;
                    if (!change.TryGetProperty("value", out var value)) continue;

                    if (objectType == "page" && field == "feed")
                    {
                        var item = value.TryGetProperty("item", out var it) ? it.GetString() : null;
                        var verb = value.TryGetProperty("verb", out var vb) ? vb.GetString() : null;
                        if (item != "comment" || verb != "add") continue;

                        var commentId = value.TryGetProperty("comment_id", out var cid) ? cid.GetString() : null;
                        if (string.IsNullOrEmpty(commentId)) continue;

                        string? fromId = null, fromName = null;
                        if (value.TryGetProperty("from", out var from))
                        {
                            fromId = from.TryGetProperty("id", out var fi) ? fi.GetString() : null;
                            fromName = from.TryGetProperty("name", out var fn) ? fn.GetString() : null;
                        }
                        // Comentario del propio page (nuestra respuesta): no avisar.
                        if (!string.IsNullOrEmpty(_options.FacebookPageId) &&
                            string.Equals(fromId, _options.FacebookPageId, StringComparison.Ordinal))
                            continue;

                        var message = value.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        var postId = value.TryGetProperty("post_id", out var pid) ? pid.GetString() : null;
                        result.Add(new PendingComment("facebook", commentId!, postId,
                            fromName ?? "alguien", fromId, message));
                    }
                    else if (objectType == "instagram" && field == "comments")
                    {
                        var commentId = value.TryGetProperty("id", out var cid) ? cid.GetString() : null;
                        if (string.IsNullOrEmpty(commentId)) continue;

                        string? fromId = null, fromName = null;
                        if (value.TryGetProperty("from", out var from))
                        {
                            fromId = from.TryGetProperty("id", out var fi) ? fi.GetString() : null;
                            fromName = from.TryGetProperty("username", out var fu) ? fu.GetString() : null;
                        }
                        if (!string.IsNullOrEmpty(_options.InstagramUserId) &&
                            string.Equals(fromId, _options.InstagramUserId, StringComparison.Ordinal))
                            continue;

                        var text = value.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        string? mediaId = null;
                        if (value.TryGetProperty("media", out var media))
                            mediaId = media.TryGetProperty("id", out var mid) ? mid.GetString() : null;

                        result.Add(new PendingComment("instagram", commentId!, mediaId,
                            fromName is null ? "alguien" : $"@{fromName}", fromId, text));
                    }
                }
            }
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // SQL — cola origen (PacificDevMeta) y cola de moderación (PhipesAssistant)
    // -------------------------------------------------------------------------

    private sealed record MetaEvent(long Id, string ObjectType, string Payload);

    private async Task<List<MetaEvent>> LoadPendingEventsAsync(CancellationToken ct)
    {
        var result = new List<MetaEvent>();
        try
        {
            await using var conn = new SqlConnection(_options.MetaConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT TOP (20) Id, ObjectType, Payload
FROM dbo.WebhookEvents
WHERE ProcessedUtc IS NULL
ORDER BY Id";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new MetaEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }
        catch (Exception ex)
        {
            FileLog($"WARN LoadPendingEvents fallo: {ex.Message}");
        }
        return result;
    }

    private async Task MarkEventProcessedAsync(long eventId, string? note, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_options.MetaConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.WebhookEvents
SET ProcessedUtc = SYSUTCDATETIME(), Error = @note
WHERE Id = @id AND ProcessedUtc IS NULL";
        cmd.Parameters.AddWithValue("@id", eventId);
        cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // INSERT idempotente en la cola de moderación. Devuelve el Id insertado, o null si
    // ese (EventId, CommentId) ya estaba (índice único) — o sea, ya avisado.
    private async Task<int?> InsertPendingAsync(long eventId, PendingComment c, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_assistantConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.MetaModerationPending (EventId, Platform, CommentId, ParentId, Author, AuthorId, CommentText)
OUTPUT INSERTED.Id
VALUES (@ev, @plat, @cid, @pid, @author, @aid, @text)";
            cmd.Parameters.AddWithValue("@ev", eventId);
            cmd.Parameters.AddWithValue("@plat", c.Platform);
            cmd.Parameters.AddWithValue("@cid", Truncate(c.CommentId, 128));
            cmd.Parameters.AddWithValue("@pid", (object?)Truncate(c.ParentId ?? "", 128) is string s && s.Length > 0 ? s : DBNull.Value);
            cmd.Parameters.AddWithValue("@author", Truncate(c.Author, 256));
            cmd.Parameters.AddWithValue("@aid", (object?)c.AuthorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@text", c.Text);
            var id = await cmd.ExecuteScalarAsync(ct);
            return id is int i ? i : Convert.ToInt32(id);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return null; // ya avisado
        }
    }

    // -------------------------------------------------------------------------
    // Teams (mismo patrón que WaProactiveNotifier)
    // -------------------------------------------------------------------------

    private async Task SendToFelipeAsync(string message, CancellationToken ct)
    {
        var chatId = await ResolveFelipeChatIdAsync(ct);
        if (chatId is null)
        {
            FileLog("SEND SKIP: chat 1:1 con Felipe no resoluble");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IGraphTokenProvider>();
        var accessToken = await tokens.GetAccessTokenAsync(ct);
        var http = _httpFactory.CreateClient("metanotifier");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages")
        {
            Content = JsonContent.Create(new { body = new { contentType = "text", content = message } })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"POST chat falló: {(int)resp.StatusCode} {Truncate(body, 300)}");
        }
    }

    private async Task<string?> ResolveFelipeChatIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_felipeChatId)) return _felipeChatId;

        await _chatIdLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_felipeChatId)) return _felipeChatId;

            var upn = _options.FelipeUpn;
            if (string.IsNullOrWhiteSpace(upn))
            {
                FileLog("WARN FelipeUpn vacio");
                return null;
            }

            using var scope = _scopeFactory.CreateScope();
            var tokens = scope.ServiceProvider.GetRequiredService<IGraphTokenProvider>();
            var accessToken = await tokens.GetAccessTokenAsync(ct);
            var http = _httpFactory.CreateClient("metanotifier");

            using var userReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(upn)}?$select=id");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var userResp = await http.SendAsync(userReq, ct);
            userResp.EnsureSuccessStatusCode();
            var felipe = await userResp.Content.ReadFromJsonAsync<UserBrief>(cancellationToken: ct);
            var felipeId = felipe?.Id ?? throw new InvalidOperationException("Sin userId de Felipe");

            using var meReq = new HttpRequestMessage(HttpMethod.Get,
                "https://graph.microsoft.com/v1.0/me?$select=id");
            meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var meResp = await http.SendAsync(meReq, ct);
            meResp.EnsureSuccessStatusCode();
            var me = await meResp.Content.ReadFromJsonAsync<UserBrief>(cancellationToken: ct);
            var myId = me?.Id ?? throw new InvalidOperationException("Sin userId de /me");

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
                  ""user@odata.bind"": ""https://graph.microsoft.com/v1.0/users('{felipeId}')""
                }}
              ]
            }}";
            using var chatReq = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/chats")
            {
                Content = new StringContent(jsonStr, Encoding.UTF8, "application/json")
            };
            chatReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var chatResp = await http.SendAsync(chatReq, ct);
            chatResp.EnsureSuccessStatusCode();
            var chat = await chatResp.Content.ReadFromJsonAsync<ChatBrief>(cancellationToken: ct);
            _felipeChatId = chat?.Id ?? throw new InvalidOperationException("Sin chat.id");
            FileLog($"chat 1:1 con Felipe resuelto: {_felipeChatId}");
            return _felipeChatId;
        }
        finally
        {
            _chatIdLock.Release();
        }
    }

    // -------------------------------------------------------------------------

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

    private static void FileLog(string message) => FileLogger.Write("MetaNotifier", message);

    private sealed class UserBrief
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    private sealed class ChatBrief
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
