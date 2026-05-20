using System.Net;

namespace Phipes.Assistant.DdnsWorker.Services;

public interface IPublicIpProvider
{
    string Name { get; }

    Task<IPAddress> GetCurrentAsync(CancellationToken cancellationToken);
}
