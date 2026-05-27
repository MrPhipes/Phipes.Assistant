namespace Phipes.Assistant.WebhookHandler.Configuration;

// Config del BackgroundService WaProactiveNotifier (Fase 2: aviso proactivo de WhatsApp).
//
// Cuando Felipe pregunta por un contacto (la skill /wsp lo registra en WaActiveTopic) y
// ese contacto le escribe por WhatsApp dentro de ActiveWindowMinutes, el notifier le avisa
// proactivamente a Felipe por Teams.
//
// La connection string a MSSQL NO vive aqui: se reusa IdempotencyOptions.ConnectionString
// (misma base PhipesAssistant). Este options solo cubre lo especifico del notifier.
public sealed class WaNotifierOptions
{
    public const string SectionName = "WaNotifier";

    // Master switch. Default false: el feature arranca apagado y se habilita por User Secret
    // (WaNotifier:Enabled=true) una vez creadas las tablas y seteado el FelipeUpn.
    public bool Enabled { get; init; } = false;

    // Ruta al staging que escribe el bridge de WhatsApp (una linea JSON por mensaje).
    // El notifier solo LEE; nunca purga ni mueve (de eso se encarga la tarea WhatsApp-Derive).
    public string StagingPath { get; init; } = @"C:\Services\WhatsAppBridge\inbox\inbox.jsonl";

    // Cada cuantos segundos despierta el notifier a revisar el staging.
    public int IntervalSeconds { get; init; } = 45;

    // Ventana en minutos durante la cual un tema sigue "activo" tras la pregunta de Felipe.
    // Solo se vigilan los contactos preguntados dentro de esta ventana.
    public int ActiveWindowMinutes { get; init; } = 15;

    // UPN de Felipe en M365 (destinatario del aviso por Teams). NO se hardcodea un valor
    // real: default vacio, se setea por User Secret (WaNotifier:FelipeUpn). Si queda vacio,
    // el notifier no puede resolver el chat 1:1 y no avisa (lo registra en el log).
    public string FelipeUpn { get; init; } = "";
}
