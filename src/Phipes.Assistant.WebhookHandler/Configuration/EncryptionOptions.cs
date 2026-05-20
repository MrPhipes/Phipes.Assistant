using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Datos para descifrar las notifications de Graph (resource data encryption).
// El PFX vive en el perfil de la cuenta del app pool (cifrado con la password de abajo).
public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    // Ruta absoluta al archivo PFX que contiene el par RSA (private + public).
    [Required] public string PfxPath { get; init; } = "";

    // Password del PFX. Es secreto, viene de User Secrets.
    [Required] public string PfxPassword { get; init; } = "";

    // Identificador estable que pasamos a Microsoft al crear la subscription;
    // Microsoft lo retorna en cada notification para que sepamos cuál par de claves usar.
    [Required] public string CertificateId { get; init; } = "";
}
