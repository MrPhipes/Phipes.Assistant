using Microsoft.Extensions.Options;
using Phipes.Assistant.TokenBroker.Configuration;
using System.Runtime.Versioning;

namespace Phipes.Assistant.TokenBroker.Services;

// Refresca el RT proactivamente cada N (default 30min) aunque nadie pida AT.
// Razón: si Microsoft revoca el RT por inactividad, lo detectamos en el próximo
// tick — no en producción cuando un user espera respuesta. También: una rotación
// más frecuente del RT reduce ventana en la que un RT comprometido es útil.
[SupportedOSPlatform("windows")]
public sealed class ProactiveRefreshHostedService : BackgroundService
{
    private readonly TokenStore _store;
    private readonly BrokerOptions _opts;
    private readonly ILogger<ProactiveRefreshHostedService> _logger;

    public ProactiveRefreshHostedService(
        TokenStore store,
        IOptions<BrokerOptions> opts,
        ILogger<ProactiveRefreshHostedService> logger)
    {
        _store = store;
        _opts = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProactiveRefresh corriendo cada {every}", _opts.ProactiveRefreshEvery);

        // Refresh inmediato al arrancar — confirma que el RT funciona.
        try { await _store.RefreshAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogError(ex, "Refresh inicial FAIL"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opts.ProactiveRefreshEvery, stoppingToken);
                await _store.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProactiveRefresh FAIL");
            }
        }
    }
}
