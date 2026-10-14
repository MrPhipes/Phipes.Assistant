using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.OpsBroker.Configuration;

// Configuración del Ops Broker — el servicio privilegiado (corre como svc-sarah-ops)
// que sirve infra y ejecuta ops acotadas SOLO para turnos del Owner (Felipe).
// Bound desde appsettings + UserSecrets. La sección es "OpsBroker".
public sealed class OpsBrokerOptions
{
    [Required] public string ListenUrl { get; set; } = "http://127.0.0.1:5060";

    // Clave HMAC compartida SOLO entre el WebhookHandler y este broker. El handler
    // firma con ella un token efímero (SARAH_OPS_TOKEN) únicamente en turnos del Owner;
    // el broker valida firma + expiración. Es lo que prueba "este request viene de un
    // turno Owner autenticado", no falsificable por un proceso que no tenga la clave.
    [Required] public string SharedHmacKey { get; set; } = "";

    // Ventana de validez del token efímero (segundos). Corto: el handler lo emite por
    // turno. Replay dentro de la ventana es de bajo riesgo (loopback only).
    public int TokenWindowSeconds { get; set; } = 180;

    // Path al archivo de infraestructura que el broker puede leer y servir (.context\).
    // svc-sarah-ops tiene ACL de lectura sobre él; svc-webhook-handler NO.
    [Required] public string InfrastructureFilePath { get; set; } = "";

    // Connection string al grafo de Felipe (todos los rings). Integrated Security:
    // corre como svc-sarah-ops, que tiene CONNECT de lectura concedido.
    public string GraphConnectionString { get; set; } = "";

    // Whitelist de servicios Windows que el broker puede start/stop/restart. Cualquier
    // nombre fuera de esta lista se rechaza. Vacío = ops de servicio deshabilitadas.
    public string[] AllowedServices { get; set; } = Array.Empty<string>();

    // Raíz física bajo la cual el broker puede crear sitios IIS. Un physicalPath fuera
    // de esta raíz se rechaza. Vacío = creación de sitios IIS deshabilitada.
    public string AllowedSiteRoot { get; set; } = "";

    // NAS (TrueNAS) — para servir el ESTADO de las apps Docker a turnos del Owner sin que
    // Sarah-server toque el vault. La URL base no es secreta (va en appsettings); el token
    // SÍ es secreto y vive en User Secrets del broker (perfil svc-sarah-ops). El broker lo
    // usa internamente y nunca lo devuelve: el endpoint /ops/nas-status retorna solo estado.
    // Vacío = consulta de estado del NAS deshabilitada.
    public string NasBaseUrl { get; set; } = "";
    public string NasApiToken { get; set; } = "";

    // Huella SHA-256 (hex) del certificado HTTPS del NAS. Si el NAS usa un cert self-signed
    // se fija aqui y solo ese cert se acepta. Vacio = se exige cadena valida (cert de la CA).
    public string NasCertThumbprint { get; set; } = "";

    // Umbrales del monitoreo del NAS (ver NasHealthEvaluator).
    public int NasScrubMaxAgeDays { get; set; } = 35;
    public int NasSnapshotMaxAgeHours { get; set; } = 26;
    public int NasPoolUsageWarningPercent { get; set; } = 80;
    public int NasPoolUsageCriticalPercent { get; set; } = 90;
    public int NasUptimeMaintenanceDays { get; set; } = 90;

    // Initiators iSCSI que deben tener sesion abierta (ej. SRV-CORE con el disco de WSB).
    public string[] NasExpectedIscsiInitiators { get; set; } = Array.Empty<string>();

    // Apps detenidas a proposito: no generan aviso de "app detenida".
    public string[] NasIgnoredApps { get; set; } = Array.Empty<string>();
}
