using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phipes.Assistant.OpsBroker.Configuration;
using Phipes.Assistant.OpsBroker.Services;
using Phipes.Assistant.OpsBroker.Utilities;

namespace Phipes.Assistant.OpsBroker;

// Ops Broker — servicio privilegiado (svc-sarah-ops) que sirve infra y ejecuta ops
// acotadas SOLO a turnos del Owner. Escucha en loopback; cada request debe traer un
// token efímero firmado por el WebhookHandler (header X-Owner-Token) que solo se emite
// en turnos clasificados TrustRing.Owner. Doble gate: la skill verifica SARAH_OWNER_TURN,
// el broker verifica criptográficamente el token.
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

        builder.Configuration.AddUserSecrets<OpsBrokerOptions>(optional: true, reloadOnChange: true);
        builder.Services.AddOptions<OpsBrokerOptions>()
            .Bind(builder.Configuration.GetSection("OpsBroker"))
            .ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddSingleton<OwnerTokenValidator>();
        builder.Services.AddSingleton<InfraReader>();
        builder.Services.AddSingleton<OpsExecutor>();
        builder.Services.AddWindowsService(o => o.ServiceName = "Phipes.Assistant.OpsBroker");

        var listenUrl = builder.Configuration["OpsBroker:ListenUrl"] ?? "http://127.0.0.1:5060";
        builder.WebHost.UseUrls(listenUrl);

        var app = builder.Build();

        // Gate: todo request debe traer X-Owner-Token válido. Sin token válido → 401.
        app.Use(async (ctx, next) =>
        {
            var validator = ctx.RequestServices.GetRequiredService<OwnerTokenValidator>();
            var token = ctx.Request.Headers["X-Owner-Token"].ToString();
            if (!validator.Validate(token, out var reason))
            {
                FileLogger.Write("Auth", $"401 {ctx.Request.Path}: {reason}");
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("unauthorized");
                return;
            }
            await next();
        });

        app.MapGet("/health", () => Results.Json(new { status = "ok", service = "Phipes.Assistant.OpsBroker" }));

        // GET /infra/context → contenido de infrastructure.md (topología, hosts, servicios).
        app.MapGet("/infra/context", async (InfraReader infra, CancellationToken ct) =>
        {
            try { return Results.Text(await infra.ReadInfrastructureAsync(ct)); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // GET /infra/graph?entity=srv-pacificdev → vecinos en el grafo de infra.
        app.MapGet("/infra/graph", async (string entity, InfraReader infra, CancellationToken ct) =>
        {
            try { return Results.Json(await infra.QueryGraphAsync(entity, ct)); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // POST /ops/service { action, name } → start|stop|restart de servicio whitelisted.
        app.MapPost("/ops/service", (ServiceRequest req, OpsExecutor ops) =>
        {
            var r = ops.ServiceAction(req.Action ?? "", req.Name ?? "");
            return Results.Json(r, statusCode: r.Success ? 200 : 400);
        });

        // POST /ops/iis-site { name, bindings, physicalPath } → crea sitio bajo AllowedSiteRoot.
        app.MapPost("/ops/iis-site", (IisSiteRequest req, OpsExecutor ops) =>
        {
            var r = ops.CreateIisSite(req.Name ?? "", req.Bindings ?? "", req.PhysicalPath ?? "");
            return Results.Json(r, statusCode: r.Success ? 200 : 400);
        });

        app.Run();
    }
}

public sealed record ServiceRequest(string? Action, string? Name);
public sealed record IisSiteRequest(string? Name, string? Bindings, string? PhysicalPath);
