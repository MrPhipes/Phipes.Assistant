using System.Text.Json;
using System.Text.Json.Serialization;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

// Persiste el estado actual de las Graph subscriptions a disco para sobrevivir
// reinicios del app pool. Sin este store, el id nuevo generado por auto-recovery
// del LifecycleHandler vive solo en memoria; al primer reinicio el Renewer
// vuelve a leer el id viejo del User Secrets y empieza a fallar con 404.
//
// Filosofia: el state file es source-of-truth para los Ids; el User Secrets aporta
// los demas campos (Resource, ExpirationMinutes, etc). Al startup se hace merge:
// para cada def del config, si el state file tiene un id mas reciente lo usamos.
public interface ISubscriptionStateStore
{
    // Carga el state persistido (id por label) y lo aplica sobre las definiciones
    // que vienen de RenewerOptions. Mutates in-place el array recibido.
    void ApplyPersistedIds(SubscriptionDefinition[] definitions);

    // Persiste el state actual (id por label) tras un auto-recovery.
    Task SaveAsync(IEnumerable<SubscriptionDefinition> definitions, CancellationToken cancellationToken = default);
}

public sealed class SubscriptionStateStore : ISubscriptionStateStore
{
    private readonly string _path;
    private readonly ILogger<SubscriptionStateStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SubscriptionStateStore(ILogger<SubscriptionStateStore> logger)
    {
        _logger = logger;
        // Configurable via env var. Default: junto al log y attachments en el ClaudeHome
        // de Sarah (mismo profile compartido entre instancias), para que sea visible y
        // versionable junto con el resto del estado operacional.
        _path = Environment.GetEnvironmentVariable("HANDLER_SUBSCRIPTION_STATE_PATH")
                ?? Path.Combine(
                    Environment.GetEnvironmentVariable("HANDLER_STATE_DIR")
                        ?? Path.Combine(Path.GetTempPath(), "webhook-handler", "state"),
                    "subscriptions.json");
    }

    public void ApplyPersistedIds(SubscriptionDefinition[] definitions)
    {
        if (!File.Exists(_path))
        {
            FileLog($"state file no existe en {_path}, usando ids del config tal cual");
            return;
        }
        try
        {
            var json = File.ReadAllText(_path);
            var persisted = JsonSerializer.Deserialize<PersistedState>(json);
            if (persisted?.Subscriptions is null) { FileLog("state file vacio o malformado"); return; }

            foreach (var def in definitions)
            {
                var match = persisted.Subscriptions.FirstOrDefault(p =>
                    string.Equals(p.Label, def.Label, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;
                if (!string.Equals(match.Id, def.Id, StringComparison.OrdinalIgnoreCase))
                {
                    FileLog($"override id label={def.Label} configId={def.Id} -> persistedId={match.Id} (updatedAt={match.UpdatedAt:o})");
                    def.Id = match.Id;
                }
            }
        }
        catch (Exception ex)
        {
            FileLog($"FAIL leyendo state file: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task SaveAsync(IEnumerable<SubscriptionDefinition> definitions, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var snapshot = new PersistedState
            {
                Subscriptions = definitions.Select(d => new PersistedSubscription
                {
                    Label = d.Label,
                    Id = d.Id,
                    Resource = d.Resource,
                    UpdatedAt = DateTimeOffset.UtcNow
                }).ToList()
            };
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            // Escritura atomica: write a temp + rename. Asi no corrompe el file si el
            // proceso muere a mitad de escritura.
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, cancellationToken);
            // File.Move con overwrite true es atomico en NTFS si tmp y _path estan en
            // el mismo volumen.
            File.Move(tmp, _path, overwrite: true);
            FileLog($"state persistido en {_path}: {snapshot.Subscriptions.Count} subscriptions");
        }
        catch (Exception ex)
        {
            FileLog($"FAIL persistiendo state: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void FileLog(string message) => FileLogger.Write("SubState", message);

    private sealed class PersistedState
    {
        [JsonPropertyName("subscriptions")]
        public List<PersistedSubscription>? Subscriptions { get; set; }
    }
    private sealed class PersistedSubscription
    {
        [JsonPropertyName("label")]     public string Label { get; set; } = "";
        [JsonPropertyName("id")]        public string Id { get; set; } = "";
        [JsonPropertyName("resource")]  public string Resource { get; set; } = "";
        [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    }
}
