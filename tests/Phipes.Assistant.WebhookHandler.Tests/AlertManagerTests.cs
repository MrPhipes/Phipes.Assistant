using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

// Tests del AlertManager. Como Record(...) dispara el send a Teams en Task.Run de
// fondo, los asserts de "se envio" o "no se envio" requieren un pequeno sleep tras
// cada Record para dar tiempo a que el Task.Run termine. Es feo pero la alternativa
// (inyectar un ITaskScheduler propio) es mucho codigo para poco beneficio.
public sealed class AlertManagerTests
{
    private static MonitoringOptions BuildOptions(bool enabled = true, int suppressionMinutes = 30, int? overrideThreshold = null)
    {
        return new MonitoringOptions
        {
            Enabled = enabled,
            AlertTarget = "19:test-chat@thread.v2",
            AlertPrefix = "[TEST]",
            SuppressionMinutes = suppressionMinutes,
            CheckIntervalSeconds = 60,
            ClaudeTimeouts = new AlertRule
            {
                Enabled = true,
                Threshold = overrideThreshold ?? 3,
                WindowMinutes = 5,
                Label = "Claude timeouts"
            },
            DecryptFailures = new AlertRule { Enabled = true, Threshold = 5, WindowMinutes = 60, Label = "Decrypt failures" },
            JwtInvalid = new AlertRule { Enabled = true, Threshold = 1, WindowMinutes = 1440, Label = "JWT invalid" },
            SubscriptionRenewalFailures = new AlertRule { Enabled = true, Threshold = 2, WindowMinutes = 60, Label = "Renewal" },
            ClaudeApiErrors = new AlertRule { Enabled = true, Threshold = 3, WindowMinutes = 10, Label = "Claude API" }
        };
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            // POST /chats/{id}/messages devuelve 201. Tambien resolver chatId via 19:
            // directo - no llama /users/me/id/chats, asi que CallCount cuenta solo el
            // POST del mensaje.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"id\":\"msg-id\"}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        private readonly CountingHandler _handler;
        public StubHttpFactory(CountingHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler) { BaseAddress = null };
    }

    private sealed class StubTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("fake-token-for-tests");
    }

    private static (AlertManager mgr, CountingHandler handler) BuildSut(MonitoringOptions opts)
    {
        var handler = new CountingHandler();
        var httpFactory = new StubHttpFactory(handler);
        var services = new ServiceCollection();
        services.AddSingleton<IGraphTokenProvider>(new StubTokenProvider());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var mgr = new AlertManager(
            Options.Create(opts),
            scopeFactory,
            httpFactory,
            NullLogger<AlertManager>.Instance);
        return (mgr, handler);
    }

    [Fact]
    public async Task BelowThreshold_DoesNotAlert()
    {
        var (mgr, handler) = BuildSut(BuildOptions(overrideThreshold: 3));
        mgr.Record(AlertCategory.ClaudeTimeouts, "first");
        mgr.Record(AlertCategory.ClaudeTimeouts, "second");
        // Threshold = 3, solo llevamos 2. No debe alertar.
        await Task.Delay(150);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ReachingThreshold_TriggersAlert()
    {
        var (mgr, handler) = BuildSut(BuildOptions(overrideThreshold: 3));
        mgr.Record(AlertCategory.ClaudeTimeouts);
        mgr.Record(AlertCategory.ClaudeTimeouts);
        mgr.Record(AlertCategory.ClaudeTimeouts);
        // Tercer Record llega al threshold y dispara el send (Task.Run en background).
        await Task.Delay(300);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AfterAlert_AntiSpamSuppresses()
    {
        var (mgr, handler) = BuildSut(BuildOptions(overrideThreshold: 1, suppressionMinutes: 30));
        // Threshold 1 - cada Record dispara alerta. Pero suppression de 30 min impide que
        // las siguientes 2 lleguen al chat.
        mgr.Record(AlertCategory.ClaudeTimeouts, "first");
        await Task.Delay(150);
        mgr.Record(AlertCategory.ClaudeTimeouts, "second");
        mgr.Record(AlertCategory.ClaudeTimeouts, "third");
        await Task.Delay(150);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task MasterSwitchOff_NeverAlerts()
    {
        var (mgr, handler) = BuildSut(BuildOptions(enabled: false, overrideThreshold: 1));
        mgr.Record(AlertCategory.JwtInvalid, "anything");
        mgr.Record(AlertCategory.ClaudeTimeouts, "anything");
        await Task.Delay(150);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DisabledRule_DoesNotAlert()
    {
        var opts = BuildOptions(overrideThreshold: 1);
        // Desactivar solo la regla JwtInvalid; las otras quedan activas.
        var withDisabled = new MonitoringOptions
        {
            Enabled = opts.Enabled,
            AlertTarget = opts.AlertTarget,
            AlertPrefix = opts.AlertPrefix,
            SuppressionMinutes = opts.SuppressionMinutes,
            CheckIntervalSeconds = opts.CheckIntervalSeconds,
            ClaudeTimeouts = opts.ClaudeTimeouts,
            DecryptFailures = opts.DecryptFailures,
            JwtInvalid = new AlertRule { Enabled = false, Threshold = 1, WindowMinutes = 1440, Label = "JWT invalid" },
            SubscriptionRenewalFailures = opts.SubscriptionRenewalFailures,
            ClaudeApiErrors = opts.ClaudeApiErrors
        };
        var (mgr, handler) = BuildSut(withDisabled);
        mgr.Record(AlertCategory.JwtInvalid, "should be ignored");
        await Task.Delay(150);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DifferentCategories_HaveIndependentCounters()
    {
        var (mgr, handler) = BuildSut(BuildOptions(overrideThreshold: 3));
        // 2 timeouts (bajo threshold 3) + 1 JwtInvalid (threshold 1 → alerta).
        mgr.Record(AlertCategory.ClaudeTimeouts);
        mgr.Record(AlertCategory.ClaudeTimeouts);
        mgr.Record(AlertCategory.JwtInvalid);

        // Polling con timeout: el Task.Run del send puede tardar mas de 300ms cuando
        // hay multiples categorias en flight a la vez. Aceptamos hasta 1.5s.
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        while (handler.CallCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.Equal(1, handler.CallCount);
    }
}
