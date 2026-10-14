using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Phipes.Assistant.OpsBroker.Configuration;
using Phipes.Assistant.OpsBroker.Utilities;

namespace Phipes.Assistant.OpsBroker.Services;

// Acciones de mantencion del NAS para turnos del Owner (Sarah-server por Teams, siempre con la
// confirmacion explicita de Felipe antes de llamar). Usa una llave propia (NasOpsApiToken) con solo
// APPS_WRITE + SYSTEM_UPDATE_WRITE ademas de lectura: no puede borrar datos, tocar pools ni shares.
// Resguardos: deshabilitado sin llave, rechaza la ventana de respaldos, una operacion a la vez, solo
// apps que existen y tienen actualizacion, y cada accion queda en el log "NasOps".
public sealed class NasOpsService(IOptions<OpsBrokerOptions> options)
{
    private static readonly TimeZoneInfo ChileTz = ResolveChileTz();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly OpsBrokerOptions _o = options.Value;

    // Metodos de TrueNAS que cuentan como "mantencion en curso" para impedir dos a la vez.
    private static readonly string[] MaintenanceMethods = ["app.upgrade", "update.run", "update.download", "update.update"];

    public async Task<NasOpResult> UpgradeAppAsync(string appName, CancellationToken ct)
    {
        return await RunGuardedAsync($"app.upgrade {appName}", async rpc =>
        {
            var app = Items(await rpc.CallAsync("app.query", ct,
                    new object[] { new object[] { "name", "=", appName } }))
                .FirstOrDefault();
            if (app is null) return NasOpResult.Fail(404, $"No existe la app '{appName}' en el NAS.");
            if (app["upgrade_available"]?.GetValue<bool>() != true)
                return NasOpResult.Fail(409, $"La app '{appName}' no tiene actualización disponible.");

            var job = await rpc.CallAsync("app.upgrade", ct, appName);
            return NasOpResult.Started(job!.GetValue<long>(), $"Actualización de la app {appName} iniciada.");
        }, ct);
    }

    // Aplica la actualizacion de TrueNAS disponible (la descarga si hace falta) y reinicia el NAS.
    // Mientras reinicia caen Vaultwarden, Odoo, las demas apps y el disco iSCSI de respaldos.
    public async Task<NasOpResult> UpdateSystemAsync(CancellationToken ct)
    {
        return await RunGuardedAsync("update.run reboot", async rpc =>
        {
            var status = await rpc.CallAsync("update.status", ct);
            var version = status?["status"]?["new_version"]?["version"]?.GetValue<string>();
            if (string.IsNullOrEmpty(version))
                return NasOpResult.Fail(409, "No hay actualización de TrueNAS disponible.");

            var job = await rpc.CallAsync("update.run", ct, new Dictionary<string, object?> { ["reboot"] = true });
            return NasOpResult.Started(job!.GetValue<long>(),
                $"Actualización de TrueNAS a {version} iniciada; el NAS se reiniciará al terminar de aplicarla.");
        }, ct);
    }

    // Estado de un job (avance de una accion). Durante el reinicio del NAS esto falla: es esperable.
    public async Task<JsonNode?> GetJobAsync(long jobId, CancellationToken ct)
    {
        // Con la llave de acciones: un usuario no administrador solo ve sus propios jobs.
        var token = string.IsNullOrWhiteSpace(_o.NasOpsApiToken) ? _o.NasApiToken : _o.NasOpsApiToken;
        await using var rpc = await TrueNasRpcClient.ConnectAsync(_o, token, ct);
        var job = Items(await rpc.CallAsync("core.get_jobs", ct,
            new object[] { new object[] { "id", "=", jobId } })).FirstOrDefault();
        if (job is null) return null;
        return new JsonObject
        {
            ["id"] = jobId,
            ["method"] = job["method"]?.DeepClone(),
            ["state"] = job["state"]?.DeepClone(),
            ["percent"] = job["progress"]?["percent"]?.DeepClone(),
            ["description"] = job["progress"]?["description"]?.DeepClone(),
            ["error"] = job["error"]?.DeepClone(),
            ["timeStarted"] = job["time_started"]?.DeepClone(),
            ["timeFinished"] = job["time_finished"]?.DeepClone(),
        };
    }

    private async Task<NasOpResult> RunGuardedAsync(
        string what, Func<TrueNasRpcClient, Task<NasOpResult>> action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.NasOpsApiToken))
            return Log(what, NasOpResult.Fail(503, "Las acciones sobre el NAS no están habilitadas (falta NasOpsApiToken)."));

        var nowChile = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ChileTz);
        if (IsInBlockedWindow(nowChile.TimeOfDay, _o.NasOpsBlockedFrom, _o.NasOpsBlockedTo))
            return Log(what, NasOpResult.Fail(409,
                $"Son las {nowChile:HH:mm} en Chile: dentro de la ventana de respaldos ({_o.NasOpsBlockedFrom}–{_o.NasOpsBlockedTo}) no se ejecutan mantenciones."));

        if (!await Gate.WaitAsync(0, ct))
            return Log(what, NasOpResult.Fail(409, "Ya hay una acción sobre el NAS iniciándose."));
        try
        {
            await using var rpc = await TrueNasRpcClient.ConnectAsync(_o, _o.NasOpsApiToken, ct);
            var running = Items(await rpc.CallAsync("core.get_jobs", ct,
                    new object[] { new object[] { "state", "in", new[] { "RUNNING", "WAITING" } } }))
                .Select(j => j["method"]?.GetValue<string>())
                .FirstOrDefault(m => m is not null && MaintenanceMethods.Contains(m));
            if (running is not null)
                return Log(what, NasOpResult.Fail(409, $"Ya hay una mantención en curso en el NAS ({running}). Espere a que termine."));

            return Log(what, await action(rpc));
        }
        catch (Exception ex)
        {
            return Log(what, NasOpResult.Fail(502, $"Error al hablar con el NAS: {ex.Message}"));
        }
        finally
        {
            Gate.Release();
        }
    }

    // Ventana [from, to) en hora local; si from > to cruza la medianoche.
    public static bool IsInBlockedWindow(TimeSpan now, string from, string to)
    {
        if (!TimeSpan.TryParse(from, out var f) || !TimeSpan.TryParse(to, out var t) || f == t) return false;
        return f < t ? now >= f && now < t : now >= f || now < t;
    }

    private static NasOpResult Log(string what, NasOpResult r)
    {
        FileLogger.Write("NasOps", $"{what} -> {r.Status} job={r.JobId?.ToString() ?? "-"} {r.Message}");
        return r;
    }

    private static IEnumerable<JsonNode> Items(JsonNode? node) =>
        node is JsonArray a ? a.Where(x => x is not null).Select(x => x!) : [];

    private static TimeZoneInfo ResolveChileTz()
    {
        foreach (var id in new[] { "Pacific SA Standard Time", "America/Santiago" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Chile", TimeSpan.FromHours(-3), "Chile", "Chile");
    }
}

// Status: codigo HTTP que devuelve el endpoint (202 = iniciada).
public sealed record NasOpResult(int Status, string Message, long? JobId)
{
    public static NasOpResult Started(long jobId, string message) => new(202, message, jobId);
    public static NasOpResult Fail(int status, string message) => new(status, message, null);
}
