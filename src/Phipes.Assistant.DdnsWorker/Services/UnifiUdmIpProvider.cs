using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phipes.Assistant.DdnsWorker.Configuration;
using Phipes.Assistant.DdnsWorker.Utilities;

namespace Phipes.Assistant.DdnsWorker.Services;

/// <summary>
/// Lee la WAN IP del UDM via su API legacy /proxy/network/api/s/{site}/stat/health.
/// El UDM tiene el modem ISP en modo bridge, por lo que la WAN IP del UDM ES la
/// IP pública de la conexión.
/// </summary>
public sealed class UnifiUdmIpProvider : IPublicIpProvider
{
    private readonly HttpClient _http;
    private readonly UnifiOptions _options;
    private readonly ILogger<UnifiUdmIpProvider> _logger;

    public string Name => "UnifiUdm";

    public UnifiUdmIpProvider(
        HttpClient http,
        IOptions<DdnsOptions> options,
        ILogger<UnifiUdmIpProvider> logger)
    {
        _http = http;
        _options = options.Value.Unifi;
        _logger = logger;
    }

    public async Task<IPAddress> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var token = PsCredentialReader.ReadPassword(_options.ApiTokenPath);

        var requestUri = $"{_options.BaseUrl.TrimEnd('/')}/proxy/network/api/s/{_options.SiteName}/stat/health";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("X-API-KEY", token);
        request.Headers.Add("Accept", "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("data", out var dataArray)
            || dataArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("UDM /stat/health no devolvió 'data' array");
        }

        foreach (var entry in dataArray.EnumerateArray())
        {
            if (!entry.TryGetProperty("subsystem", out var subsystem)) continue;
            if (subsystem.GetString() != "wan") continue;
            if (!entry.TryGetProperty("wan_ip", out var wanIp)) continue;

            var ipString = wanIp.GetString();
            if (string.IsNullOrWhiteSpace(ipString))
                throw new InvalidDataException("UDM WAN subsystem sin wan_ip");

            if (!IPAddress.TryParse(ipString, out var ip))
                throw new InvalidDataException($"UDM devolvió wan_ip inválido: {ipString}");

            // Log auxiliar: ISP, latencia, uptime
            if (entry.TryGetProperty("isp_name", out var isp))
                _logger.LogDebug("UDM WAN: IP={Ip} ISP={Isp}", ip, isp.GetString());

            return ip;
        }

        throw new InvalidDataException("UDM /stat/health no contiene subsistema 'wan'");
    }
}
