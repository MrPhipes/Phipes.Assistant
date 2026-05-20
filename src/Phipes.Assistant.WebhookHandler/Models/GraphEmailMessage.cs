using System.Text.Json.Serialization;

namespace Phipes.Assistant.WebhookHandler.Models;

// Modelo del subset de campos del email que necesitamos. Los emails llegan en el
// resourceData de la notification si la subscription se creo con $select=subject,from,...
public sealed class GraphEmailMessage
{
    [JsonPropertyName("id")]                public string? Id { get; set; }
    [JsonPropertyName("subject")]           public string? Subject { get; set; }
    [JsonPropertyName("bodyPreview")]       public string? BodyPreview { get; set; }
    [JsonPropertyName("body")]              public EmailBody? Body { get; set; }
    [JsonPropertyName("from")]              public EmailRecipient? From { get; set; }
    [JsonPropertyName("sender")]            public EmailRecipient? Sender { get; set; }
    [JsonPropertyName("toRecipients")]      public List<EmailRecipient>? ToRecipients { get; set; }
    [JsonPropertyName("ccRecipients")]      public List<EmailRecipient>? CcRecipients { get; set; }
    [JsonPropertyName("receivedDateTime")]  public DateTimeOffset? ReceivedDateTime { get; set; }
    [JsonPropertyName("isRead")]            public bool? IsRead { get; set; }
    [JsonPropertyName("conversationId")]    public string? ConversationId { get; set; }
}

public sealed class EmailBody
{
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    [JsonPropertyName("content")]     public string? Content { get; set; }
}

public sealed class EmailRecipient
{
    [JsonPropertyName("emailAddress")] public EmailAddress? EmailAddress { get; set; }
}

public sealed class EmailAddress
{
    [JsonPropertyName("name")]    public string? Name { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }
}
