using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuracion para invocar Claude Code en modo --print contra el plan Max de Felipe.
public sealed class ClaudeOptions
{
    public const string SectionName = "Claude";

    // Ruta absoluta al binario claude.exe instalado en el host (ej C:\Tools\Claude\claude.exe).
    [Required] public string ExePath { get; init; } = "";

    // Ruta al PSCredential XML (DPAPI) que contiene el OAuth long-lived token.
    [Required] public string OAuthTokenPath { get; init; } = "";

    // Limite por turno en USD - frena conversaciones largas.
    public double MaxBudgetUsd { get; init; } = 0.50;

    // Timeout total para una invocacion (cancela el proceso si claude no responde).
    public int TimeoutSeconds { get; init; } = 60;

    // Texto que se anexa al system prompt al invocar - reglas especificas del modo Teams.
    public string AppendSystemPrompt { get; init; } = "";

    // Path al perfil Claude (HOME / USERPROFILE) que se inyecta como env var al spawn
    // de claude.exe. Ahi viven CLAUDE.md, memory/, skills/, sessions/, .credentials.json.
    // Tipicamente C:\Users\<asistente-account>\ClaudeProfile o similar.
    [Required] public string ClaudeHome { get; init; } = "";
}
