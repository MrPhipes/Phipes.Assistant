using System.Net;
using Microsoft.Extensions.Logging;

namespace Phipes.Assistant.DdnsWorker.Services;

/// <summary>
/// Fallback: consulta ipify.org cuando el UDM no responde (mantenimiento, reboot).
/// </summary>
public sealed class IpifyIpProvider : IPublicIpProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<IpifyIpProvider> _logger;

    public string Name => "Ipify";

    public IpifyIpProvider(HttpClient http, ILogger<IpifyIpProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IPAddress> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var raw = (await _http.GetStringAsync("/", cancellationToken)).Trim();

        if (!IPAddress.TryParse(raw, out var ip))
            throw new InvalidDataException($"ipify devolvió respuesta inválida: '{raw}'");

        return ip;
    }
}
