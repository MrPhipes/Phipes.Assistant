using System.Text;
using System.Text.Json;

namespace Phipes.Assistant.WebhookHandler.Services;

// Lee el .jsonl de una sesion de claude.exe. Sirve para rescatar la respuesta de un turno
// que claude.exe ya genero pero cerro con error (p. ej. presupuesto excedido): en ese caso
// el JSON de --output-format no trae "result", pero el mensaje del asistente si quedo
// escrito en la sesion.
public static class ClaudeTranscript
{
    // Texto del ultimo mensaje del asistente con texto, escrito desde "since". Vacio si no hay.
    // Lineas invalidas o de otros tipos se ignoran.
    public static string LastAssistantText(IEnumerable<string> jsonlLines, DateTimeOffset since)
    {
        var last = string.Empty;
        foreach (var line in jsonlLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant") continue;
                if (!root.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), out var at) || at < since) continue;
                if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) continue;

                var text = new StringBuilder();
                if (content.ValueKind == JsonValueKind.String)
                {
                    text.Append(content.GetString());
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" && block.TryGetProperty("text", out var t))
                            text.Append(t.GetString());
                    }
                }
                if (text.Length > 0) last = text.ToString();
            }
            catch (JsonException) { }
        }
        return last;
    }
}
