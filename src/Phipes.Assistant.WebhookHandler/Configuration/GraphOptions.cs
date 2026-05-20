using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuracion para llamar Microsoft Graph como la identidad asistente.
// Los valores vienen de User Secrets (no del repo).
public sealed class GraphOptions
{
    public const string SectionName = "Graph";

    [Required] public string TenantId { get; init; } = "";
    [Required] public string ClientId { get; init; } = "";

    // UPN de la identidad asistente (la cuenta M365 que ejecuta como Sarah). Se usa al
    // persistir el refresh token rotado y para filtrar correos donde la asistente es
    // destinataria directa. Viene de User Secrets - NUNCA hardcodear.
    [Required] public string UserPrincipalName { get; init; } = "";

    // Ruta al XML DPAPI con el refresh token, cifrado por la cuenta que corre el host.
    [Required] public string RefreshTokenPath { get; init; } = "";

    // Scopes que pedimos a cada refresh.
    public string Scopes { get; init; } =
        "openid offline_access User.Read Mail.ReadWrite Mail.Send Chat.ReadWrite ChannelMessage.Send Calendars.ReadWrite";
}
