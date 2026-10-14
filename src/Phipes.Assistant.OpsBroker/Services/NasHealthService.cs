using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Phipes.Assistant.OpsBroker.Configuration;
using Phipes.Assistant.OpsBroker.Utilities;

namespace Phipes.Assistant.OpsBroker.Services;

// Estado de salud del NAS (TrueNAS) por JSON-RPC: sistema, actualizaciones, pools, scrub,
// apps, tareas de snapshot, servicios (SMB/iSCSI), sesiones iSCSI y alertas propias
// del NAS. Cada consulta es independiente: si un metodo falla (o no existe en esta version)
// queda en Errors y el resto del informe igual se entrega.
//
// Ademas de los datos crudos, devuelve Findings: hallazgos con clave estable y severidad.
// El monitor del WebhookHandler compara las claves entre una consulta y la siguiente para
// avisar a Felipe solo de lo nuevo y de lo resuelto. Nunca devuelve credenciales.
public sealed class NasHealthService(IOptions<OpsBrokerOptions> options)
{
    private readonly OpsBrokerOptions _o = options.Value;

    public async Task<NasHealth> GetHealthAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        ct = timeout.Token;

        await using var rpc = await TrueNasRpcClient.ConnectAsync(_o, ct);
        var h = new NasHealth { CollectedUtc = DateTimeOffset.UtcNow };

        await Try(h, "system.info", async () =>
        {
            var info = await rpc.CallAsync("system.info", ct);
            h.Hostname = Str(info?["hostname"]);
            h.Version = Str(info?["version"]);
            var uptime = info?["uptime_seconds"]?.GetValue<double>();
            if (uptime is not null) h.UptimeDays = (int)(uptime.Value / 86400);
        });

        await Try(h, "update", async () => h.UpdateAvailable = await GetAvailableUpdateAsync(rpc, ct));

        await Try(h, "pool.query", async () =>
        {
            foreach (var p in Items(await rpc.CallAsync("pool.query", ct)))
            {
                var scan = p["scan"];
                h.Pools.Add(new NasPool(
                    Str(p["name"]) ?? "?",
                    Str(p["status"]) ?? "?",
                    p["healthy"]?.GetValue<bool>() ?? false,
                    Long(p["size"]),
                    Long(p["allocated"]),
                    Str(scan?["function"]),
                    Str(scan?["state"]),
                    Date(scan?["end_time"]),
                    Long(scan?["errors"]) ?? 0));
            }
        });

        await Try(h, "app.query", async () =>
        {
            foreach (var a in Items(await rpc.CallAsync("app.query", ct)))
            {
                h.Apps.Add(new NasApp(
                    Str(a["name"]) ?? "?",
                    Str(a["state"]) ?? "?",
                    a["upgrade_available"]?.GetValue<bool>() ?? false,
                    Str(a["human_version"]) ?? Str(a["version"])));
            }
        });

        await Try(h, "pool.snapshottask.query", async () =>
        {
            foreach (var t in Items(await rpc.CallAsync("pool.snapshottask.query", ct)))
            {
                var schedule = t["schedule"];
                var daily = Str(schedule?["dom"]) == "*" && Str(schedule?["dow"]) == "*";
                h.SnapshotTasks.Add(new NasSnapshotTask(
                    Str(t["dataset"]) ?? "?",
                    t["enabled"]?.GetValue<bool>() ?? false,
                    daily,
                    Str(t["state"]?["state"]),
                    Date(t["state"]?["datetime"]),
                    Str(t["state"]?["error"])));
            }
        });

        await Try(h, "service.query", async () =>
        {
            var filter = new object[] { new object[] { "service", "in", new[] { "cifs", "iscsitarget" } } };
            foreach (var s in Items(await rpc.CallAsync("service.query", ct, [filter])))
            {
                h.Services.Add(new NasService(
                    Str(s["service"]) ?? "?",
                    Str(s["state"]) ?? "?",
                    s["enable"]?.GetValue<bool>() ?? false));
            }
        });

        await Try(h, "iscsi.global.sessions", async () =>
        {
            foreach (var s in Items(await rpc.CallAsync("iscsi.global.sessions", ct)))
            {
                var initiator = Str(s["initiator"]);
                if (initiator is not null) h.IscsiInitiators.Add(initiator);
            }
        });

