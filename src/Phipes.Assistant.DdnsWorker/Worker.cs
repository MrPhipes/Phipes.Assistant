using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phipes.Assistant.DdnsWorker.Configuration;
using Phipes.Assistant.DdnsWorker.Services;

namespace Phipes.Assistant.DdnsWorker;

public sealed class Worker : BackgroundService
{
    private readonly PublicIpResolver _ipResolver;
    private readonly CloudflareDnsClient _cloudflare;
    private readonly DdnsOptions _options;
    private readonly ILogger<Worker> _logger;

    public Worker(
        PublicIpResolver ipResolver,
        CloudflareDnsClient cloudflare,
        IOptions<DdnsOptions> options,
        ILogger<Worker> logger)
    {
        _ipResolver = ipResolver;
        _cloudflare = cloudflare;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "DDNS Worker iniciado. Intervalo: {Interval}. Records gestionados: {Count}",
            interval,
            _options.Cloudflare.Records.Count);

        // Tick inmediato
        await RunCycleSafelyAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleSafelyAsync(stoppingToken);
        }
    }

    private async Task RunCycleSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunCycleAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ciclo DDNS falló con excepción no manejada");
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var publicIp = await _ipResolver.ResolveAsync(cancellationToken);
        var publicIpString = publicIp.ToString();

        foreach (var record in _options.Cloudflare.Records)
        {
            var current = await _cloudflare.GetRecordContentAsync(record, cancellationToken);

            if (current is null)
            {
                _logger.LogWarning(
                    "No pude leer record {Hostname} ({RecordId}) en Cloudflare, skip",
                    record.Hostname, record.RecordId);
                continue;
            }

            if (string.Equals(current, publicIpString, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Record {Hostname} ya está en {Ip}, sin cambios",
                    record.Hostname, publicIpString);
                continue;
            }

            _logger.LogInformation(
                "Record {Hostname} difiere: {OldIp} -> {NewIp}. Actualizando...",
                record.Hostname, current, publicIpString);

            var ok = await _cloudflare.UpdateRecordContentAsync(record, publicIpString, cancellationToken);
            if (ok)
            {
                _logger.LogInformation(
                    "Record {Hostname} actualizado a {NewIp}",
                    record.Hostname, publicIpString);
            }
            else
            {
                _logger.LogError(
                    "Falló la actualización del record {Hostname} a {NewIp}",
                    record.Hostname, publicIpString);
            }
        }
    }
}
