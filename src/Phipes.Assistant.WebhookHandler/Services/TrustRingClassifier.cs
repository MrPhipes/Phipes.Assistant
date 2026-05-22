using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;

namespace Phipes.Assistant.WebhookHandler.Services;

public enum TrustRing
{
    // Felipe Hernandez — acceso total sin restricciones, unico que da ordenes administrativas.
    Owner = 1,
    // Equipo interno (dominios verificados del tenant PacificDev). Ven 3 y 4, no a Felipe.
    Internal = 2,
    // Tenants federados via Entra (Dimaco, Amaza). No comparten info cruzada sin consentimiento.
    Federated = 3,
    // Externos sin relacion establecida. Default-deny salvo confirmacion de Felipe.
    External = 4,
}

public interface ITrustRingClassifier
{
    // senderObjectId: ObjectId firmado por Microsoft, viene en `from.user.id` de la notification.
    //                 NO es falsificable dentro del flujo de Graph (lo emite Microsoft).
    // senderTenantId: TenantId firmado por Microsoft (cuando aplica). Para senders no-Microsoft
    //                 (gmail, outlook personal via SMTP externo) viene null/empty.
    // senderEmail:   UPN/email del sender, usado solo para validar dominio interno.
    TrustRing Classify(string? senderObjectId, string? senderTenantId, string? senderEmail);
}

public sealed class TrustRingClassifier : ITrustRingClassifier
{
    private readonly HashSet<string> _ownerObjectIds;
    private readonly HashSet<string> _internalDomains;
    private readonly HashSet<string> _federatedTenants;
    private readonly HashSet<string> _federatedDomains;
    private readonly string _ownerTenantId;
    private readonly ILogger<TrustRingClassifier> _logger;

    public TrustRingClassifier(IOptions<TrustRingOptions> options, ILogger<TrustRingClassifier> logger)
    {
        var opts = options.Value;
        _ownerTenantId    = opts.OwnerTenantId?.Trim().ToLowerInvariant() ?? "";
        _ownerObjectIds   = new HashSet<string>(opts.OwnerObjectIds.Select(NormalizeGuid), StringComparer.OrdinalIgnoreCase);
        _internalDomains  = new HashSet<string>(opts.InternalDomains.Select(d => d.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        _federatedTenants = new HashSet<string>(opts.FederatedTenantIds.Select(NormalizeGuid), StringComparer.OrdinalIgnoreCase);
        _federatedDomains = new HashSet<string>(opts.FederatedDomains.Select(d => d.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public TrustRing Classify(string? senderObjectId, string? senderTenantId, string? senderEmail)
    {
        var oid = NormalizeGuid(senderObjectId);
        var tid = NormalizeGuid(senderTenantId);
        var domain = ExtractDomain(senderEmail);

        // Anillo 1: ObjectId en lista whitelist (verificacion cryptografica fuerte).
        if (!string.IsNullOrEmpty(oid) && _ownerObjectIds.Contains(oid))
        {
            return TrustRing.Owner;
        }

        // Anillo 2: tenant == owner AND dominio verificado del tenant principal.
        // El AND es defensa: si Microsoft cambiara el comportamiento y un guest user
        // del tenant viniera con OwnerTenantId pero con un dominio @somethingelse.com,
        // no le concedemos privilegios Internal.
        if (!string.IsNullOrEmpty(tid) && tid == _ownerTenantId
            && !string.IsNullOrEmpty(domain) && _internalDomains.Contains(domain))
        {
            return TrustRing.Internal;
        }

        // Anillo 3: tenant federado configurado en Entra cross-tenant access.
        if (!string.IsNullOrEmpty(tid) && _federatedTenants.Contains(tid))
        {
            return TrustRing.Federated;
        }

        // Anillo 3 fallback por dominio: cuando el sender llega SIN tenantId firmado
        // (caso tipico de mail SMTP), nos quedamos solo con el header From: el dominio
        // como senal. Senal mas debil que tenantId firmado, pero suficiente para mail
        // que ya paso SPF/DKIM/DMARC en Exchange Online. Sin este check, todos los
        // mails de partners federados caerian erroneamente a External.
        if (!string.IsNullOrEmpty(domain) && _federatedDomains.Contains(domain))
        {
            return TrustRing.Federated;
        }

        // Default: anillo 4 (incluye gmail / outlook personal sin tenantId,
        // tenants Microsoft no federados, y senders sin identidad clara).
        return TrustRing.External;
    }

    private static string NormalizeGuid(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        // Acepta el GUID con o sin braces, lo normaliza al formato lowercase canonical
        if (Guid.TryParse(t, out var g)) return g.ToString("D").ToLowerInvariant();
        return t.ToLowerInvariant();
    }

    private static string ExtractDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "";
        var at = email.LastIndexOf('@');
        return at >= 0 && at < email.Length - 1
            ? email[(at + 1)..].Trim().ToLowerInvariant()
            : "";
    }
}
