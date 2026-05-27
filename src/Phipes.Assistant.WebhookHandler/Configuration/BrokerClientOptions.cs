using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.WebhookHandler.Configuration;

// Configuración para hablar con Phipes.Assistant.TokenBroker (servicio local
// que aísla el RT bajo svc-token-broker). Si Broker:ListenUrl está vacío en
// secrets, el handler cae al GraphTokenProvider legacy (lectura directa del
// cred.xml). Esto da migración gradual.
public sealed class BrokerClientOptions
{
    public const string SectionName = "Broker";

    // URL del broker. Default: vacío (legacy path). Producción: "http://127.0.0.1:5050"
    public string ListenUrl { get; init; } = "";

    // Secret compartido. Si ListenUrl está set, este también debe estarlo.
    public string Secret { get; init; } = "";
}
