using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

// Invoca claude.exe --print contra el plan Max via OAuth long-lived token. Cada chat
// mantiene su propia conversation continuity via session-id deterministico desde chatId.
public interface IClaudeCodeInvoker
{
    Task<string> AskAsync(string chatId, string userPrompt, CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed class ClaudeCodeInvoker : IClaudeCodeInvoker
{
    private readonly ClaudeOptions _options;
    private readonly IAlertManager _alerts;
    private readonly ILogger<ClaudeCodeInvoker> _logger;
    private readonly Lazy<string> _oauthToken;

    // Semaforos por session-id. Serializa invocaciones a claude.exe sobre la misma sesion
    // para que dos mensajes Teams seguidos del mismo chat NO produzcan dos procesos Claude
    // paralelos (caso de duplicados: cada Claude decide independientemente "envio correo").
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public ClaudeCodeInvoker(IOptions<ClaudeOptions> options, IAlertManager alerts, ILogger<ClaudeCodeInvoker> logger)
    {
        _options = options.Value;
        _alerts = alerts;
        _logger = logger;
        _oauthToken = new Lazy<string>(() => PsCredentialReader.ReadPassword(_options.OAuthTokenPath));
    }

    public async Task<string> AskAsync(string chatId, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_options.ExePath))
        {
            throw new FileNotFoundException($"claude.exe no encontrado en {_options.ExePath}");
        }

        var sessionId = DeriveSessionId(chatId);

        // Serializar invocaciones contra la misma sesion. Si llegan 2 mensajes Teams al
        // mismo chat dentro de pocos segundos, esperamos a que el primero termine antes
        // de empezar el segundo. Sin esto, dos claude.exe paralelos sobre la misma session
        // interpretan independientemente y producen acciones duplicadas (caso real: 2 correos).
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        var lockAcquireStart = DateTime.UtcNow;
        await sessionLock.WaitAsync(cancellationToken);
        var lockWaitMs = (DateTime.UtcNow - lockAcquireStart).TotalMilliseconds;
        if (lockWaitMs > 100)
        {
            FileLog($"SERIALIZE chat={chatId} session={sessionId} esperando={lockWaitMs:0}ms (otro turno en curso)");
        }
        try
        {
            return await AskAsyncInternal(chatId, sessionId, userPrompt, cancellationToken);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private async Task<string> AskAsyncInternal(string chatId, string sessionId, string userPrompt, CancellationToken cancellationToken)
    {
        // Archivo flag: si existe, esta sesion ya fue creada en alguna iteracion anterior
        // y debemos usar --resume en vez de --session-id (que tira "already in use").
        // El path se configura via env var HANDLER_SESSIONS_DIR (fallback a TempPath).
        var flagDir = Environment.GetEnvironmentVariable("HANDLER_SESSIONS_DIR")
                      ?? Path.Combine(Path.GetTempPath(), "webhook-handler", "sessions");
        Directory.CreateDirectory(flagDir);
        var flagFile = Path.Combine(flagDir, $"{sessionId}.touch");
        var isResume = File.Exists(flagFile);

        FileLog($"INVOKE chat={chatId} session={sessionId} {(isResume ? "RESUME" : "NEW")} promptLen={userPrompt.Length}");

        var args = new List<string> { "--print" };
        if (isResume)
        {
            args.Add("--resume"); args.Add(sessionId);
        }
        else
        {
            args.Add("--session-id"); args.Add(sessionId);
        }
        args.Add("--output-format"); args.Add("json");
        args.Add("--max-budget-usd"); args.Add(_options.MaxBudgetUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

        // Autoriza explicitamente la carpeta donde el MessageAttachmentExtractor deja los
        // archivos descargados, asi claude.exe puede leerlos sin pedir aprobacion interactiva
        // (que en modo --print fallaria silenciosamente). Sin esto, Read sobre la carpeta
        // queda fuera del trust boundary y Claude responde "no tengo permiso para ese path".
        var attachmentsRoot = Environment.GetEnvironmentVariable("HANDLER_ATTACHMENTS_DIR")
                              ?? Path.Combine(Path.GetTempPath(), "webhook-handler", "attachments");
        args.Add("--add-dir"); args.Add(attachmentsRoot);
        args.Add("--permission-mode"); args.Add("acceptEdits");
        if (!string.IsNullOrWhiteSpace(_options.AppendSystemPrompt))
        {
            args.Add("--append-system-prompt");
            args.Add(_options.AppendSystemPrompt);
        }
        args.Add(userPrompt);

        var psi = new ProcessStartInfo
        {
            FileName = _options.ExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = _oauthToken.Value;
        // Forzamos USERPROFILE/HOME al perfil de la asistente (centralizado en
        // ClaudeOptions.ClaudeHome), no al de la cuenta tecnica del app pool. Asi todas
        // las instancias de la asistente leen del mismo lugar: CLAUDE.md identidad,
        // memory/, skills/, sessions/, etc.
        psi.Environment["USERPROFILE"] = _options.ClaudeHome;
        psi.Environment["HOME"] = _options.ClaudeHome;

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        var sw = Stopwatch.StartNew();
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            FileLog($"TIMEOUT chat={chatId} despues de {_options.TimeoutSeconds}s");
            _alerts.Record(AlertCategory.ClaudeTimeouts, $"chat={chatId} timeout={_options.TimeoutSeconds}s");
            throw new TimeoutException($"claude.exe excedio timeout de {_options.TimeoutSeconds}s");
        }
        sw.Stop();

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        if (process.ExitCode != 0)
        {
            FileLog($"CLAUDE EXIT {process.ExitCode} stderr={Truncate(stderr, 500)}");
            throw new InvalidOperationException($"claude.exe exit {process.ExitCode}: {Truncate(stderr, 500)}");
        }

        ClaudeResult? parsed;
        try { parsed = JsonSerializer.Deserialize<ClaudeResult>(stdout); }
        catch (JsonException ex)
        {
            FileLog($"JSON PARSE FAIL: {ex.Message} stdoutHead={Truncate(stdout, 300)}");
            throw new InvalidOperationException("claude.exe devolvio JSON no parseable", ex);
        }

        // Caso A: parsed null o is_error=true → es API error real. Record + throw.
        if (parsed is null || parsed.IsError)
        {
            FileLog($"CLAUDE ERROR is_error={parsed?.IsError} apiErr={parsed?.ApiErrorStatus} resultLen={parsed?.Result?.Length}");
            _alerts.Record(AlertCategory.ClaudeApiErrors, $"chat={chatId} apiErr={parsed?.ApiErrorStatus ?? "n/a"}");
            throw new InvalidOperationException($"claude.exe reporto error: {parsed?.ApiErrorStatus ?? "(sin detalle)"}");
        }

        // Crear flag file solo si esta sesion fue exitosa - asi proximos turnos usan --resume
        try { File.WriteAllText(flagFile, DateTime.UtcNow.ToString("o")); } catch { }

        // Caso B: is_error=false pero Result vacio. Es una decision LEGITIMA del LLM de no
        // responder (ej. "no conversar en chat de alertas"). NO es error, NO record alerta,
        // NO throw. Retornamos "" y el caller decide no postear.
        if (string.IsNullOrWhiteSpace(parsed.Result))
        {
            FileLog($"VOID chat={chatId} session={sessionId} elapsed={sw.Elapsed.TotalSeconds:0.0}s cost=${parsed.TotalCostUsd:0.0000} - LLM decidio no responder");
            return string.Empty;
        }

        FileLog($"OK chat={chatId} session={sessionId} elapsed={sw.Elapsed.TotalSeconds:0.0}s cost=${parsed.TotalCostUsd:0.0000} resultLen={parsed.Result.Length}");
        return parsed.Result.Trim();
    }

    // Convierte chatId (texto arbitrario) en un UUID v4 estable via SHA1, para que cada chat
    // tenga su propia conversation continuity en Claude Code.
    private static string DeriveSessionId(string chatId)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(chatId));
        // Tomamos primeros 16 bytes y los formateamos como UUID v4
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        // Setear bits de version (4) y variant (RFC 4122)
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

        private static void FileLog(string message) => Phipes.Assistant.WebhookHandler.Utilities.FileLogger.Write("Claude", message);

    private sealed class ClaudeResult
    {
        [JsonPropertyName("type")]            public string? Type { get; set; }
        [JsonPropertyName("subtype")]         public string? Subtype { get; set; }
        [JsonPropertyName("is_error")]        public bool IsError { get; set; }
        [JsonPropertyName("api_error_status")] public string? ApiErrorStatus { get; set; }
        [JsonPropertyName("result")]          public string? Result { get; set; }
        [JsonPropertyName("session_id")]      public string? SessionId { get; set; }
        [JsonPropertyName("total_cost_usd")]  public double TotalCostUsd { get; set; }
        [JsonPropertyName("num_turns")]       public int NumTurns { get; set; }
    }
}
