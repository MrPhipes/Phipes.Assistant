using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Phipes.Assistant.OpsBroker.Configuration;
using Phipes.Assistant.OpsBroker.Utilities;

namespace Phipes.Assistant.OpsBroker.Services;

// Registro de las acciones de mantencion lanzadas sobre el NAS, para que Sarah-server no tenga que
// esperar el resultado dentro del turno: el broker recuerda cada accion (job, que, cuando, estimacion)
// y GET /infra/nas devuelve su estado; el NasMonitor del handler avisa a Felipe cuando termina.
//
// Resolucion del estado: por el job de TrueNAS mientras exista; tras el reinicio del NAS los jobs se
// pierden, asi que la actualizacion del sistema se da por exitosa cuando el NAS vuelve con la version
// destino, y una app cuando ya no tiene actualizacion pendiente.
public sealed class NasOpsTracker(IOptions<OpsBrokerOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(45);
    private readonly OpsBrokerOptions _o = options.Value;
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "state", "nas-ops.json");
    private readonly object _lock = new();

    public void Record(long jobId, string kind, string target, string description, int estimatedMinutes)
    {
        lock (_lock)
        {
            var ops = Load();
            ops.Add(new NasOperation
            {
                JobId = jobId, Kind = kind, Target = target, Description = description,
                EstimatedMinutes = estimatedMinutes, StartedUtc = DateTimeOffset.UtcNow, Status = "running",
            });
            Save(ops);
        }
    }

    // Actualiza el estado de las acciones en curso y devuelve las de los ultimos 7 dias.
    public async Task<List<NasOperation>> ResolveAsync(CancellationToken ct)
    {
        List<NasOperation> ops;
        lock (_lock) ops = Load();
        var running = ops.Where(o => o.Status == "running").ToList();

        if (running.Count > 0 && !string.IsNullOrWhiteSpace(_o.NasOpsApiToken))
        {
            try
            {
                await using var rpc = await TrueNasRpcClient.ConnectAsync(_o, _o.NasOpsApiToken, ct);
                var version = (await rpc.CallAsync("system.info", ct))?["version"]?.GetValue<string>();
                foreach (var op in running)
                    await ResolveOneAsync(rpc, op, version, ct);
            }
            catch (Exception ex)
            {
                // NAS sin respuesta (p. ej. reiniciando): siguen "running" salvo que se pase del plazo.
                FileLogger.Write("NasOps", $"resolve: NAS sin respuesta ({ex.Message})");
            }

            foreach (var op in running.Where(o => o.Status == "running" && DateTimeOffset.UtcNow - o.StartedUtc > GiveUpAfter))
                Finish(op, "failed", $"No terminó en {GiveUpAfter.TotalMinutes:0} minutos; revisar el NAS.");

            lock (_lock)
            {
                var current = Load();
                foreach (var op in running)
                {
                    var i = current.FindIndex(x => x.JobId == op.JobId);
                    if (i >= 0) current[i] = op;
                }
                Save(current);
                ops = current;
            }
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        return ops.Where(o => o.StartedUtc >= cutoff).ToList();
    }

    private static async Task ResolveOneAsync(TrueNasRpcClient rpc, NasOperation op, string? version, CancellationToken ct)
    {
        var job = (await rpc.CallAsync("core.get_jobs", ct,
            [new object[] { new object[] { "id", "=", op.JobId } }]) as JsonArray)?.FirstOrDefault();
        var state = job?["state"]?.GetValue<string>();

        if (op.Kind == "system")
        {
            if (state is "FAILED" or "ABORTED")
                Finish(op, "failed", job?["error"]?.GetValue<string>() ?? state);
            else if (version is not null && version == op.Target)
                Finish(op, "success", null);
            return;
        }

        if (state == "SUCCESS") Finish(op, "success", null);
        else if (state is "FAILED" or "ABORTED") Finish(op, "failed", job?["error"]?.GetValue<string>() ?? state);
        else if (job is null)
        {
            var app = (await rpc.CallAsync("app.query", ct,
                [new object[] { new object[] { "name", "=", op.Target } }]) as JsonArray)?.FirstOrDefault();
            if (app?["upgrade_available"]?.GetValue<bool>() == false) Finish(op, "success", null);
        }
    }

    private static void Finish(NasOperation op, string status, string? error)
    {
        op.Status = status;
        op.Error = error;
        op.FinishedUtc = DateTimeOffset.UtcNow;
        FileLogger.Write("NasOps", $"job {op.JobId} ({op.Description}) -> {status} {error}");
    }

    private List<NasOperation> Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<NasOperation>>(File.ReadAllText(_path), Json) ?? [] : []; }
        catch { return []; }
    }

    private void Save(List<NasOperation> ops)
    {
        var keep = ops.Where(o => o.StartedUtc >= DateTimeOffset.UtcNow.AddDays(-30)).TakeLast(50).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(keep, Json));
    }
}

public sealed class NasOperation
{
    public long JobId { get; set; }
    public string Kind { get; set; } = "";          // "app" | "system"
    public string Target { get; set; } = "";        // nombre de la app o version destino de TrueNAS
    public string Description { get; set; } = "";
    public int EstimatedMinutes { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public string Status { get; set; } = "running"; // running | success | failed
    public DateTimeOffset? FinishedUtc { get; set; }
    public string? Error { get; set; }
}
