using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuración del webhook handler.
public sealed class WebhookOptions
{
    public const string SectionName = "Webhook";

    // Secreto compartido con Microsoft Graph: lo enviamos al crear la subscription y
    // Graph lo retorna en cada notification. Sirve para verificar que el POST es legítimo.
    [Required] public string ClientState { get; init; } = "";

    // URL publica del endpoint donde Microsoft envia las notifications. Se usa al recrear
    // una subscription tras un evento subscriptionRemoved (auto-recovery del LifecycleHandler).
    [Required] public string NotificationUrl { get; init; } = "";

    // URL publica del endpoint donde Microsoft envia eventos del ciclo de vida
    // (reauthorizationRequired, subscriptionRemoved, missed). Mismo uso que NotificationUrl.
    [Required] public string LifecycleNotificationUrl { get; init; } = "";
}
