using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

// Sarah-server vigila el NAS (TrueNAS) y le avisa a Felipe por Teams:
//
//   1. Cada IntervalMinutes pide GET /infra/nas al Ops Broker, firmando el token Owner con
//      la clave HMAC compartida (el handler es el unico que la tiene; el NAS solo lo toca
//      el broker, con su propia llave).
//   2. Compara los hallazgos con los de la consulta anterior (estado en disco):
//      critical/warning nuevos o agravados → aviso; los que desaparecen → "resuelto".
//   3. Si el broker o el NAS no responden FailuresBeforeAlert veces seguidas → aviso
//      (y otro cuando vuelve a responder).
//   4. Una vez por semana (WeeklySummaryDay/Hour, hora de Chile) → resumen de mantencion.
//
// Patron calcado de MetaProactiveNotifier: PeriodicTimer + scopes por tick.
[SupportedOSPlatform("windows")]
public sealed class NasMonitor : BackgroundService
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NasMonitorOptions _options;
    private readonly ClaudeOptions _claude;
    private readonly string _statePath;

    private string? _felipeChatId;

    public NasMonitor(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<NasMonitorOptions> options,
        IOptions<ClaudeOptions> claude)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _options = options.Value;
        _claude = claude.Value;
        _statePath = Path.Combine(
            Environment.GetEnvironmentVariable("HANDLER_STATE_DIR")
                ?? Path.Combine(Path.GetTempPath(), "webhook-handler", "state"),
            "nas-monitor.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            FileLog("disabled (NasMonitor:Enabled=false) — no se arma el timer");
            return;
        }
        if (string.IsNullOrEmpty(_claude.OpsBrokerUrl) || string.IsNullOrEmpty(_claude.OpsBrokerHmacKey))
        {
            FileLog("disabled: falta Claude:OpsBrokerUrl u OpsBrokerHmacKey");
            return;
        }

        var interval = Math.Max(5, _options.IntervalMinutes);
        FileLog($"started, interval={interval}min, estado en {_statePath}");

        // Primera consulta al arrancar (no esperar un intervalo completo). Mientras haya una mantencion
        // en curso (lanzada por Sarah-server) se consulta cada minuto para avisar apenas termine.
        while (!stoppingToken.IsCancellationRequested)
        {
            var maintenanceRunning = await SafeTickAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(maintenanceRunning ? 1 : interval), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> SafeTickAsync(CancellationToken ct)
    {
        try { return await TickAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) { FileLog($"FAIL outer tick: {ex.Message}"); return false; }
    }

    // Devuelve true si hay una mantencion en curso (para consultar mas seguido).
    private async Task<bool> TickAsync(CancellationToken ct)
    {
        var state = LoadState();
        var (health, error) = await FetchHealthAsync(ct);

        if (health is null)
        {
            state.ConsecutiveFailures++;
            FileLog($"consulta fallida {state.ConsecutiveFailures}/{_options.FailuresBeforeAlert}: {error}");
            // Durante una mantencion lanzada por Sarah (p. ej. el reinicio tras actualizar TrueNAS) que el
            // NAS no responda es esperable: no se avisa como caida mientras siga en curso.
            // Tope de 45 min: si no vuelve, se avisa igual.
            if (state.MaintenanceSinceUtc is { } since && DateTimeOffset.UtcNow - since < TimeSpan.FromMinutes(45))
            {
                SaveState(state);
                return true;
            }
            if (state.ConsecutiveFailures >= _options.FailuresBeforeAlert && !state.UnreachableNotified)
            {
                await SendToFelipeAsync(
                    $"🔴 NAS: no logro consultarlo hace {state.ConsecutiveFailures} intentos seguidos. " +
                    $"Si el NAS está caído, también lo están Vaultwarden, Odoo y el disco de respaldos. " +
                    $"Detalle: {Utilities.InfraMonitorWebhook.HtmlText(Truncate(error ?? "sin detalle", 300))}", ct);
                state.UnreachableNotified = true;
            }
            SaveState(state);
            return false;
        }

        if (state.UnreachableNotified)
            await SendToFelipeAsync("🟢 NAS: volví a consultarlo sin problemas.", ct);
        state.ConsecutiveFailures = 0;
        state.UnreachableNotified = false;

        var diff = NasFindingsDiff.Compute(state.Active, health.Findings, DateTimeOffset.UtcNow,
            NasFindingsDiff.UnknownPrefixes(health.Errors));
        var message = NasFindingsDiff.FormatChanges(diff);
        if (message is not null)
        {
            await SendToFelipeAsync(message, ct);
            FileLog($"NOTIFIED {diff.Raised.Count} nuevos, {diff.Resolved.Count} resueltos");
        }
        state.Active = diff.Active;

        var now = DateTimeOffset.UtcNow;
        if (NasFindingsDiff.IsWeeklySummaryDue(
                TrustRingPromptPolicy.InChile(now), state.LastWeeklySummaryUtc is { } last ? TrustRingPromptPolicy.InChile(last) : null,
                _options.WeeklySummaryDay, _options.WeeklySummaryHour))
        {
            await SendToFelipeAsync(NasFindingsDiff.FormatWeeklySummary(health), ct);
            state.LastWeeklySummaryUtc = now;
            FileLog("resumen semanal enviado");
        }

        // Mantenciones lanzadas por Sarah-server: aviso al terminar (exito o falla), una sola vez.
        var finished = NasFindingsDiff.FinishedOperations(health.Operations, state.ReportedOperations);
        foreach (var op in finished)
        {
            await SendToFelipeAsync(NasFindingsDiff.FormatOperation(op), ct);
            state.ReportedOperations.Add(op.JobId);
            FileLog($"NOTIFIED operacion {op.JobId} {op.Status}");
        }
        state.ReportedOperations.IntersectWith(health.Operations.Select(o => o.JobId));
        var running = health.Operations.Any(o => o.Status == "running");
        state.MaintenanceSinceUtc = running ? state.MaintenanceSinceUtc ?? now : null;

        SaveState(state);
        return running;
    }

    private async Task<(NasHealthDto? Health, string? Error)> FetchHealthAsync(CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient("nasmonitor");
            using var req = new HttpRequestMessage(HttpMethod.Get, _claude.OpsBrokerUrl.TrimEnd('/') + "/infra/nas");
            req.Headers.Add("X-Owner-Token", OpsTokenMinter.Mint(_claude.OpsBrokerHmacKey));
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (null, $"broker {(int)resp.StatusCode}: {Truncate(body, 300)}");
            return (JsonSerializer.Deserialize<NasHealthDto>(body, WebJson), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Estado en disco (sobrevive reinicios del pool)
    // -------------------------------------------------------------------------

    private NasMonitorState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
                return JsonSerializer.Deserialize<NasMonitorState>(File.ReadAllText(_statePath), WebJson) ?? new();
        }
        catch (Exception ex)
        {
            FileLog($"WARN estado ilegible, se parte de cero: {ex.Message}");
        }
        return new();
    }

    private void SaveState(NasMonitorState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, WebJson));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog($"WARN no se pudo guardar el estado: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Teams (mismo patrón que MetaProactiveNotifier)
    // -------------------------------------------------------------------------

    private async Task SendToFelipeAsync(string message, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IGraphTokenProvider>();
        var accessToken = await tokens.GetAccessTokenAsync(ct);
        var http = _httpFactory.CreateClient("nasmonitor");

        var chatId = _felipeChatId ??= await ResolveFelipeChatIdAsync(http, accessToken, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages")
        {
            Content = JsonContent.Create(new { body = new { contentType = "html", content = message } })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"POST chat falló: {(int)resp.StatusCode} {Truncate(body, 300)}");
        }
    }

    private async Task<string> ResolveFelipeChatIdAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.FelipeUpn))
            throw new InvalidOperationException("NasMonitor:FelipeUpn vacío");

        async Task<string> GetIdAsync(string url)
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await http.SendAsync(r, ct);
            resp.EnsureSuccessStatusCode();
            var brief = await resp.Content.ReadFromJsonAsync<IdBrief>(cancellationToken: ct);
            return brief?.Id ?? throw new InvalidOperationException($"sin id en {url}");
        }

        var felipeId = await GetIdAsync(
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_options.FelipeUpn)}?$select=id");
        var myId = await GetIdAsync("https://graph.microsoft.com/v1.0/me?$select=id");

        // POST /chats oneOnOne devuelve el chat existente si ya existe. JSON manual por @odata.
        var jsonStr = $@"{{
          ""chatType"": ""oneOnOne"",
          ""members"": [
            {{ ""@odata.type"": ""#microsoft.graph.aadUserConversationMember"", ""roles"": [""owner""],
               ""user@odata.bind"": ""https://graph.microsoft.com/v1.0/users('{myId}')"" }},
            {{ ""@odata.type"": ""#microsoft.graph.aadUserConversationMember"", ""roles"": [""owner""],
               ""user@odata.bind"": ""https://graph.microsoft.com/v1.0/users('{felipeId}')"" }}
          ]
        }}";
        using var chatReq = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/chats")
        {
            Content = new StringContent(jsonStr, Encoding.UTF8, "application/json")
        };
        chatReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var chatResp = await http.SendAsync(chatReq, ct);
        chatResp.EnsureSuccessStatusCode();
        var chat = await chatResp.Content.ReadFromJsonAsync<IdBrief>(cancellationToken: ct);
        var chatId = chat?.Id ?? throw new InvalidOperationException("sin chat.id");
        FileLog($"chat 1:1 con Felipe resuelto: {chatId}");
        return chatId;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

    private static void FileLog(string message) => FileLogger.Write("NasMonitor", message);

    private sealed class IdBrief
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}

// -----------------------------------------------------------------------------
// Logica pura (sin E/S) del monitor: diff de hallazgos, agenda semanal y textos.
// -----------------------------------------------------------------------------

public static class NasFindingsDiff
{
    public sealed record Result(
        IReadOnlyList<NasFindingDto> Raised,
        IReadOnlyList<KeyValuePair<string, TrackedFinding>> Resolved,
        Dictionary<string, TrackedFinding> Active);

    // Solo critical/warning se avisan al momento; maintenance queda para el resumen semanal.
    public static bool IsAlertable(string severity) => Rank(severity) > 0;

    public static Result Compute(
        IReadOnlyDictionary<string, TrackedFinding> previous, IEnumerable<NasFindingDto> current, DateTimeOffset now,
        IReadOnlyCollection<string>? unknownPrefixes = null)
    {
        var raised = new List<NasFindingDto>();
        var active = new Dictionary<string, TrackedFinding>(StringComparer.Ordinal);

        foreach (var f in current.Where(f => IsAlertable(f.Severity)))
        {
            if (active.ContainsKey(f.Key)) continue;
            previous.TryGetValue(f.Key, out var before);
            // Nuevo, o agravado (warning → critical): se avisa. Si sigue igual, silencio.
            if (before is null || Rank(f.Severity) > Rank(before.Severity))
                raised.Add(f);
            active[f.Key] = new TrackedFinding(f.Severity, f.Message, before?.FirstSeenUtc ?? now);
        }

        // Lo que no se pudo consultar esta vez no esta "resuelto": se arrastra tal cual.
        foreach (var p in previous)
        {
            if (!active.ContainsKey(p.Key) && unknownPrefixes?.Any(x => p.Key.StartsWith(x, StringComparison.Ordinal)) == true)
                active[p.Key] = p.Value;
        }

        var resolved = previous.Where(p => !active.ContainsKey(p.Key)).ToList();
        return new Result(raised, resolved, active);
    }

    // Errores del broker ("pool.query: ...") → prefijos de clave cuyos hallazgos quedaron sin evaluar.
    public static IReadOnlyCollection<string> UnknownPrefixes(IEnumerable<string> errors)
    {
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in errors)
        {
            var method = e.Split(':')[0].Trim();
            var prefix = method switch
            {
                "pool.query" => "pool:",
                "app.query" => "app:",
                "pool.snapshottask.query" => "snapshot:",
                "service.query" => "service:",
                "iscsi.global.sessions" => "iscsi:",
                "alert.list" => "alert:",
                _ => null,
            };
            if (prefix is not null) prefixes.Add(prefix);
        }
        return prefixes;
    }

    // Los mensajes van a Teams como HTML (en texto plano Teams junta los saltos de linea).
    public static string? FormatChanges(Result diff)
    {
        if (diff.Raised.Count == 0 && diff.Resolved.Count == 0) return null;

        var sb = new StringBuilder("<p><b>NAS</b></p><ul>");
        foreach (var f in diff.Raised.OrderByDescending(f => Rank(f.Severity)))
            sb.Append("<li>").Append(Rank(f.Severity) == 2 ? "🔴 " : "🟠 ").Append(Html(f.Message)).Append("</li>");
        foreach (var r in diff.Resolved)
            sb.Append("<li>🟢 Resuelto: ").Append(Html(r.Value.Message)).Append("</li>");
        return sb.Append("</ul>").ToString();
    }

    // El resumen toca si ya paso el ultimo "dia/hora" programado y no se envio desde entonces.
    public static bool IsWeeklySummaryDue(
        DateTimeOffset nowLocal, DateTimeOffset? lastSentLocal, DayOfWeek day, int hour)
    {
        var daysBack = ((int)nowLocal.DayOfWeek - (int)day + 7) % 7;
        var slot = new DateTimeOffset(nowLocal.Date.AddDays(-daysBack).AddHours(hour), nowLocal.Offset);
        if (slot > nowLocal) slot = slot.AddDays(-7);
        return lastSentLocal is null || lastSentLocal < slot;
    }

    public static string FormatWeeklySummary(NasHealthDto h)
    {
        var sb = new StringBuilder("<p><b>📋 Resumen semanal del NAS</b></p><ul>");
        var system = $"TrueNAS {h.Version ?? "?"}";
        if (h.UptimeDays is not null) system += $", {h.UptimeDays} días encendido";
        sb.Append("<li>").Append(Html(system)).Append("</li>");

        foreach (var p in h.Pools)
        {
            var scrub = p.ScanEnd is null ? "sin registro de scrub" : $"último scrub {TrustRingPromptPolicy.ToChile(p.ScanEnd)}";
            sb.Append("<li>").Append(Html($"Pool {p.Name}: {p.Status}, {scrub}")).Append("</li>");
        }

        var running = h.Apps.Count(a => string.Equals(a.State, "RUNNING", StringComparison.OrdinalIgnoreCase));
        sb.Append("<li>").Append(Html($"Apps: {running} de {h.Apps.Count} corriendo")).Append("</li></ul>");

        var alertable = h.Findings.Where(f => IsAlertable(f.Severity)).ToList();
        var maintenance = h.Findings.Where(f => !IsAlertable(f.Severity)).ToList();

        if (alertable.Count > 0)
        {
            sb.Append("<p><b>Problemas abiertos</b></p><ul>");
            foreach (var f in alertable.OrderByDescending(f => Rank(f.Severity)))
                sb.Append("<li>").Append(Rank(f.Severity) == 2 ? "🔴 " : "🟠 ").Append(Html(f.Message)).Append("</li>");
            sb.Append("</ul>");
        }

        if (maintenance.Count > 0)
        {
            sb.Append("<p><b>Mantención pendiente</b></p><ul>");
            // Las apps con actualizacion ("app:<nombre>:upgrade") se agrupan en una sola linea.
            var upgrades = maintenance
                .Where(f => f.Key.StartsWith("app:", StringComparison.Ordinal) && f.Key.EndsWith(":upgrade", StringComparison.Ordinal))
                .ToList();
            if (upgrades.Count > 0)
            {
                var names = upgrades.Select(f => f.Key["app:".Length..^":upgrade".Length]);
                sb.Append("<li>").Append(Html($"Apps con actualización: {string.Join(", ", names)}")).Append("</li>");
            }
            foreach (var f in maintenance.Except(upgrades))
                sb.Append("<li>").Append(Html(f.Message)).Append("</li>");
            sb.Append("</ul><p>Si quiere avanzar con algo, dígame y le propongo un plan con horario (fuera de la ventana de respaldos de 01:30 a 06:00).</p>");
        }
        else if (alertable.Count == 0)
        {
            sb.Append("<p>Todo al día, sin pendientes.</p>");
        }

        if (h.Errors.Count > 0)
            sb.Append("<p><i>").Append(Html($"No pude consultar: {string.Join("; ", h.Errors.Select(e => e.Split(':')[0]))}.")).Append("</i></p>");

        return sb.ToString();
    }

    // Operaciones terminadas (success / failed) que aun no se avisaron.
    public static IReadOnlyList<NasOperationDto> FinishedOperations(
        IEnumerable<NasOperationDto> operations, IReadOnlySet<long> reported) =>
        operations.Where(o => o.Status is "success" or "failed" && !reported.Contains(o.JobId)).ToList();

    public static string FormatOperation(NasOperationDto op)
    {
        var minutes = op.FinishedUtc is { } end ? $" ({Math.Max(1, (int)Math.Round((end - op.StartedUtc).TotalMinutes))} min)" : "";
        return op.Status == "success"
            ? $"✅ NAS: terminó la {Html(op.Description)}{minutes}. Todo en orden."
            : $"❌ NAS: falló la {Html(op.Description)}{minutes}: {Html(op.Error ?? "sin detalle")}. Revíselo conmigo antes de reintentar.";
    }

    private static string Html(string text) => Utilities.InfraMonitorWebhook.HtmlText(text);

    private static int Rank(string severity) => severity switch
    {
        "critical" => 2,
        "warning" => 1,
        _ => 0,
    };
}

public sealed record TrackedFinding(string Severity, string Message, DateTimeOffset FirstSeenUtc);

public sealed class NasMonitorState
{
    public Dictionary<string, TrackedFinding> Active { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? LastWeeklySummaryUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public bool UnreachableNotified { get; set; }

    // Jobs de mantencion ya avisados (no repetir) y desde cuando hay una en curso.
    public HashSet<long> ReportedOperations { get; set; } = [];
    public DateTimeOffset? MaintenanceSinceUtc { get; set; }
}

// Forma de la respuesta de GET /infra/nas del broker (solo lo que usa el monitor).
public sealed class NasHealthDto
{
    public string? Version { get; set; }
    public int? UptimeDays { get; set; }
    public List<NasPoolDto> Pools { get; set; } = [];
    public List<NasAppDto> Apps { get; set; } = [];
    public List<NasFindingDto> Findings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<NasOperationDto> Operations { get; set; } = [];
}

public sealed record NasOperationDto(
    long JobId, string Description, string Status, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc, string? Error);

public sealed record NasPoolDto(string Name, string Status, DateTimeOffset? ScanEnd);

public sealed record NasAppDto(string Name, string State);

public sealed record NasFindingDto(string Key, string Severity, string Message);
