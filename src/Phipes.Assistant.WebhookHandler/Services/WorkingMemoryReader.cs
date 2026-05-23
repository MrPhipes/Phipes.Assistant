using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;

namespace Phipes.Assistant.WebhookHandler.Services;

// Consulta la WorkingMemory + CoordinationTasks abiertas para un interlocutor, y
// devuelve una seccion de prompt formateada que el handler inyecta antes de invocar
// claude.exe. Sin esto, Sarah-server depende de que ella misma se acuerde de llamar
// el skill /recordar — no confiable. Con esto, el contexto SIEMPRE llega al prompt.
//
// Fail-soft: si MSSQL falla, devuelve string vacio. Mejor responder sin contexto
// que bloquear la respuesta del todo.
public interface IWorkingMemoryReader
{
    Task<string> BuildContextSectionAsync(
        string subjectKey,
        string? subjectUpn,
        CancellationToken cancellationToken = default);
}

public sealed class SqlWorkingMemoryReader : IWorkingMemoryReader
{
    private readonly string _connectionString;
    private readonly ILogger<SqlWorkingMemoryReader> _logger;

    public SqlWorkingMemoryReader(IOptions<IdempotencyOptions> options, ILogger<SqlWorkingMemoryReader> logger)
    {
        _connectionString = options.Value.ConnectionString;
        _logger = logger;
    }

    public async Task<string> BuildContextSectionAsync(
        string subjectKey,
        string? subjectUpn,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subjectKey)) return "";

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            var facts = await ReadFactsAsync(conn, subjectKey, cancellationToken);
            var tasks = await ReadOpenTasksAsync(conn, subjectUpn, cancellationToken);

            if (facts.Count == 0 && tasks.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== CONTEXTO PREVIO (WorkingMemory + Tasks) ===");

            if (facts.Count > 0)
            {
                sb.AppendLine($"Hechos vigentes sobre '{subjectKey}' (los mas recientes primero):");
                foreach (var f in facts)
                {
                    sb.AppendLine($"  - [{f.FactType}] {f.FactContent} (vis={f.Visibility}, src={f.Source ?? "?"}, {f.CreatedAt:yyyy-MM-dd HH:mm}Z)");
                }
                sb.AppendLine("Recuerde: NO mencionar hechos privados (vis=private) de OTRAS personas al interlocutor actual.");
            }

            if (tasks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"CoordinationTasks abiertos donde este interlocutor es participante:");
                foreach (var t in tasks)
                {
                    sb.AppendLine($"  - Task #{t.Id} [{t.TaskType}/{t.Status}] target='{t.TargetOutcome}'");
                    sb.AppendLine($"      participants={t.Participants}");
                    sb.AppendLine($"      collected={t.CollectedData}");
                }
                // Instruccion imperativa fuerte. Previene dos bugs observados en runtime:
                // (a) Sarah haciendo VOID a turnos que aportan al task.
                // (b) Sarah respondiendo en el chat pero olvidando persistir en CollectedData.
                // La memoria operacional `feedback-no-void-en-chats-de-coordinacion` cubre
                // este caso pero el LLM con RESUME no siempre la re-lee. Esta seccion del
                // prompt se inyecta en CADA turno, por eso es la barrera dura.
                sb.AppendLine();
                sb.AppendLine("*** PROTOCOLO OBLIGATORIO para este turno (porque hay task abierto donde el interlocutor es participante): ***");
                sb.AppendLine("  1. NO use VOID / [SKIP] / silencio para este mensaje. Debe responder en el chat aunque sea una frase corta de acuse de recibo.");
                sb.AppendLine("  2. DESPUES de responder, ejecute /coordinar-actualizar con el taskId apropiado para persistir el aporte de este interlocutor en CollectedData. Sin este paso la nota se pierde.");
                sb.AppendLine("  3. La politica de anillo (Federated/Internal/External) aplica al CONTENIDO de la respuesta (no compartir info cruzada sin consentimiento), NO a si responder o no — aqui SIEMPRE se responde.");
            }

            sb.AppendLine();
            return sb.ToString();
        }
        catch (Exception ex)
        {
            FileLog($"WARN: WorkingMemoryReader fallo para subject='{subjectKey}' upn='{subjectUpn}': {ex.Message}");
            return "";
        }
    }

    private static async Task<List<FactRow>> ReadFactsAsync(SqlConnection conn, string subjectKey, CancellationToken cancellationToken)
    {
        var rows = new List<FactRow>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 15 FactType, FactContent, Visibility, Source, CreatedAt
FROM dbo.WorkingMemory
WHERE SubjectEntity = @subj
  AND (ExpiresAt IS NULL OR ExpiresAt > SYSUTCDATETIME())
ORDER BY CreatedAt DESC";
        cmd.Parameters.AddWithValue("@subj", subjectKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FactRow(
                FactType:   reader.GetString(0),
                FactContent: reader.GetString(1),
                Visibility: reader.GetString(2),
                Source:     reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt:  reader.GetDateTime(4)));
        }
        return rows;
    }

    private static async Task<List<TaskRow>> ReadOpenTasksAsync(SqlConnection conn, string? subjectUpn, CancellationToken cancellationToken)
    {
        var rows = new List<TaskRow>();
        if (string.IsNullOrWhiteSpace(subjectUpn)) return rows;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT Id, TaskType, Status, Participants, CollectedData, TargetOutcome
FROM dbo.CoordinationTask
WHERE Status IN ('pending','partial')
  AND CHARINDEX('""' + @upn + '""', Participants) > 0
ORDER BY CreatedAt DESC";
        cmd.Parameters.AddWithValue("@upn", subjectUpn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TaskRow(
                Id:            reader.GetInt64(0),
                TaskType:      reader.GetString(1),
                Status:        reader.GetString(2),
                Participants:  reader.GetString(3),
                CollectedData: reader.IsDBNull(4) ? "" : reader.GetString(4),
                TargetOutcome: reader.IsDBNull(5) ? "" : reader.GetString(5)));
        }
        return rows;
    }

    private static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("WorkingMemory", message);

    private sealed record FactRow(string FactType, string FactContent, string Visibility, string? Source, DateTime CreatedAt);
    private sealed record TaskRow(long Id, string TaskType, string Status, string Participants, string CollectedData, string TargetOutcome);
}
