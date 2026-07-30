namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuracion del endpoint GET /api/m365-token, que entrega a la CONSOLA
// (Sarah-fhernandez en casa/oficina) un ACCESS token de Graph para sconnor.
//
// El refresh token NUNCA sale del servidor: lo rota el TokenBroker (svc-token-broker)
// y aca solo se devuelve un access token corto (no rota). Asi el unico rotador del RT
// sigue siendo el broker -> la carrera del 2026-05-19 es estructuralmente imposible.
//
// Auth en dos capas (fail-closed):
//   1. X-Api-Key dedicada (distinta de PhipesAssistant:ApiKey, para aislar blast radius).
//      Si esta vacia, el endpoint rechaza TODO.
//   2. Allowlist de prefijos de IP de origen (LAN/VPN). Si esta vacia, no restringe por IP
//      (solo confia en la key). En produccion se setea a las redes internas para que aunque
//      la key se filtre, solo la LAN pueda usarla, y aunque el tunel Cloudflare reenvie la
//      ruta, el origen (connector) quede fuera de la allowlist.
public sealed class M365RelayOptions
{
    public const string SectionName = "M365Relay";

    public string ApiKey { get; init; } = "";

    public string[] AllowedSourcePrefixes { get; init; } = System.Array.Empty<string>();
}
