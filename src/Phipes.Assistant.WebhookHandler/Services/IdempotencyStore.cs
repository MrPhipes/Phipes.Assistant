using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;

namespace Phipes.Assistant.WebhookHandler.Services;

// Deduplica notifications de Graph: dado un (subscriptionId, resourceId), retorna true
// solo la PRIMERA vez. Las llamadas siguientes con el mismo par retornan false.
// La tabla esta en MSSQL local accedida via Windows auth como la cuenta del app pool.
public interface IIdempotencyStore
{
    Task<bool> TryMarkProcessedAsync(string subscriptionId, string resourceId, string channel, CancellationToken cancellationToken = default);

    // Deshace una marca cuando el procesamiento fallo, para que el reintento del emisor se procese.
    Task ForgetAsync(string subscriptionId, string resourceId, CancellationToken cancellationToken = default);
}

public sealed class SqlIdempotencyStore : IIdempotencyStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqlIdempotencyStore> _logger;

    public SqlIdempotencyStore(IOptions<IdempotencyOptions> options, ILogger<SqlIdempotencyStore> logger)
    {
        _connectionString = options.Value.ConnectionString;
        _logger = logger;
    }

    public async Task<bool> TryMarkProcessedAsync(string subscriptionId, string resourceId, string channel, CancellationToken cancellationToken = default)
    {
        // Truncar a 64/512 segun el schema de la tabla.
        var subId = Truncate(subscriptionId, 64);
        var resId = Truncate(resourceId, 512);
        var ch    = Truncate(channel, 16);

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO WebhookProcessedMessages (SubscriptionId, ResourceId, Channel) VALUES (@s, @r, @c)";
            cmd.Parameters.AddWithValue("@s", subId);
            cmd.Parameters.AddWithValue("@r", resId);
            cmd.Parameters.AddWithValue("@c", ch);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            return rows > 0;
        }
        catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
        {
            // 2627: violation of PRIMARY KEY  /  2601: violation of UNIQUE INDEX
            // Significa: este resource ya fue procesado antes. Comportamiento idempotente.
            return false;
        }
        catch (Exception ex)
        {
            // Cualquier otro error: loguear y FALLAR-OPEN (devolver true para procesar).
            // Mejor procesar un duplicado de mas que perder un mensaje real por un blip
            // momentaneo del SQL.
            FileLog($"WARN: Idempotency check fallo, FAIL-OPEN: {ex.Message}");
            return true;
        }
    }

    public async Task ForgetAsync(string subscriptionId, string resourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WebhookProcessedMessages WHERE SubscriptionId = @s AND ResourceId = @r";
            cmd.Parameters.AddWithValue("@s", Truncate(subscriptionId, 64));
            cmd.Parameters.AddWithValue("@r", Truncate(resourceId, 512));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            FileLog($"WARN: no se pudo deshacer la marca {subscriptionId}/{resourceId}: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

        private static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Idempotency", message);
}
