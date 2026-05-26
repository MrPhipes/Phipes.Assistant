using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Phipes.Assistant.TokenBroker.Configuration;
using Phipes.Assistant.TokenBroker.Services;
using System.Runtime.Versioning;

namespace Phipes.Assistant.TokenBroker;

[SupportedOSPlatform("windows")]
public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration.AddUserSecrets<BrokerOptions>(optional: true, reloadOnChange: true);

        builder.Services
            .AddOptions<BrokerOptions>()
            .Bind(builder.Configuration.GetSection("Broker"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<TokenStore>();
        builder.Services.AddHostedService<ProactiveRefreshHostedService>();
        builder.Services.AddWindowsService(o => o.ServiceName = "Phipes.Assistant.TokenBroker");

        // Kestrel solo escucha en localhost loopback. Cualquier otro intento se
        // descarta a nivel TCP (no llega al endpoint).
        var listenUrl = builder.Configuration["Broker:ListenUrl"] ?? "http://127.0.0.1:5050";
        builder.WebHost.UseUrls(listenUrl);

        var app = builder.Build();

        var opts = app.Services.GetRequiredService<IOptions<BrokerOptions>>().Value;

        // Middleware: cualquier request sin header X-Broker-Secret correcto → 401.
        // Defensa contra procesos del mismo box que descubran el puerto.
        app.Use(async (ctx, next) =>
        {
            var got = ctx.Request.Headers["X-Broker-Secret"].ToString();
            if (string.IsNullOrEmpty(got) || !CryptoEquals(got, opts.Secret))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("unauthorized");
                return;
            }
            await next();
        });

        // GET /token → devuelve AT válido. JSON {"access_token":"...", "expires_at_utc":"..."}
        app.MapGet("/token", async (TokenStore store, CancellationToken ct) =>
        {
            try
            {
                var at = await store.GetAccessTokenAsync(ct);
                var health = store.GetHealth();
                return Results.Json(new
                {
                    access_token = at,
                    expires_at_utc = health.AccessTokenExpiresUtc?.ToString("o"),
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });

        // POST /refresh → fuerza rotación inmediata.
        app.MapPost("/refresh", async (TokenStore store, CancellationToken ct) =>
        {
            try
            {
                await store.RefreshAsync(ct);
                return Results.Json(new { status = "ok" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });

        // GET /health → estado sin AT (no expone AT, solo metadata).
        app.MapGet("/health", (TokenStore store) =>
        {
            var h = store.GetHealth();
            return Results.Json(h);
        });

        app.Run();
    }

    // Comparación de strings en tiempo constante para evitar timing side-channel
    // en la validación del secret. Para secrets cortos casi irrelevante, pero
    // hábito sano.
    private static bool CryptoEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
