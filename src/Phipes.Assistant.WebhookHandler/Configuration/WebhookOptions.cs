using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuración del webhook handler.
public sealed class WebhookOptions
{
    public const string SectionName = "Webhook";

    // Secreto compartido con Microsoft Graph: lo enviamos al crear la subscription y
    // Graph lo retorna en cada notification. Sirve para verificar que el POST es legítimo.
    [Required] public string ClientState { get; init; } = "";
}
