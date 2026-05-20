using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Config para la tabla de idempotencia (deduplica notifications repetidas de Graph).
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    // Connection string a MSSQL local del host que corre el handler (Windows auth).
    [Required] public string ConnectionString { get; init; } = "";
}
