namespace Phipes.Assistant.OpsBroker.Utilities;

// Log de archivo simple para auditoría de toda acción privilegiada del broker.
// Path configurable via env var OPSBROKER_LOG_DIR; default junto al binario.
public static class FileLogger
{
    private static readonly object Gate = new();

    public static void Write(string category, string message)
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("OPSBROKER_LOG_DIR")
                      ?? Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var line = $"{DateTime.UtcNow:o} [{category}] {message}{Environment.NewLine}";
            var file = Path.Combine(dir, $"opsbroker-{DateTime.UtcNow:yyyyMMdd}.log");
            lock (Gate) { File.AppendAllText(file, line); }
        }
        catch { /* el logging nunca debe tumbar la operación */ }
    }
}
