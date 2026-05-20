namespace Phipes.Assistant.DdnsWorker.Configuration;

public sealed class DdnsOptions
{
    public const string SectionName = "Ddns";

    public int PollIntervalSeconds { get; init; } = 300;

    public CloudflareOptions Cloudflare { get; init; } = new();

    public UnifiOptions Unifi { get; init; } = new();

    public IpifyOptions Ipify { get; init; } = new();
}

public sealed class CloudflareOptions
{
    public string ApiTokenPath { get; init; } = "";

    public IReadOnlyList<ManagedRecord> Records { get; init; } = [];
}

public sealed record ManagedRecord(
    string Zone,
    string ZoneId,
    string Hostname,
    string RecordId,
    bool Proxied);

public sealed class UnifiOptions
{
    // Sin default: cada despliegue define la URL del UDM en appsettings.Local.json.
    // La IP del gateway de cada LAN es distinta; no se hardcodea.
    public string BaseUrl { get; init; } = "";

    public string ApiTokenPath { get; init; } = "";

    public string SiteName { get; init; } = "default";

    public bool VerifyTls { get; init; } = false;

    public int TimeoutSeconds { get; init; } = 10;
}

public sealed class IpifyOptions
{
    public string Endpoint { get; init; } = "https://api.ipify.org?format=text";

    public int TimeoutSeconds { get; init; } = 10;
}
