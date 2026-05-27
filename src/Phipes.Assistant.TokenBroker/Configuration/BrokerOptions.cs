using System.ComponentModel.DataAnnotations;

namespace Phipes.Assistant.TokenBroker.Configuration;

// Configuración del broker — bound desde appsettings + UserSecrets en setup, o
// Environment en deploy. La sección "Broker" contiene todo lo necesario.
public sealed class BrokerOptions
{
    [Required]
    public string ListenUrl { get; set; } = "http://127.0.0.1:5050";

    // Path absoluto al cred.xml. Cifrado DPAPI por el usuario que corre el
    // servicio (svc-token-broker). Si otro user intenta Import-Clixml, falla.
    [Required]
    public string CredXmlPath { get; set; } = "";

    [Required]
    public string TenantId { get; set; } = "";

    [Required]
    public string ClientId { get; set; } = "";

    // Scope completo que se pide en cada refresh. Microsoft solo devuelve los
    // que estén consentidos al tenant.
    [Required]
    public string Scope { get; set; } = "";

    // Cada cuánto el broker hace un refresh proactivo aunque nadie pida AT.
    // Esto mantiene el RT fresco — un RT inactivo por más de 90d se revoca,
    // y rotaciones más frecuentes detectan tempranamente revocaciones.
    public TimeSpan ProactiveRefreshEvery { get; set; } = TimeSpan.FromMinutes(30);

    // Si el AT cache vence en menos de este margen, refresh antes de servir.
    public TimeSpan AccessTokenCacheMargin { get; set; } = TimeSpan.FromMinutes(5);

    // Secreto que los clientes (handler) envían en header X-Broker-Secret.
    // Si no coincide → 401. Defensa contra otros procesos del box que intenten
    // pegar al endpoint local.
    [Required]
    public string Secret { get; set; } = "";
}
