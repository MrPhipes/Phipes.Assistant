namespace Phipes.Assistant.WebhookHandler.Configuration;

// Monitoreo del NAS (TrueNAS) por Sarah-server. El handler NO habla con el NAS: le pide la
// salud al Ops Broker (GET /infra/nas, con token Owner firmado) y avisa a Felipe por Teams.
//   - Hallazgos critical / warning nuevos o resueltos → aviso inmediato.
//   - Hallazgos maintenance (versiones, apps, uptime) → resumen semanal.
//   - Broker o NAS sin respuesta FailuresBeforeAlert veces seguidas → aviso.
public sealed class NasMonitorOptions
{
    public const string SectionName = "NasMonitor";

    public bool Enabled { get; set; } = false;

    public int IntervalMinutes { get; set; } = 15;

    // Consultas fallidas seguidas antes de avisar que el NAS no responde (evita avisar por
    // un corte puntual).
    public int FailuresBeforeAlert { get; set; } = 2;

    // UPN de Felipe para resolver el chat 1:1 (mismo valor que WaNotifier:FelipeUpn).
    public string FelipeUpn { get; set; } = "";

    // Resumen semanal de mantencion, en hora de Chile.
    public DayOfWeek WeeklySummaryDay { get; set; } = DayOfWeek.Monday;
    public int WeeklySummaryHour { get; set; } = 9;
}
