using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuracion del modelo de 4 anillos de confianza. Los valores sensibles
// (ObjectIds de Felipe, TenantIds federados) viven en User Secrets, NUNCA en
// appsettings.json versionado. Ver appsettings.json para los placeholders.
public sealed class TrustRingOptions
{
    public const string SectionName = "TrustRing";

    // TenantId del tenant principal donde corre Sarah (PacificDev). Senders con
    // este tenantId + dominio en InternalDomains caen a Internal (anillo 2).
    [Required] public string OwnerTenantId { get; init; } = "";

    // ObjectIds (GUIDs) de las cuentas que califican como Owner (anillo 1).
    // Identificacion por ObjectId, no por UPN, para sobrevivir a renames de email.
    public List<string> OwnerObjectIds { get; init; } = new();

    // Dominios verificados del tenant principal (anillo 2). Sender con tenantId ==
    // OwnerTenantId pero dominio FUERA de esta lista cae a External por seguridad.
    public List<string> InternalDomains { get; init; } = new();

    // TenantIds (GUIDs) de tenants externos federados via Entra cross-tenant access
    // (anillo 3). Hoy: Dimaco + Amaza. Si Felipe agrega otro partner, se agrega aqui.
    public List<string> FederatedTenantIds { get; init; } = new();

    // Dominios de los tenants federados, usados como fallback cuando el sender llega
    // SIN tenantId (caso tipico de mail SMTP: solo tenemos el header From:). Es senal
    // mas debil que TenantId pero suficiente para mail que ya paso SPF/DKIM/DMARC en
    // Exchange Online. Si un dominio NO esta aqui y no hay tenantId, cae a External.
    public List<string> FederatedDomains { get; init; } = new();
}
