using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuracion del sistema de monitoring activo. Sarah-server reporta condiciones
// malas a Felipe via Teams chat 1:1, con prefijo distintivo. Cada regla es desactivable
// individualmente desde User Secrets.
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    // Master switch. Si false, NO se dispara ninguna alerta (util para deshabilitar
    // temporalmente sin desconfigurar reglas individuales).
    public bool Enabled { get; init; } = true;

    // Destinatario de las alertas. Puede ser:
    //   - chatId Teams (empieza con "19:")
    //   - UPN/email (ej "felipe@midominio.cl") - se resuelve a chatId 1:1 internamente.
    [Required] public string AlertTarget { get; init; } = "";

    // Prefijo que se antepone a cada mensaje de alerta, para distinguir alertas
    // del flujo normal de respuestas.
    public string AlertPrefix { get; init; } = "[SARAH OPS]";

    // Anti-spam: si la misma categoria de alerta ya se dispar en este tiempo,
    // suprimir alertas adicionales.
    public int SuppressionMinutes { get; init; } = 30;

    // Cadencia del BackgroundService para evaluar contadores.
    public int CheckIntervalSeconds { get; init; } = 60;

    // === Reglas individuales (cada una desactivable) ===

    public AlertRule ClaudeTimeouts { get; init; } = new()
    {
        Enabled = true, Threshold = 3, WindowMinutes = 5,
        Label = "Claude timeouts"
    };

    public AlertRule DecryptFailures { get; init; } = new()
    {
        Enabled = true, Threshold = 5, WindowMinutes = 60,
        Label = "Decrypt failures"
    };

    public AlertRule JwtInvalid { get; init; } = new()
    {
        Enabled = true, Threshold = 1, WindowMinutes = 1440,
        Label = "JWT validation invalid (posible intento de intrusion)"
    };

    public AlertRule SubscriptionRenewalFailures { get; init; } = new()
    {
        Enabled = true, Threshold = 2, WindowMinutes = 60,
        Label = "Subscription renewal failures"
    };

    public AlertRule ClaudeApiErrors { get; init; } = new()
    {
        Enabled = true, Threshold = 3, WindowMinutes = 10,
        Label = "Claude API errors"
    };
}

public sealed class AlertRule
{
    public bool Enabled { get; init; } = true;
    // Cantidad de ocurrencias en la ventana para disparar alerta.
    public int Threshold { get; init; } = 3;
    // Ventana rolling (minutos) sobre la que se cuentan las ocurrencias.
    public int WindowMinutes { get; init; } = 5;
    // Etiqueta legible que aparece en el mensaje de alerta.
    public string Label { get; init; } = "";
}
