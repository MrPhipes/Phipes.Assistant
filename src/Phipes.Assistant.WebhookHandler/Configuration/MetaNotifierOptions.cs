namespace Phipes.Assistant.WebhookHandler.Configuration;

// Aviso proactivo de comentarios y mensajes directos de Facebook/Instagram (PacificDev.Social).
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

    // ---- Mensajes directos -------------------------------------------------------------

    // Almacén DPAPI (LocalMachine) de PacificDev.Social; el notifier solo lee el token de
    // Instagram para mostrar el @usuario del remitente.
    public string SecretsFilePath { get; set; } = @"C:\ProgramData\PacificDev.Meta\meta-secrets.json";

    // Ventana de Meta para responder: 24 h desde el último mensaje de la persona.
    // Con la función Human Agent aprobada por Meta sube a 7 días (168 h).
    public int ResponseWindowHours { get; set; } = 24;

    // Cuántas horas antes del vencimiento se manda el recordatorio a Felipe.
    public int ReminderLeadHours { get; set; } = 4;

    // Redactar una respuesta propuesta con Claude aislado (sin herramientas).
    public bool DraftReplies { get; set; } = true;
    public double DraftMaxBudgetUsd { get; set; } = 0.15;
    public int DraftTimeoutSeconds { get; set; } = 60;
}
