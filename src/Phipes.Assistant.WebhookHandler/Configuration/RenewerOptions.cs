using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Config para el BackgroundService que mantiene vivas las Graph subscriptions.
public sealed class RenewerOptions
{
    public const string SectionName = "Renewer";

    // Cada cuantos minutos despierta el BackgroundService.
    public int IntervalMinutes { get; init; } = 30;

    // Margen: si la subscription expira en menos de esto, dispara renovacion.
    public int RenewWhenLessThanMinutes { get; init; } = 30;

    // Extension a aplicar al renovar. Microsoft topea: /me/chats/getAllMessages = 60 min,
    // /me/messages = 4230 min (70.5h).
    public int ChatExtendMinutes { get; init; } = 55;
    public int MailExtendMinutes { get; init; } = 4230;

    // Ids de subscriptions que debe vigilar.
    [Required] public string[] SubscriptionIds { get; init; } = Array.Empty<string>();
}

// Config de la "Webhook app" en Entra: la app que tiene Chat.ReadWrite.All
// admin-consent y bajo cuyo contexto se crean las subscriptions con resource data encryption.
public sealed class WebhookAppOptions
{
    public const string SectionName = "WebhookApp";

    [Required] public string TenantId { get; init; } = "";
    [Required] public string ClientId { get; init; } = "";
    [Required] public string RefreshTokenPath { get; init; } = "";

    // UPN de la identidad asistente (compartido con GraphOptions). Usado al persistir
    // el refresh token rotado.
    [Required] public string UserPrincipalName { get; init; } = "";

    public string Scopes { get; init; } =
        "openid offline_access User.Read Chat.ReadWrite Chat.ReadWrite.All";
}
