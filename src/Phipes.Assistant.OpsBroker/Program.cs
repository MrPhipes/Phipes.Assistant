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
        builder.Services.AddSingleton<NasHealthService>();
        builder.Services.AddSingleton<NasOpsTracker>();
        builder.Services.AddSingleton<NasOpsService>();
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

        // GET /infra/nas → salud del NAS (TrueNAS por JSON-RPC): sistema, actualizaciones, pools,
        // scrub, apps, snapshots, SMB/iSCSI, SMART, alertas y hallazgos con severidad. El broker
        // consulta con su token interno y nunca lo devuelve. Lo usan Felipe (preguntas por Teams)
        // y el NasMonitor del WebhookHandler (avisos proactivos).
        app.MapGet("/infra/nas", async (NasHealthService nas, CancellationToken ct) =>
        {
            try { return Results.Json(await nas.GetHealthAsync(ct)); }
            catch (Exception ex)
            {
                FileLogger.Write("Nas", $"health ERROR: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // GET /ops/nas-status → compatibilidad: solo nombre + estado de las apps del NAS.
        app.MapGet("/ops/nas-status", async (NasHealthService nas, CancellationToken ct) =>
        {
            try
            {
                var h = await nas.GetHealthAsync(ct);
                return Results.Json(new { apps = h.Apps.Select(a => new { name = a.Name, state = a.State }) });
            }
            catch (Exception ex)
            {
                FileLogger.Write("Nas", $"status ERROR: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // Acciones de mantencion del NAS. Solo turnos Owner (gate de arriba) y SIEMPRE despues de que
        // Felipe confirme por Teams (lo exige el prompt del Owner). Devuelven 202 + jobId; el avance se
        // consulta en /infra/nas/jobs/{id}. Resguardos en NasOpsService (ventana de respaldos, una a la vez).
        app.MapPost("/infra/nas/apps/{name}/upgrade", async (string name, NasOpsService ops, CancellationToken ct) =>
        {
            var r = await ops.UpgradeAppAsync(name, ct);
            return Results.Json(new { message = r.Message, jobId = r.JobId, estimatedMinutes = r.EstimatedMinutes }, statusCode: r.Status);
        });

        app.MapPost("/infra/nas/update", async (NasOpsService ops, CancellationToken ct) =>
        {
            var r = await ops.UpdateSystemAsync(ct);
            return Results.Json(new { message = r.Message, jobId = r.JobId, estimatedMinutes = r.EstimatedMinutes }, statusCode: r.Status);
        });

        app.MapGet("/infra/nas/jobs/{id:long}", async (long id, NasOpsService ops, CancellationToken ct) =>
        {
            try
            {
                var job = await ops.GetJobAsync(id, ct);
                return job is null ? Results.NotFound(new { message = $"No existe el job {id}." }) : Results.Json(job);
            }
            catch (Exception ex)
            {
                // Durante el reinicio del NAS no hay respuesta: se informa tal cual, no es un error del broker.
                return Results.Json(new { message = $"El NAS no responde (¿reiniciando?): {ex.Message}" }, statusCode: 503);
            }
        });

        app.Run();
    }
}

public sealed record ServiceRequest(string? Action, string? Name);
public sealed record IisSiteRequest(string? Name, string? Bindings, string? PhysicalPath);
