namespace Phipes.Assistant.WebhookHandler.Configuration;

// Receptor POST /api/alerts/inframonitor: recibe los eventos del contrato webhook v1 de
// PacificDev.InfraMonitor (docs/notificaciones-webhook.md en ese repo) y los publica en el
// chat de alertas (Monitoring:AlertTarget, "Sarah - Alerts") sin pasar por Claude.
//
// Cerrado por defecto, igual que /api/m365-token:
//   1. Secret vacio => rechaza todo. El secreto lo genera el portal al crear el canal y vive en
//      el vault como InfraMonitor-webhook-sarah-server; aca va en User Secrets.
//   2. Allowlist de IP de origen. InfraMonitor corre en el mismo srv-pacificdev.
//   3. Ademas se rechaza cualquier request que llegue por el tunel Cloudflare (trae
//      CF-Connecting-IP): el conector tambien corre local y su origen seria 127.0.0.1.
public sealed class InfraMonitorAlertsOptions
{
    public const string SectionName = "InfraMonitorAlerts";

    public string Secret { get; init; } = "";

    public string[] AllowedSourcePrefixes { get; init; } = ["127.0.0.1", "::1", "13.13.69.45", "::ffff:127.0.0.1"];

    // Diferencia maxima entre X-InfraMonitor-Timestamp y la hora local (contrato: 300 s).
    public int MaxClockSkewSeconds { get; init; } = 300;
}
