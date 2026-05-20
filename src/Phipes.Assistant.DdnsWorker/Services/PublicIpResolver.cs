using System.Net;
using Microsoft.Extensions.Logging;

namespace Phipes.Assistant.DdnsWorker.Services;

/// <summary>
/// Compone proveedores de IP pública: intenta el primario, si falla cae al fallback.
/// </summary>
public sealed class PublicIpResolver
{
    private readonly IEnumerable<IPublicIpProvider> _providers;
    private readonly ILogger<PublicIpResolver> _logger;

    public PublicIpResolver(IEnumerable<IPublicIpProvider> providers, ILogger<PublicIpResolver> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task<IPAddress> ResolveAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var provider in _providers)
        {
            try
            {
                var ip = await provider.GetCurrentAsync(cancellationToken);
                _logger.LogDebug("Provider {Provider} resolved public IP {Ip}", provider.Name, ip);
                return ip;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} falló, intentando siguiente", provider.Name);
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            "Ningún IPublicIpProvider pudo resolver la IP pública",
            lastError);
    }
}
