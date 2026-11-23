using System.Text.Json;

namespace Phipes.Assistant.WebhookHandler.Services;

// Mensaje directo de un tercero extraído de un webhook de Meta.
// Platform: "instagram" (Instagram Login, object=instagram) o "facebook" (Messenger, object=page).
// SenderId: IGSID / PSID — el id con el que se responde por la Send API.
public sealed record MetaDirectMessage(
    string Platform, string MessageId, string SenderId, string Text, DateTime SentUtc);

// Lee los eventos de mensajería (entry[].messaging[]) de un payload de webhook de Meta.
// Ignora: ecos de nuestros propios envíos (is_echo), mensajes cuyo remitente es la cuenta
// propia, mensajes borrados (is_deleted), lecturas, reacciones y eventos sin "message".
public static class MetaDmParser
{
    public static List<MetaDirectMessage> Extract(string payload, string? ownInstagramId, string? ownPageId)
    {
        var result = new List<MetaDirectMessage>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException) { return result; }

        using (doc)
        {
            var root = doc.RootElement;
            var objectType = root.TryGetProperty("object", out var o) ? o.GetString() : null;
            var platform = objectType switch
            {
                "instagram" => "instagram",
                "page" => "facebook",
                _ => null
            };
            if (platform is null) return result;
            var ownId = platform == "instagram" ? ownInstagramId : ownPageId;

            if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("messaging", out var messaging) || messaging.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var ev in messaging.EnumerateArray())
                {
                    if (!ev.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                        continue; // read, reaction, postback, etc.

                    if (message.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True)
                        continue;
                    if (message.TryGetProperty("is_deleted", out var del) && del.ValueKind == JsonValueKind.True)
                        continue;

                    var senderId = ev.TryGetProperty("sender", out var s) && s.TryGetProperty("id", out var sid)
                        ? sid.GetString() : null;
                    if (string.IsNullOrEmpty(senderId)) continue;
                    if (!string.IsNullOrEmpty(ownId) && string.Equals(senderId, ownId, StringComparison.Ordinal))
                        continue;

                    var mid = message.TryGetProperty("mid", out var m) ? m.GetString() : null;
                    if (string.IsNullOrEmpty(mid)) continue;

                    var text = message.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text) && message.TryGetProperty("attachments", out var att)
                        && att.ValueKind == JsonValueKind.Array)
                    {
                        var kinds = att.EnumerateArray()
                            .Select(a => a.TryGetProperty("type", out var ty) ? ty.GetString() : null)
                            .Where(k => !string.IsNullOrEmpty(k));
                        text = $"(adjunto: {string.Join(", ", kinds)})";
                    }

                    var sentUtc = DateTime.UtcNow;
                    if (ev.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var ms) && ms > 0)
                        sentUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

                    result.Add(new MetaDirectMessage(platform, mid!, senderId!, text, sentUtc));
                }
            }
        }
        return result;
    }
}
