using System.Text;

namespace Phipes.Assistant.WebhookHandler.Utilities;

// Logger de archivo centralizado. El directorio se lee de la env var HANDLER_LOG_DIR
// (configurable en web.config / docker / variable del proceso). Si no esta setada,
// usa un path local genérico bajo TempPath, asi el codigo no filtra el nombre del
// site IIS al codebase publico.
//
// Cada categoria de log usa el mismo archivo diario (handler-yyyymmdd.log) con prefijo
// [Categoria] en cada linea.
//
// Encoding: UTF-8 con BOM al inicio del archivo. El BOM ayuda a readers como
// PowerShell 5.1 (Get-Content default lee como Windows-1252 sin BOM) a reconocer
// el encoding correctamente. Los appends siguientes son UTF-8 sin BOM (el BOM
// al inicio del archivo basta para que el reader detecte el encoding).
internal static class FileLogger
{
    private static readonly string LogDir =
        Environment.GetEnvironmentVariable("HANDLER_LOG_DIR")
        ?? Path.Combine(Path.GetTempPath(), "webhook-handler", "logs");

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly object _writeLock = new();

    // Escribe una linea de log. Nunca tira excepcion (try/catch silencioso).
    public static void Write(string category, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var file = Path.Combine(LogDir, $"handler-{DateTime.UtcNow:yyyyMMdd}.log");
            var line = $"{DateTime.UtcNow:o} [{category}] {message}{Environment.NewLine}";

            // Lock para evitar race entre threads que ambos crean el archivo y duplican BOM.
            lock (_writeLock)
            {
                var fileExists = File.Exists(file) && new FileInfo(file).Length > 0;
                if (!fileExists)
                {
                    File.WriteAllText(file, line, Utf8WithBom);
                }
                else
                {
                    File.AppendAllText(file, line, Utf8NoBom);
                }
            }
        }
        catch { }
    }
}
