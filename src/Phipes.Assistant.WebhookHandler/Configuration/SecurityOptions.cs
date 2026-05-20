namespace Phipes.Assistant.WebhookHandler.Configuration;

// Politicas de seguridad del webhook handler.
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    // Si true, una notification con validationTokens invalidos se rechaza con 401.
    // Si false (default), solo se loguea warning - util para shadow-testing antes de
    // activar rejection. Cuando este validado que la logica funciona en produccion sin
    // falsos negativos, cambiar a true desde User Secrets.
    public bool RejectInvalidJwts { get; init; } = false;
}
