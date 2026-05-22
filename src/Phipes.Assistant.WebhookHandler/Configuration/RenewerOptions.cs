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

    // Legacy: ids de subscriptions sueltos (sin resource/changeType para auto-recovery).
    // Si Subscriptions[] esta poblado, gana sobre este array (ver SubscriptionRenewer.
    // ResolveSubscriptionIds). Mantenido por compatibilidad con configs viejos pero
    // nuevos deployments deben usar solo Subscriptions[].
    public string[] SubscriptionIds { get; init; } = Array.Empty<string>();

    // Definiciones completas de cada sub gestionada. Necesarias para auto-recovery en
    // LifecycleHandler cuando Microsoft elimina una sub: sin saber el resource original,
    // no se puede recrear. Si esta vacio, auto-recovery queda deshabilitado.
    public SubscriptionDefinition[] Subscriptions { get; init; } = Array.Empty<SubscriptionDefinition>();
}

// Definicion completa de una Graph subscription gestionada por el handler. Necesaria
// para recrear automaticamente cuando Microsoft elimina la sub (lifecycle event
// "subscriptionRemoved"). El campo Id es mutable - despues de auto-recovery la app
// guarda en memoria el id de la sub recreada; persistencia a disco queda pendiente.
public sealed class SubscriptionDefinition
{
    // Id actual de la sub. Se actualiza in-memory tras auto-recovery; al restart se
    // re-lee del User Secrets, asi que conviene que Felipe actualice el secrets.json
    // con el id nuevo cuando AlertManager le notifique el cambio.
    [Required] public string Id { get; set; } = "";

    // Resource que se vigila (ej "/me/chats/getAllMessages" o
    // "/me/messages?$select=subject,from,toRecipients,bodyPreview,receivedDateTime,isRead").
    [Required] public string Resource { get; init; } = "";

    // Minutos hasta expirar al recrear. Topes Microsoft: chats=60, messages=4230.
    public int ExpirationMinutes { get; init; } = 55;

    // ChangeType que dispara la notification (default: created, separado por coma si son varios).
    public string ChangeType { get; init; } = "created";

    // Etiqueta legible para logs y alertas (ej "chat" o "mail").
    public string Label { get; init; } = "";
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
