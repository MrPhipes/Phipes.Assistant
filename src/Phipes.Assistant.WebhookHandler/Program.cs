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

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

builder.Services.AddSingleton<IIdempotencyStore, SqlIdempotencyStore>();
builder.Services.AddHttpClient<IJwtNotificationValidator, JwtNotificationValidator>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<INotificationDecrypter, NotificationDecrypter>();
builder.Services.AddSingleton<IClaudeCodeInvoker, ClaudeCodeInvoker>();
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

// HttpClient dedicado para Microsoft Identity Platform (oauth2/v2.0/token).
builder.Services.AddHttpClient<IGraphTokenProvider, GraphTokenProvider>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});

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

var app = builder.Build();

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
        var jwtResult = await jwtValidator.ValidateTokensAsync(payload.ValidationTokens ?? new List<string>());
        if (!jwtResult.IsValid)
        {
            FileLog($"[Jwt] INVALID validationTokens: {jwtResult.FailureReason} (shadowMode={(securityOpts.RejectInvalidJwts ? "REJECT" : "LOG-ONLY")})");
            log.LogWarning("validationTokens invalidos: {Reason}", jwtResult.FailureReason);
            if (securityOpts.RejectInvalidJwts)
            {
                return Results.Unauthorized();
            }
        }
        else
        {
            FileLog($"[Jwt] OK validationTokens validados ({(payload.ValidationTokens?.Count ?? 0)} tokens)");
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
        var jwtResult = await jwtValidator.ValidateTokensAsync(payload.ValidationTokens ?? new List<string>());
        if (!jwtResult.IsValid)
        {
            FileLog($"[Jwt-Lifecycle] INVALID: {jwtResult.FailureReason} (shadow={(securityOpts.RejectInvalidJwts ? "REJECT" : "LOG-ONLY")})");
            if (securityOpts.RejectInvalidJwts) { return Results.Unauthorized(); }
        }
        else
        {
            FileLog($"[Jwt-Lifecycle] OK ({(payload.ValidationTokens?.Count ?? 0)} tokens)");
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

app.Run();

// Log a archivo de respaldo: in-process hosting no flushea ILogger a stdout, asi que
// duplicamos los eventos criticos a un archivo diario (path configurado via env var
// HANDLER_LOG_DIR; ver Utilities/FileLogger.cs).
static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Top", message);

// Marker partial para que AddUserSecrets<Program>() resuelva el ensamblado.
public partial class Program { }
