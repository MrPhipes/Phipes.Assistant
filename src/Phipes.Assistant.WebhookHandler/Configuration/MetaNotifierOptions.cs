namespace Phipes.Assistant.WebhookHandler.Configuration;

// Aviso proactivo de comentarios de Facebook/Instagram (plataforma MetaSocial).
// El sitio meta.pacificdev.cl persiste los webhooks de Meta en
// PacificDevMeta.dbo.WebhookEvents; este notifier los vigila y avisa a Felipe
// por Teams para que instruya la moderación (responder / #plantilla / ocultar).
public sealed class MetaNotifierOptions
{
    public const string SectionName = "MetaNotifier";

    public bool Enabled { get; set; } = false;

    public int IntervalSeconds { get; set; } = 45;

    // Connection string a la BD PacificDevMeta (misma instancia local).
    public string MetaConnectionString { get; set; } = "";

    // UPN de Felipe para resolver el chat 1:1 (mismo valor que WaNotifier:FelipeUpn).
    public string FelipeUpn { get; set; } = "";

    // Ids propios para reconocer (y no avisar) nuestros propios comentarios/respuestas.
    public string FacebookPageId { get; set; } = "";
    public string InstagramUserId { get; set; } = "";
}
