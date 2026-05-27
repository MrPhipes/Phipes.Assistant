using System.Collections.Concurrent;
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

// BackgroundService de la Fase 2: aviso proactivo de WhatsApp.
//
// Cuando Felipe le pregunta a Sarah por un contacto, la skill /wsp registra ese contacto
// en la tabla WaActiveTopic (tema activo). Si ESE contacto le escribe por WhatsApp dentro
// de ActiveWindowMinutes, este servicio le avisa proactivamente a Felipe por Teams:
//   "📱 {nombre} te escribio recien: «{texto}»"
//
// El pipeline de WhatsApp (bridge + tarea WhatsApp-Derive) NO se toca: este servicio solo
// LEE el staging inbox.jsonl (+ su hermano .proc si existe), tolerando que aparezca /
// desaparezca / este bloqueado (IOException -> skip silencioso ese tick).
//
// Patron calcado de SubscriptionRenewer: PeriodicTimer + IServiceScopeFactory para resolver
// dependencias scoped (IGraphTokenProvider) por tick, sin captar un scope vivo en el singleton.
[SupportedOSPlatform("windows")]
public sealed class WaProactiveNotifier : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly WaNotifierOptions _options;
    private readonly string _connectionString;
    private readonly ILogger<WaProactiveNotifier> _logger;

    // chatId 1:1 entre Sarah (/me) y Felipe — se resuelve una vez y se cachea.
    private string? _felipeChatId;
    private readonly SemaphoreSlim _chatIdLock = new(1, 1);

    // Cache en memoria de (Jid|MsgTs) ya notificados, para evitar pegarle a SQL en cada
    // tick por los mismos mensajes mientras el staging no se purga. La fuente de verdad
    // sigue siendo la PK de WaProactiveNotified; esto es solo un atajo.
    private readonly ConcurrentDictionary<string, byte> _notifiedCache = new();

    public WaProactiveNotifier(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<WaNotifierOptions> options,
        IOptions<IdempotencyOptions> idempotency,
        ILogger<WaProactiveNotifier> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _options = options.Value;
        _connectionString = idempotency.Value.ConnectionString;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Si el feature esta apagado, ni siquiera armamos el timer.
        if (!_options.Enabled)
        {
            FileLog("disabled (WaNotifier:Enabled=false) — no se arma el timer");
            return;
        }

        var interval = Math.Max(5, _options.IntervalSeconds);
        FileLog($"started, interval={interval}s, activeWindow={_options.ActiveWindowMinutes}min, staging={_options.StagingPath}");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                FileLog($"FAIL outer tick: {ex.Message}");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        // 1. Cargar temas activos (contactos preguntados dentro de la ventana).
        var topics = await LoadActiveTopicsAsync(cancellationToken);
        if (topics.Count == 0) return; // nada que vigilar

        // 2. Leer el staging (inbox.jsonl + .proc hermano). Tolera IOException saltando el tick.
        var messages = ReadStaging();
        if (messages.Count == 0) return;

        // 3. Filtrar mensajes entrantes (fromMe == false) cuyo chatJid sea un tema activo.
        var watched = new HashSet<string>(topics.Keys, StringComparer.OrdinalIgnoreCase);
        var relevant = messages
            .Where(m => !m.FromMe
                        && !string.IsNullOrEmpty(m.ChatJid)
                        && watched.Contains(m.ChatJid!))
            .ToList();
        if (relevant.Count == 0) return;

        // 4. Para cada mensaje, intentar marcarlo notificado (INSERT idempotente). Solo
        //    seguimos con los que insertamos OK (los demas ya se avisaron).
        var fresh = new List<WaStagingMessage>();
        foreach (var m in relevant)
        {
            var cacheKey = $"{m.ChatJid}|{m.Ts}";
            if (_notifiedCache.ContainsKey(cacheKey)) continue;

            bool inserted;
            try
            {
                inserted = await TryMarkNotifiedAsync(m.ChatJid!, m.Ts, cancellationToken);
            }
            catch (Exception ex)
            {
                // Error transitorio de SQL: no avisamos este mensaje en este tick (mejor
                // perder un aviso puntual que arriesgar un duplicado o romper el resto).
                FileLog($"WARN mark notified fallo jid={m.ChatJid} ts={m.Ts}: {ex.Message}");
                continue;
            }

            _notifiedCache.TryAdd(cacheKey, 0);
            if (inserted) fresh.Add(m);
        }
        if (fresh.Count == 0) return;

        // 5. Agrupar por contacto (chatJid) y armar UN aviso por contacto.
        var byContact = fresh
            .GroupBy(m => m.ChatJid!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byContact)
        {
            try
            {
                var jid = group.Key;
                topics.TryGetValue(jid, out var topicName);

                // Nombre: el de WaActiveTopic (lo que Sarah resolvio) gana; si no, el pushName
                // del propio mensaje; si tampoco, el Jid crudo (ultimo recurso para no quedar mudo).
                var ordered = group.OrderBy(m => m.Ts).ToList();
                var name = FirstNonEmpty(
                    topicName,
                    ordered.Select(m => m.PushName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                    jid);

                // Concatenar los textos del contacto. Es el WhatsApp del propio Felipe, por eso
                // se le puede mostrar el texto literal.
                var bodies = ordered
                    .Select(m => (m.Body ?? "").Trim())
                    .Where(b => b.Length > 0)
                    .Select(b => $"«{b}»");
                var joined = string.Join(" ", bodies);
                if (string.IsNullOrWhiteSpace(joined)) continue; // nada que mostrar (ej. solo media)

                var msg = $"📱 {name} te escribió recién: {joined}";

                await SendToFelipeAsync(msg, cancellationToken);
                FileLog($"NOTIFIED jid={jid} name={name} msgs={ordered.Count}");
            }
            catch (Exception ex)
            {
                // Un contacto que falla no debe romper el resto (igual que SubscriptionRenewer).
                FileLog($"FAIL notify jid={group.Key}: {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // SQL
    // -------------------------------------------------------------------------

    // Temas activos: Jid -> ContactName (puede ser null). Solo los preguntados dentro
    // de la ventana ActiveWindowMinutes. Si hay varias filas del mismo Jid, gana la mas
    // reciente con nombre no nulo.
    private async Task<Dictionary<string, string?>> LoadActiveTopicsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT Jid, ContactName, AskedAtUtc
FROM dbo.WaActiveTopic
WHERE AskedAtUtc > DATEADD(minute, -@win, SYSUTCDATETIME())
ORDER BY AskedAtUtc DESC";
            cmd.Parameters.AddWithValue("@win", _options.ActiveWindowMinutes);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var jid = reader.GetString(0);
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                // Primera ocurrencia (mas reciente por ORDER BY) gana; si quedo sin nombre y
                // llega otra fila mas vieja con nombre, lo completamos.
                if (!result.TryGetValue(jid, out var existing))
                {
                    result[jid] = name;
                }
                else if (string.IsNullOrWhiteSpace(existing) && !string.IsNullOrWhiteSpace(name))
                {
                    result[jid] = name;
                }
            }
        }
        catch (Exception ex)
        {
            FileLog($"WARN LoadActiveTopics fallo: {ex.Message}");
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    // INSERT idempotente. true si inserto (mensaje nuevo, hay que avisar); false si la PK
    // ya existia (ya se aviso). Excepciones de SQL distintas a PK violation se propagan
    // al caller para que decida (skip ese mensaje este tick).
    private async Task<bool> TryMarkNotifiedAsync(string jid, long msgTs, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO dbo.WaProactiveNotified (Jid, MsgTs) VALUES (@jid, @ts)";
            cmd.Parameters.AddWithValue("@jid", Truncate(jid, 128));
            cmd.Parameters.AddWithValue("@ts", msgTs);
            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            return rows > 0;
        }
        catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
        {
            // 2627: violation of PRIMARY KEY / 2601: violation of UNIQUE INDEX -> ya notificado.
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Staging
    // -------------------------------------------------------------------------

    // Lee inbox.jsonl + su hermano .proc (si existen). Una linea JSON por mensaje. Tolera
    // que falten / esten bloqueados (IOException -> ese archivo se salta sin romper el tick).
    private List<WaStagingMessage> ReadStaging()
    {
        var result = new List<WaStagingMessage>();
        var primary = _options.StagingPath;
        // El derivador mueve inbox.jsonl -> inbox.jsonl.proc antes de procesar; leemos ambos
        // para no perder mensajes que quedaron a medio camino entre el bridge y el derivador.
        var proc = primary + ".proc";

        foreach (var path in new[] { primary, proc })
        {
            ReadStagingFile(path, result);
        }
        return result;
    }

    private void ReadStagingFile(string path, List<WaStagingMessage> sink)
    {
        try
        {
            if (!File.Exists(path)) return;
            // FileShare.ReadWrite|Delete: el bridge/derivador puede estar escribiendo, rotando
            // o borrando el archivo concurrentemente. Si aun asi esta locked, cae a IOException.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                WaStagingMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<WaStagingMessage>(line);
                }
                catch (JsonException)
                {
                    // Linea corrupta / a medio escribir: la saltamos sin matar el tick.
                    continue;
                }
                if (msg is not null) sink.Add(msg);
            }
        }
        catch (IOException)
        {
            // Archivo en uso por el bridge/derivador: skip silencioso este tick (se reintenta
            // en el proximo). No es un error real.
        }
        catch (UnauthorizedAccessException)
        {
            // Mismo trato que IOException: lock/permisos transitorios.
        }
        catch (Exception ex)
        {
            FileLog($"WARN read staging '{path}' fallo: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Teams
    // -------------------------------------------------------------------------

    private async Task SendToFelipeAsync(string message, CancellationToken cancellationToken)
    {
        var chatId = await ResolveFelipeChatIdAsync(cancellationToken);
        if (chatId is null)
        {
            FileLog("SEND SKIP: chat 1:1 con Felipe no resoluble (FelipeUpn vacio o error Graph)");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IGraphTokenProvider>();
        var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);
        var http = _httpFactory.CreateClient("wanotifier");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages")
        {
            Content = JsonContent.Create(new { body = new { contentType = "text", content = message } })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"POST chat falló: {(int)resp.StatusCode} {Truncate(body, 300)}");
        }
    }

    // Resuelve (y cachea) el chatId del 1:1 entre Sarah (/me) y Felipe. Mismo patron que
    // AlertManager.ResolveChatIdAsync: GET /users/{upn}, GET /me, POST /chats oneOnOne.
    // Microsoft devuelve el chat existente sin duplicar.
    private async Task<string?> ResolveFelipeChatIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_felipeChatId)) return _felipeChatId;

        await _chatIdLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(_felipeChatId)) return _felipeChatId;

            var upn = _options.FelipeUpn;
            if (string.IsNullOrWhiteSpace(upn))
            {
                FileLog("WARN FelipeUpn vacio: no se puede resolver chat 1:1");
                return null;
            }

            using var scope = _scopeFactory.CreateScope();
            var tokens = scope.ServiceProvider.GetRequiredService<IGraphTokenProvider>();
            var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);
            var http = _httpFactory.CreateClient("wanotifier");

            // 1. userId de Felipe a partir de su UPN.
            using var userReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(upn)}?$select=id");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var userResp = await http.SendAsync(userReq, cancellationToken);
            userResp.EnsureSuccessStatusCode();
            var felipe = await userResp.Content.ReadFromJsonAsync<UserBrief>(cancellationToken: cancellationToken);
            var felipeId = felipe?.Id ?? throw new InvalidOperationException("No se pudo obtener userId de Felipe");

            // 2. Mi propio userId (/me).
            using var meReq = new HttpRequestMessage(HttpMethod.Get,
                "https://graph.microsoft.com/v1.0/me?$select=id");
            meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var meResp = await http.SendAsync(meReq, cancellationToken);
            meResp.EnsureSuccessStatusCode();
            var me = await meResp.Content.ReadFromJsonAsync<UserBrief>(cancellationToken: cancellationToken);
            var myId = me?.Id ?? throw new InvalidOperationException("No se pudo obtener userId de /me");

            // 3. POST /chats oneOnOne. System.Text.Json no serializa bien las claves @odata,
            //    asi que armamos el JSON a mano (mismo enfoque que AlertManager).
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
            using var chatResp = await http.SendAsync(chatReq, cancellationToken);
            chatResp.EnsureSuccessStatusCode();
            var chat = await chatResp.Content.ReadFromJsonAsync<ChatBrief>(cancellationToken: cancellationToken);
            _felipeChatId = chat?.Id ?? throw new InvalidOperationException("No se pudo obtener chat.id");
            FileLog($"chat 1:1 con Felipe resuelto: {_felipeChatId}");
            return _felipeChatId;
        }
        finally
        {
            _chatIdLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

    private static void FileLog(string message) => FileLogger.Write("WaNotifier", message);

    // -------------------------------------------------------------------------
    // DTOs
    // -------------------------------------------------------------------------

    // Una linea del staging del bridge de WhatsApp. Campos segun el formato documentado:
    // { ts (epoch seconds), chatJid, isGroup, senderJid, fromMe (bool), pushName, body }.
    private sealed class WaStagingMessage
    {
        [JsonPropertyName("ts")]        public long Ts { get; set; }
        [JsonPropertyName("chatJid")]   public string? ChatJid { get; set; }
        [JsonPropertyName("isGroup")]   public bool IsGroup { get; set; }
        [JsonPropertyName("senderJid")] public string? SenderJid { get; set; }
        [JsonPropertyName("fromMe")]    public bool FromMe { get; set; }
        [JsonPropertyName("pushName")]  public string? PushName { get; set; }
        [JsonPropertyName("body")]      public string? Body { get; set; }
    }

    private sealed class UserBrief
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    private sealed class ChatBrief
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
