using System.Text.Json.Serialization;

namespace Phipes.Assistant.WebhookHandler.Models;

// Modelo del payload que Microsoft Graph manda en cada webhook.
// Documentación: https://learn.microsoft.com/graph/webhooks
public sealed class ChangeNotificationCollection
{
    [JsonPropertyName("value")]
    public List<ChangeNotification> Value { get; set; } = new();

    // Cuando usamos resource-data encryption, Graph también incluye validationTokens
    // (uno por cada subscription distinta). Cada uno es un JWT firmado por Microsoft que
    // debemos validar para confirmar que el POST realmente viene de Graph.
    [JsonPropertyName("validationTokens")]
    public List<string>? ValidationTokens { get; set; }
}

public sealed class ChangeNotification
{
    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("clientState")]
    public string? ClientState { get; set; }

    [JsonPropertyName("changeType")]
    public string? ChangeType { get; set; }

    [JsonPropertyName("resource")]
    public string? Resource { get; set; }

    [JsonPropertyName("subscriptionExpirationDateTime")]
    public DateTimeOffset? SubscriptionExpirationDateTime { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    // Presente cuando la subscription pidió includeResourceData=true.
    // Contiene el resource cifrado (mensaje completo de Teams, etc).
    [JsonPropertyName("encryptedContent")]
    public EncryptedContent? EncryptedContent { get; set; }

    // Presente SOLO en notifications de lifecycle (vienen al lifecycleNotificationUrl).
    // Valores: "reauthorizationRequired", "subscriptionRemoved", "missed".
    [JsonPropertyName("lifecycleEvent")]
    public string? LifecycleEvent { get; set; }
}

public sealed class EncryptedContent
{
    // AES-CBC ciphertext base64.
    [JsonPropertyName("data")]
    public string Data { get; set; } = "";

    // RSA-OAEP-SHA1 ciphertext de la AES key, base64.
    [JsonPropertyName("dataKey")]
    public string DataKey { get; set; } = "";

    // HMAC-SHA256 de "data" usando la AES key, base64.
    [JsonPropertyName("dataSignature")]
    public string DataSignature { get; set; } = "";

    // Identificador que mandamos al crear la subscription.
    [JsonPropertyName("encryptionCertificateId")]
    public string? EncryptionCertificateId { get; set; }

    // Thumbprint SHA1 del cert que Microsoft usó para cifrar (debe coincidir con nuestro cert).
    [JsonPropertyName("encryptionCertificateThumbprint")]
    public string? EncryptionCertificateThumbprint { get; set; }
}