        // SMART: TrueNAS 25.10 elimino la API de pruebas SMART (smart.test.*); los problemas de
        // disco llegan como alertas propias del NAS (alert.list), que se evaluan abajo.
        await Try(h, "alert.list", async () =>
        {
            foreach (var a in Items(await rpc.CallAsync("alert.list", ct)))
            {
                if (a["dismissed"]?.GetValue<bool>() == true) continue;
                // "key" identifica la condicion (estable entre re-emisiones); el uuid cambia
                // cada vez que el NAS recrea la alerta y provocaria avisos repetidos.
                h.Alerts.Add(new NasAlert(
                    Str(a["key"]) ?? Str(a["uuid"]) ?? "?",
                    Str(a["klass"]) ?? "?",
                    Str(a["level"]) ?? "INFO",
                    Str(a["formatted"]) ?? "",
                    Date(a["datetime"])));
            }
        });

        h.Findings.AddRange(NasHealthEvaluator.Evaluate(h, _o));
        FileLogger.Write("Nas", $"health OK: {h.Findings.Count} hallazgos, {h.Errors.Count} consultas fallidas");
        return h;
    }

    // TrueNAS 25.10 trae update.status (perfiles de actualizacion); versiones previas usan
    // update.check_available. Se intenta el nuevo y, si no existe, el antiguo.
    private static async Task<string?> GetAvailableUpdateAsync(TrueNasRpcClient rpc, CancellationToken ct)
    {
        try
        {
            var status = await rpc.CallAsync("update.status", ct);
            return Str(status?["status"]?["new_version"]?["version"]);
        }
        catch (TrueNasRpcException)
        {
            var check = await rpc.CallAsync("update.check_available", ct);
            return Str(check?["status"]) == "AVAILABLE" ? Str(check?["version"]) ?? "disponible" : null;
        }
    }

    private static async Task Try(NasHealth h, string what, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            h.Errors.Add($"{what}: {ex.Message}");
            FileLogger.Write("Nas", $"WARN {what}: {ex.Message}");
        }
    }

    private static IEnumerable<JsonNode> Items(JsonNode? node) =>
        node is JsonArray arr ? arr.OfType<JsonNode>() : [];

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToString();

    private static long? Long(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, out l) ? l : null;
    }

    // Las fechas llegan como {"$date": <epoch ms>}; se aceptan tambien epoch o ISO directos.
    private static DateTimeOffset? Date(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonObject obj) node = obj["$date"];
        var ms = Long(node);
        if (ms is not null) return DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);
        return DateTimeOffset.TryParse(Str(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : null;
    }
}

public sealed class NasHealth
{
    public DateTimeOffset CollectedUtc { get; set; }
    public string? Hostname { get; set; }
    public string? Version { get; set; }
    public int? UptimeDays { get; set; }
    public string? UpdateAvailable { get; set; }
    public List<NasPool> Pools { get; } = [];
    public List<NasApp> Apps { get; } = [];
    public List<NasSnapshotTask> SnapshotTasks { get; } = [];
    public List<NasService> Services { get; } = [];
    public List<string> IscsiInitiators { get; } = [];
    public List<NasAlert> Alerts { get; } = [];
    public List<NasFinding> Findings { get; } = [];
    public List<string> Errors { get; } = [];
}

public sealed record NasPool(
    string Name, string Status, bool Healthy, long? SizeBytes, long? AllocatedBytes,
    string? ScanFunction, string? ScanState, DateTimeOffset? ScanEnd, long ScanErrors);

public sealed record NasApp(string Name, string State, bool UpgradeAvailable, string? Version);

public sealed record NasSnapshotTask(
    string Dataset, bool Enabled, bool Daily, string? State, DateTimeOffset? LastRun, string? Error);

public sealed record NasService(string Name, string State, bool Enabled);

public sealed record NasAlert(string Id, string Klass, string Level, string Text, DateTimeOffset? Since);

// Severity: "critical" | "warning" | "maintenance". Key es estable entre consultas
// (ej. "pool:Tank:status", "app:odoo:state") para que el monitor detecte nuevo / resuelto.
public sealed record NasFinding(string Key, string Severity, string Message);
