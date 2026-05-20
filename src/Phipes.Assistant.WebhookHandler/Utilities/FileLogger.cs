namespace Phipes.Assistant.WebhookHandler.Utilities;

// Logger de archivo centralizado. El directorio se lee de la env var HANDLER_LOG_DIR
// (configurable en web.config / docker / variable del proceso). Si no esta setada,
// usa un path local genérico bajo TempPath, asi el codigo no filtra el nombre del
// site IIS al codebase publico.
//
// Cada categoria de log usa el mismo archivo diario (handler-yyyymmdd.log) con prefijo
// [Categoria] en cada linea.
internal static class FileLogger
{
    private static readonly string LogDir =
        Environment.GetEnvironmentVariable("HANDLER_LOG_DIR")
        ?? Path.Combine(Path.GetTempPath(), "webhook-handler", "logs");

    // Escribe una linea de log. Nunca tira excepcion (try/catch silencioso).
    public static void Write(string category, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var file = Path.Combine(LogDir, $"handler-{DateTime.UtcNow:yyyyMMdd}.log");
            File.AppendAllText(file, $"{DateTime.UtcNow:o} [{category}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
