using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

public sealed class ClaudeTranscriptTests
{
    private static readonly DateTimeOffset TurnStart = DateTimeOffset.Parse("2026-10-28T00:50:36Z");

    [Fact]
    public void Devuelve_el_ultimo_texto_del_asistente_del_turno()
    {
        var lines = new[]
        {
            """{"type":"assistant","timestamp":"2026-10-27T20:00:00Z","message":{"content":[{"type":"text","text":"respuesta vieja"}]}}""",
            """{"type":"user","timestamp":"2026-10-28T00:50:37Z","message":{"content":"hola"}}""",
            """{"type":"assistant","timestamp":"2026-10-28T00:50:39Z","message":{"content":[{"type":"tool_use","name":"Bash"}]}}""",
            """{"type":"assistant","timestamp":"2026-10-28T00:50:41Z","message":{"content":[{"type":"text","text":"Sí, todo bien. "},{"type":"text","text":"¿En qué le ayudo?"}]}}""",
            """{"type":"last-prompt"}""",
        };

        Assert.Equal("Sí, todo bien. ¿En qué le ayudo?", ClaudeTranscript.LastAssistantText(lines, TurnStart));
    }

    [Fact]
    public void Ignora_mensajes_anteriores_al_turno()
    {
        var lines = new[]
        {
            """{"type":"assistant","timestamp":"2026-10-27T20:00:00Z","message":{"content":[{"type":"text","text":"respuesta vieja"}]}}""",
        };

        Assert.Equal(string.Empty, ClaudeTranscript.LastAssistantText(lines, TurnStart));
    }

    [Fact]
    public void Acepta_contenido_como_string_e_ignora_lineas_invalidas()
    {
        var lines = new[]
        {
            "no es json",
            "",
            """{"type":"assistant","timestamp":"2026-10-28T00:51:00Z","message":{"content":"texto plano"}}""",
        };

        Assert.Equal("texto plano", ClaudeTranscript.LastAssistantText(lines, TurnStart));
    }
}
