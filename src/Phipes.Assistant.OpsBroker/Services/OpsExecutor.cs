using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Phipes.Assistant.OpsBroker.Configuration;
using Phipes.Assistant.OpsBroker.Utilities;

namespace Phipes.Assistant.OpsBroker.Services;

// Ejecuta ops ACOTADAS y whitelisted. Cada acción valida sus parámetros contra la
// config antes de tocar nada. Sin verbo arbitrario: solo lo que está explícitamente
// permitido. Todo se loguea (auditoría).
[SupportedOSPlatform("windows")]
public sealed class OpsExecutor
{
    private readonly OpsBrokerOptions _o;
    public OpsExecutor(IOptions<OpsBrokerOptions> options) => _o = options.Value;

    // start | stop | restart de un servicio en la whitelist.
    public OpResult ServiceAction(string action, string serviceName)
    {
        if (!_o.AllowedServices.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
            return OpResult.Fail($"servicio '{serviceName}' no esta en la whitelist");

        var verb = action.ToLowerInvariant() switch
        {
            "start" => "Start-Service",
            "stop" => "Stop-Service",
            "restart" => "Restart-Service",
            _ => null
        };
        if (verb is null) return OpResult.Fail($"accion '{action}' no soportada (start|stop|restart)");

        var (code, output) = RunPowerShell($"{verb} -Name '{serviceName}' -ErrorAction Stop; 'OK'");
        FileLog($"SERVICE {action} {serviceName} -> exit {code}");
        return code == 0 ? OpResult.Ok($"{action} {serviceName} OK") : OpResult.Fail(output);
    }

    // Crea un sitio IIS desde parámetros validados: physicalPath DEBE estar bajo AllowedSiteRoot.
    public OpResult CreateIisSite(string siteName, string bindings, string physicalPath)
    {
        if (string.IsNullOrWhiteSpace(_o.AllowedSiteRoot))
            return OpResult.Fail("creacion de sitios IIS deshabilitada (AllowedSiteRoot vacio)");
        if (!IsSafeName(siteName))
            return OpResult.Fail("nombre de sitio invalido");
        var full = Path.GetFullPath(physicalPath);
        var root = Path.GetFullPath(_o.AllowedSiteRoot);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail($"physicalPath debe estar bajo {root}");
        if (!IsSafeBindings(bindings))
            return OpResult.Fail("bindings invalidos");

        Directory.CreateDirectory(full);
        var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");
        var (code, output) = RunProcess(appcmd,
            $"add site /name:\"{siteName}\" /bindings:\"{bindings}\" /physicalPath:\"{full}\"");
        FileLog($"IIS-SITE add {siteName} bindings={bindings} path={full} -> exit {code}");
        return code == 0 ? OpResult.Ok($"sitio {siteName} creado") : OpResult.Fail(output);
    }

    private static bool IsSafeName(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 64 &&
        s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    // Acepta formato appcmd "http/*:80:host,https/*:443:host". Sin metacaracteres de shell.
    private static bool IsSafeBindings(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 256 &&
        s.All(c => char.IsLetterOrDigit(c) || c is ':' or '/' or '.' or '-' or ',' or '*');

    private static (int, string) RunPowerShell(string script) =>
        RunProcess("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"");

    private static (int code, string output) RunProcess(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        var output = (stdout + stderr).Trim();
        return (p.HasExited ? p.ExitCode : -1, output.Length > 800 ? output[..800] : output);
    }

    private static void FileLog(string m) => FileLogger.Write("Ops", m);
}

public sealed record OpResult(bool Success, string Message)
{
    public static OpResult Ok(string m) => new(true, m);
    public static OpResult Fail(string m) => new(false, m);
}
