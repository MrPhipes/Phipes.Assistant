using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Phipes.Assistant.WebhookHandler.Utilities;

// Logica pura del receptor de InfraMonitor (contrato webhook v1): firma y formato para Teams.
public static class InfraMonitorWebhook
{
    // firma = "v1=" + hex(HMAC-SHA256(secreto, timestamp + "." + cuerpo)), sobre los bytes exactos
    // recibidos. Rechaza timestamps fuera de la ventana (evita repetir un mensaje capturado).
    public static bool VerifySignature(
        string secret, string? timestamp, byte[] body, string? signatureHeader, DateTimeOffset now, int maxSkewSeconds = 300)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader)) return false;
        if (!long.TryParse(timestamp, out var ts) || Math.Abs(now.ToUnixTimeSeconds() - ts) > maxSkewSeconds)
            return false;

        var message = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signatureHeader));
    }

    // Mensaje HTML para Teams: title en negrita, text respetando saltos de linea y url como enlace.
    // Campos desconocidos se ignoran (el contrato puede crecer sin cambiar de version).
    public static string FormatForTeams(JsonElement payload)
    {
        var title = GetString(payload, "title") ?? GetString(payload, "event") ?? "InfraMonitor";
        var text = GetString(payload, "text");
        var url = GetString(payload, "url");

        var sb = new StringBuilder("<p><b>").Append(HtmlText(title)).Append("</b></p>");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').Select(HtmlText);
            sb.Append("<p>").Append(string.Join("<br>", lines)).Append("</p>");
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            var href = HtmlText(uri.ToString());
            sb.Append("<p><a href=\"").Append(href).Append("\">Ver en InfraMonitor</a></p>");
        }
        return sb.ToString();
    }

    // Escape minimo para el HTML de Teams: solo los caracteres con significado en HTML. Deja tildes
    // y emojis tal cual (WebUtility.HtmlEncode los vuelve entidades numericas).
    public static string HtmlText(string? text) =>
        string.IsNullOrEmpty(text) ? "" :
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
