using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Models;
using Phipes.Assistant.WebhookHandler.Services;

var builder = WebApplication.CreateBuilder(args);

// User Secrets se cargan tambien cuando corre bajo IIS (no solo en Development).
// El secrets.json vive en el perfil de la cuenta del app pool en el host.
builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: true);

builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<GraphOptions>()
    .Bind(builder.Configuration.GetSection(GraphOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<EncryptionOptions>()
    .Bind(builder.Configuration.GetSection(EncryptionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ClaudeOptions>()
    .Bind(builder.Configuration.GetSection(ClaudeOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<WebhookAppOptions>()
    .Bind(builder.Configuration.GetSection(WebhookAppOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RenewerOptions>()
    .Bind(builder.Configuration.GetSection(RenewerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<IdempotencyOptions>()
    .Bind(builder.Configuration.GetSection(IdempotencyOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<MonitoringOptions>()
    .Bind(builder.Configuration.GetSection(MonitoringOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TrustRingOptions>()
    .Bind(builder.Configuration.GetSection(TrustRingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// WaNotifier no lleva [Required], asi que solo ValidateOnStart (binding + startup check)
// sin ValidateDataAnnotations.
builder.Services.AddOptions<WaNotifierOptions>()
    .Bind(builder.Configuration.GetSection(WaNotifierOptions.SectionName))
    .ValidateOnStart();

// MetaNotifier (moderacion Facebook/Instagram): mismo trato que WaNotifier — opcional,
// Enabled=false por default, sin [Required].
builder.Services.AddOptions<MetaNotifierOptions>()
    .Bind(builder.Configuration.GetSection(MetaNotifierOptions.SectionName))
    .ValidateOnStart();

// NasMonitor (salud del NAS via Ops Broker): opcional, Enabled=false por default.
builder.Services.AddOptions<NasMonitorOptions>()
    .Bind(builder.Configuration.GetSection(NasMonitorOptions.SectionName))
    .ValidateOnStart();

// PhipesAssistantOptions: connection string a la DB local (PhipesAssistant) + API key
// para los endpoints REST /api/context y /api/sessions. Connection string es Required;
// ApiKey puede venir vacio y en ese caso los endpoints rechazan TODO (fail-closed).
builder.Services.AddOptions<PhipesAssistantOptions>()
    .Bind(builder.Configuration.GetSection(PhipesAssistantOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

// M365Relay: endpoint /api/m365-token para la consola (Sarah-fhernandez). ApiKey vacia
// => fail-closed (endpoint deshabilitado). No usa ValidateOnStart para no romper el
// arranque si aun no se ha provisto la key (migracion gradual).
builder.Services.Configure<M365RelayOptions>(builder.Configuration.GetSection(M365RelayOptions.SectionName));

builder.Services.AddSingleton<IIdempotencyStore, SqlIdempotencyStore>();
builder.Services.AddSingleton<IWorkingMemoryReader, SqlWorkingMemoryReader>();
builder.Services.AddSingleton<ITrustRingClassifier, TrustRingClassifier>();
builder.Services.AddHttpClient<IJwtNotificationValidator, JwtNotificationValidator>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<INotificationDecrypter, NotificationDecrypter>();
builder.Services.AddSingleton<IClaudeCodeInvoker, ClaudeCodeInvoker>();
builder.Services.AddSingleton<IProjectContextService, ProjectContextService>();
builder.Services.AddHttpClient<IWebhookAppTokenProvider, WebhookAppTokenProvider>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("renewer", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHostedService<SubscriptionRenewer>();

builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    o.SingleLine = true;
});

// IGraphTokenProvider — si Broker:ListenUrl está configurado, usar el broker
// remoto (servicio aislado bajo svc-token-broker). Si no, fallback al legacy
// que lee cred.xml directo. La idea: migración gradual sin romper deploys.
builder.Services.AddOptions<BrokerClientOptions>()
    .Bind(builder.Configuration.GetSection(BrokerClientOptions.SectionName));

var brokerSection = builder.Configuration.GetSection(BrokerClientOptions.SectionName);
var brokerUrl = brokerSection["ListenUrl"];
if (!string.IsNullOrWhiteSpace(brokerUrl))
{
    builder.Services.AddHttpClient<IGraphTokenProvider, BrokerGraphTokenProvider>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(15);
    });
}
else
{
    builder.Services.AddHttpClient<IGraphTokenProvider, GraphTokenProvider>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(15);
    });
}

// HttpClient dedicado para Microsoft Graph API (Teams).
builder.Services.AddHttpClient<ITeamsNotificationHandler, TeamsNotificationHandler>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});

// HttpClient dedicado para Microsoft Graph API (Mail).
builder.Services.AddHttpClient<IMailNotificationHandler, MailNotificationHandler>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

// HttpClient dedicado para Lifecycle (PATCH subscriptions).
builder.Services.AddHttpClient<ILifecycleHandler, LifecycleHandler>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});

// HttpClient dedicado para SubscriptionRecreator (POST /subscriptions con encryption cert).
builder.Services.AddHttpClient<ISubscriptionRecreator, SubscriptionRecreator>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

// State store que persiste a disco el id actual de cada Graph subscription. Sobrevive
// al reinicio del app pool: en el startup se hace merge con el User Secrets para que
// el id de auto-recovery se mantenga aunque Felipe no toque el config persistente.
builder.Services.AddSingleton<ISubscriptionStateStore, SubscriptionStateStore>();

// HttpClient dedicado para descargar adjuntos de Teams (hosted images + OneDrive references).
// Timeout mas grande porque puede ser un PDF de varios MB.
builder.Services.AddHttpClient<IMessageAttachmentExtractor, MessageAttachmentExtractor>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
});

// AlertManager: singleton porque mantiene contadores rolling-window y last-alert state
// en memoria. Usa IHttpClientFactory (named "alerts") y IServiceScopeFactory para
// resolver el IGraphTokenProvider sin captar un scope vivo.
builder.Services.AddHttpClient("alerts", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<IAlertManager, AlertManager>();

// WaProactiveNotifier (Fase 2): vigila el staging de WhatsApp y avisa a Felipe por Teams
// cuando un contacto por el que pregunto le escribe dentro de la ventana. Named HttpClient
// "wanotifier" para las llamadas a Graph; resuelve IGraphTokenProvider por tick via
// IServiceScopeFactory (no captura scope vivo). Si WaNotifier:Enabled=false, el servicio
// arranca pero no arma el timer (no-op).
builder.Services.AddHttpClient("wanotifier", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<WaProactiveNotifier>();

// MetaProactiveNotifier (moderacion de comentarios FB/IG de la plataforma MetaSocial):
// vigila PacificDevMeta.dbo.WebhookEvents y avisa a Felipe por Teams con el menu de
// acciones; la ejecucion de la instruccion la hace la skill /meta-comentario de Sarah.
builder.Services.AddHttpClient("metanotifier", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<MetaProactiveNotifier>();

// NasMonitor: pide la salud del NAS al Ops Broker cada IntervalMinutes y avisa a Felipe por
// Teams de lo nuevo, lo resuelto y, una vez por semana, de la mantencion pendiente.
builder.Services.AddHttpClient("nasmonitor", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHostedService<NasMonitor>();

var app = builder.Build();

// Aplicar state persistido sobre las subscriptions del config. Si el state file
// existe, sus ids ganan sobre los del User Secrets (el state file refleja
// auto-recoveries previos del LifecycleHandler).
{
    var stateStore = app.Services.GetRequiredService<ISubscriptionStateStore>();
    var renewerOpts = app.Services.GetRequiredService<IOptions<RenewerOptions>>().Value;
    stateStore.ApplyPersistedIds(renewerOpts.Subscriptions);
}

// =============== Endpoints ===============

app.MapGet("/", () => Results.Text(
    "Phipes.Assistant.WebhookHandler — ver /health o /webhook/teams",
    "text/plain", Encoding.UTF8));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "Phipes.Assistant.WebhookHandler",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
    timestamp = DateTimeOffset.UtcNow
}));

// === Graph webhook: validacion inicial ===
app.MapGet("/webhook/teams", ([FromQuery] string? validationToken, ILogger<Program> log) =>
{
    if (string.IsNullOrEmpty(validationToken))
    {
        log.LogWarning("GET /webhook/teams sin validationToken");
        return Results.BadRequest("validationToken es obligatorio");
    }
    log.LogInformation("Validation token recibido (len={Len})", validationToken.Length);
    return Results.Text(validationToken, "text/plain", Encoding.UTF8);
});

// === Graph webhook: notificaciones ===
//
// Graph espera 200/202 dentro de 3 segundos, por lo que respondemos de inmediato y
// procesamos las notifications en background (Task.Run con un scope DI propio para
// no compartir HttpClients/services con la request que ya terminó).
app.MapPost("/webhook/teams", async (
    HttpContext ctx,
    IOptions<WebhookOptions> webhookOptions,
    ITeamsNotificationHandler handler,
    IServiceScopeFactory scopeFactory,
    ILogger<Program> log,
    [FromQuery] string? validationToken) =>
{
    // Caso especial: renovacion via POST con validationToken (algunos clientes lo usan).
    if (!string.IsNullOrEmpty(validationToken))
    {
        return Results.Text(validationToken, "text/plain", Encoding.UTF8);
    }

    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync();
    log.LogInformation("Notification recibida ({Bytes} bytes)", raw.Length);

    ChangeNotificationCollection? payload;
    try
    {
        payload = JsonSerializer.Deserialize<ChangeNotificationCollection>(raw);
    }
    catch (JsonException ex)
    {
        log.LogError(ex, "Payload no parseable");
        return Results.BadRequest();
    }

    if (payload?.Value is null || payload.Value.Count == 0)
    {
        log.LogWarning("Payload sin notifications");
        return Results.BadRequest();
    }

    // Validar JWT signatures de validationTokens contra JWKS de Microsoft. Comprueba
    // que la notification realmente viene del backend Graph (no de alguien que descubrio
    // la URL). En modo shadow (default) solo loguea warning si falla. Con
    // Security:RejectInvalidJwts=true se rechaza con 401.
    {
        using var scopeJwt = scopeFactory.CreateScope();
        var jwtValidator = scopeJwt.ServiceProvider.GetRequiredService<IJwtNotificationValidator>();
        var securityOpts = scopeJwt.ServiceProvider.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var tokens = payload.ValidationTokens ?? new List<string>();
        var jwtResult = await jwtValidator.ValidateTokensAsync(tokens);
        if (!jwtResult.IsValid)
        {
            Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Jwt", $"INVALID validationTokens: {jwtResult.FailureReason} (shadowMode={(securityOpts.RejectInvalidJwts ? "REJECT" : "LOG-ONLY")})");
            log.LogWarning("validationTokens invalidos: {Reason}", jwtResult.FailureReason);
            // Solo alertar cuando hay tokens presentes pero con firma/claims invalidos.
            // Ausencia de tokens suele ser ruido (Microsoft retry, scan automatizado).
            if (tokens.Count > 0)
            {
                scopeJwt.ServiceProvider.GetRequiredService<IAlertManager>()
                    .Record(AlertCategory.JwtInvalid, jwtResult.FailureReason);
            }
            if (securityOpts.RejectInvalidJwts)
            {
                return Results.Unauthorized();
            }
        }
        else
        {
            Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Jwt", $"OK validationTokens validados ({(payload.ValidationTokens?.Count ?? 0)} tokens)");
        }
    }

    var expected = webhookOptions.Value.ClientState;

    // Filtramos clientState antes de despachar — fail-fast si Microsoft no validó.
    var valid = payload.Value
        .Where(n => string.IsNullOrEmpty(expected) || n.ClientState == expected)
        .ToList();

    if (payload.Value.Count != valid.Count)
    {
        log.LogWarning("Descartadas {N} notifications por clientState invalido",
            payload.Value.Count - valid.Count);
    }

    // Procesamos cada notification fuera de la request original. Si una falla, las otras
    // siguen. El handler debe loguear su propio error. Distinguimos por resource type:
    // - chats(...) o /me/chats/...   -> Teams chat
    // - /me/messages o Users(...)/Messages(...) -> Mail
    foreach (var notification in valid)
    {
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var scopedLog = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            var resource = notification.Resource ?? "";
            // Mail: empieza con "Users(" (server-side) o "/me/messages" (delegated path).
            // Teams: "chats(...)/messages(...)" tambien contiene "messages" pero NO es mail.
            var isMail = resource.StartsWith("Users(", StringComparison.OrdinalIgnoreCase)
                          || resource.StartsWith("/me/messages", StringComparison.OrdinalIgnoreCase)
                          || resource.StartsWith("me/messages", StringComparison.OrdinalIgnoreCase);
            var kind = isMail ? "mail" : "chat";
            FileLog($"START kind={kind} sub={notification.SubscriptionId} resource={resource} change={notification.ChangeType}");
            try
            {
                // Idempotency check primero: si ya procesamos este (subId, resource),
                // salimos sin descifrar ni invocar al LLM. Microsoft a veces re-entrega.
                var idemStore = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
                var isNew = await idemStore.TryMarkProcessedAsync(
                    notification.SubscriptionId ?? "",
                    resource,
                    kind);
                if (!isNew)
                {
                    FileLog($"DEDUP kind={kind} sub={notification.SubscriptionId} (duplicado, ya procesado)");
                    return;
                }

                if (isMail)
                {
                    var mailHandler = scope.ServiceProvider.GetRequiredService<IMailNotificationHandler>();
                    await mailHandler.HandleAsync(notification);
                }
                else
                {
                    var teamsHandler = scope.ServiceProvider.GetRequiredService<ITeamsNotificationHandler>();
                    await teamsHandler.HandleAsync(notification);
                }
                FileLog($"OK kind={kind} sub={notification.SubscriptionId}");
            }
            catch (Exception ex)
            {
                scopedLog.LogError(ex, "Error procesando notification {SubId} {Resource}",
                    notification.SubscriptionId, notification.Resource);
                FileLog($"ERROR kind={kind} sub={notification.SubscriptionId}: {ex}");
            }
        });
    }

    // 202 Accepted: tomamos posesión, procesaremos.
    return Results.Accepted();
});

// === Lifecycle endpoint separado ===
// Microsoft Graph manda aca eventos del ciclo de vida de la subscription:
// reauthorizationRequired (renovar), subscriptionRemoved, missed.
app.MapPost("/webhook/teams/lifecycle", async (
    HttpContext ctx,
    IOptions<WebhookOptions> webhookOptions,
    IServiceScopeFactory scopeFactory,
    [FromQuery] string? validationToken) =>
{
    // Microsoft tambien valida este endpoint al crear la subscription.
    if (!string.IsNullOrEmpty(validationToken))
    {
        return Results.Text(validationToken, "text/plain", Encoding.UTF8);
    }

    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync();
    FileLog($"LIFECYCLE POST {raw.Length} bytes");

    ChangeNotificationCollection? payload;
    try { payload = JsonSerializer.Deserialize<ChangeNotificationCollection>(raw); }
    catch (JsonException ex) { FileLog($"LIFECYCLE PARSE FAIL: {ex.Message}"); return Results.BadRequest(); }

    if (payload?.Value is null || payload.Value.Count == 0) { return Results.BadRequest(); }

    // Validar JWT signatures (mismo patron que /webhook/teams - ver descripcion alla).
    using (var scopeJwt = scopeFactory.CreateScope())
    {
        var jwtValidator = scopeJwt.ServiceProvider.GetRequiredService<IJwtNotificationValidator>();
        var securityOpts = scopeJwt.ServiceProvider.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var tokens = payload.ValidationTokens ?? new List<string>();
        var jwtResult = await jwtValidator.ValidateTokensAsync(tokens);
        if (!jwtResult.IsValid)
        {
            Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Jwt-Lifecycle", $"INVALID: {jwtResult.FailureReason} (shadow={(securityOpts.RejectInvalidJwts ? "REJECT" : "LOG-ONLY")})");
            // Solo alertar cuando hay tokens PRESENTES pero con firma/claims invalidos
            // (eso si es intento de impersonacion real). Cuando los tokens vienen vacios/ausentes
            // suele ser Microsoft Graph retry o un scan automatizado contra la URL publica -
            // descartamos con 401 pero NO alertamos para evitar ruido al chat de alertas.
            if (tokens.Count > 0)
            {
                scopeJwt.ServiceProvider.GetRequiredService<IAlertManager>()
                    .Record(AlertCategory.JwtInvalid, $"lifecycle: {jwtResult.FailureReason}");
            }
            if (securityOpts.RejectInvalidJwts) { return Results.Unauthorized(); }
        }
        else
        {
            Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Jwt-Lifecycle", $"OK ({(payload.ValidationTokens?.Count ?? 0)} tokens)");
        }
    }

    var expected = webhookOptions.Value.ClientState;
    var valid = payload.Value
        .Where(n => string.IsNullOrEmpty(expected) || n.ClientState == expected)
        .ToList();

    foreach (var notification in valid)
    {
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ILifecycleHandler>();
            try { await handler.HandleAsync(notification); }
            catch (Exception ex) { FileLog($"LIFECYCLE HANDLE FAIL: {ex}"); }
        });
    }

    return Results.Accepted();
});

// =====================================================================
// Endpoints REST /api/context y /api/sessions — acceso cross-PC a la DB
// PhipesAssistant para skills /contexto y /cerrar-sesion. Auth via header
// X-Api-Key contra PhipesAssistantOptions.ApiKey (fail-closed si vacio).
// =====================================================================

// Helper local de auth: chequea X-Api-Key contra config. Devuelve null si OK,
// IResult con 401 si falla.
static IResult? CheckApiKey(HttpRequest req, IOptions<PhipesAssistantOptions> opts)
{
    var expected = opts.Value.ApiKey;
    if (string.IsNullOrEmpty(expected))
    {
        FileLog("API auth FAIL: ApiKey no configurada (fail-closed)");
        return Results.Unauthorized();
    }
    if (!req.Headers.TryGetValue("X-Api-Key", out var got) || got != expected)
    {
        FileLog($"API auth FAIL: X-Api-Key invalida o ausente desde {req.HttpContext.Connection.RemoteIpAddress}");
        return Results.Unauthorized();
    }
    return null;
}

// GET /api/context/{slug} - lee ProjectContext del slug
app.MapGet("/api/context/{slug}", async (
    string slug,
    HttpRequest req,
    IOptions<PhipesAssistantOptions> opts,
    IProjectContextService svc,
    CancellationToken ct) =>
{
    var authFail = CheckApiKey(req, opts);
    if (authFail is not null) return authFail;

    var ctx = await svc.GetAsync(slug, ct);
    if (ctx is null) return Results.NotFound(new { error = "ProjectContext no encontrado", slug });
    return Results.Ok(ctx);
});

// POST /api/context/{slug} - UPSERT del ProjectContext
app.MapPost("/api/context/{slug}", async (
    string slug,
    HttpRequest req,
    IOptions<PhipesAssistantOptions> opts,
    IProjectContextService svc,
    [FromBody] ProjectContextUpsertDto dto,
    CancellationToken ct) =>
{
    var authFail = CheckApiKey(req, opts);
    if (authFail is not null) return authFail;

    await svc.UpsertAsync(slug, dto, ct);
    var ctx = await svc.GetAsync(slug, ct);
    return Results.Ok(ctx);
});

// POST /api/sessions - cierra una sesion y la persiste
app.MapPost("/api/sessions", async (
    HttpRequest req,
    IOptions<PhipesAssistantOptions> opts,
    IProjectContextService svc,
    [FromBody] SessionCloseDto dto,
    CancellationToken ct) =>
{
    var authFail = CheckApiKey(req, opts);
    if (authFail is not null) return authFail;

    try
    {
        if (dto is null) { FileLog("POST sessions FAIL: dto null"); return Results.BadRequest(new { error = "body vacio o no parseable" }); }
        FileLog($"POST sessions IN: host={dto.HostName} project={dto.Project} summary.Length={dto.Summary?.Length}");
        var sessionId = await svc.CloseSessionAsync(dto, ct);
        FileLog($"POST sessions OK: session_id={sessionId}");
        return Results.Ok(new { session_id = sessionId, host = dto.HostName, project = dto.Project });
    }
    catch (Exception ex)
    {
        FileLog($"POST sessions EXC: {ex.GetType().Name}: {ex.Message}{System.Environment.NewLine}{ex.StackTrace}");
        return Results.Problem(detail: ex.Message, statusCode: 500, title: ex.GetType().Name);
    }
});

// =====================================================================
// GET /api/m365-token — relay owner-gated del access token de Graph para la
// CONSOLA (Sarah-fhernandez en casa/oficina). El refresh token NUNCA sale del
// servidor: lo rota el TokenBroker; aca solo devolvemos un access token corto
// que obtenemos via IGraphTokenProvider (que en produccion habla con el broker).
// Esto hace al broker el UNICO rotador del RT -> la carrera del 2026-05-19 es
// estructuralmente imposible. Auth: X-Api-Key dedicada + allowlist de IP LAN.
// Fail-closed: si M365Relay:ApiKey esta vacio, rechaza todo.
// =====================================================================
app.MapGet("/api/m365-token", async (
    HttpRequest req,
    IOptions<M365RelayOptions> relayOpts,
    IGraphTokenProvider tokenProvider,
    CancellationToken ct) =>
{
    var o = relayOpts.Value;
    var remoteIp = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

    if (string.IsNullOrEmpty(o.ApiKey))
    {
        FileLog("M365Relay FAIL: ApiKey no configurada (fail-closed)");
        return Results.Unauthorized();
    }

    if (o.AllowedSourcePrefixes.Length > 0 &&
        !o.AllowedSourcePrefixes.Any(p => remoteIp.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    {
        FileLog($"M365Relay FAIL: origen {remoteIp} fuera de allowlist");
        return Results.Unauthorized();
    }

    var got = req.Headers.TryGetValue("X-Api-Key", out var hdr) ? hdr.ToString() : "";
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(o.ApiKey)))
    {
        FileLog($"M365Relay FAIL: X-Api-Key invalida o ausente desde {remoteIp}");
        return Results.Unauthorized();
    }

    try
    {
        var at = await tokenProvider.GetAccessTokenAsync(ct);
        FileLog($"M365Relay OK: AT entregado a {remoteIp}");
        return Results.Json(new { access_token = at });
    }
    catch (Exception ex)
    {
        FileLog($"M365Relay EXC: {ex.Message}");
        return Results.Json(new { error = ex.Message }, statusCode: 503);
    }
});

app.Run();

// Log a archivo de respaldo: in-process hosting no flushea ILogger a stdout, asi que
// duplicamos los eventos criticos a un archivo diario (path configurado via env var
// HANDLER_LOG_DIR; ver Utilities/FileLogger.cs).
static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Top", message);

// Marker partial para que AddUserSecrets<Program>() resuelva el ensamblado.
public partial class Program { }
