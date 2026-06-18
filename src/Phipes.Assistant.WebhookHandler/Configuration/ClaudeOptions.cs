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
    // Subido a 120s en 2026-06-02 (era 60s) para dar margen con sesiones gordas,
    // mientras la rotacion automatica del .jsonl (BloatThresholdMb) hace su trabajo.
    public int TimeoutSeconds { get; init; } = 120;

    // Cuando el .jsonl de la sesion supera este tamaño, el invoker lo rota
    // automaticamente antes de la proxima invocacion: archiva el actual a
    // _archived-sessions/<sid>-<timestamp>.jsonl y borra el .touch para que la
    // proxima invocacion arranque como NEW (sesion limpia y rapida). Cero MB
    // o negativo desactiva la rotacion automatica.
    public double BloatThresholdMb { get; init; } = 1.5;

    // Texto que se anexa al system prompt al invocar - reglas especificas del modo Teams.
    public string AppendSystemPrompt { get; init; } = "";

    // Path al perfil Claude (HOME / USERPROFILE) que se inyecta como env var al spawn
    // de claude.exe. Ahi viven CLAUDE.md, memory/, skills/, sessions/, .credentials.json.
    // Tipicamente C:\Users\<asistente-account>\ClaudeProfile o similar.
    [Required] public string ClaudeHome { get; init; } = "";

    // Session-id global que Sarah usa para TODAS sus conversaciones. Una sola sesion
    // continua le da vision cross-interlocutor: cuando responde a Yaritza, sabe que
    // antes hablo con Katerin sobre la misma coordinacion. Si en algun futuro se
    // operara multi-tenant, cambia el sufijo por tenant (ej "sarah-dimaco-master").
    public string MasterSessionId { get; init; } = "sarah-server-master";

    // URL del Ops Broker privilegiado (svc-sarah-ops). Solo se inyecta a claude.exe en
    // turnos del Owner (env var SARAH_OPS_BROKER_URL) para que las skills owner-gated
    // sepan a donde pegar. Vacio = capacidad ops deshabilitada.
    public string OpsBrokerUrl { get; init; } = "http://127.0.0.1:5060";

    // Clave HMAC compartida con el Ops Broker. El handler firma con ella el token efimero
    // que prueba "turno Owner autenticado". DEBE coincidir con OpsBroker:SharedHmacKey.
    // Vive en User Secrets, nunca en el repo.
    public string OpsBrokerHmacKey { get; init; } = "";
}
