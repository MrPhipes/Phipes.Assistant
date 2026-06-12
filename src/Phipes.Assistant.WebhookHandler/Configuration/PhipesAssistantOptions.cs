using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuracion de la DB local PhipesAssistant donde viven ProjectContext + Sessions
// + grafo (Nodes/Edges) para Sarah-server. Los endpoints REST de
// /api/context y /api/sessions consumen esta DB.
public sealed class PhipesAssistantOptions
{
    public const string SectionName = "PhipesAssistant";

    // Connection string a la DB. Tipicamente Integrated Security con
    // svc-webhook-handler que ya tiene permisos. Cuando se necesite separar
    // por SQL login, pasa por aqui sin tocar codigo.
    [Required] public string ConnectionString { get; init; } = "";

    // API key que se valida en el header `X-Api-Key` de los endpoints REST
    // /api/context y /api/sessions. Si esta vacio, los endpoints rechazan
    // todas las peticiones por defecto (fail-closed). Vive en User Secrets.
    public string ApiKey { get; init; } = "";
}
