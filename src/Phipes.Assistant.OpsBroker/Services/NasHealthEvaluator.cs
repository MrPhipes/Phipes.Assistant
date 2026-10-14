using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Phipes.Assistant.OpsBroker.Configuration;

namespace Phipes.Assistant.OpsBroker.Services;

// Reglas que convierten el estado del NAS en hallazgos. Puro (sin E/S) para poder probarlo.
//   critical    → avisar a Felipe de inmediato (riesgo de perder datos o servicio caido).
//   warning     → avisar de inmediato, pero no es emergencia.
//   maintenance → no se avisa al momento; va al resumen semanal (versiones, apps, uptime).
public static class NasHealthEvaluator
{
    public static IEnumerable<NasFinding> Evaluate(NasHealth h, OpsBrokerOptions o, DateTimeOffset? now = null)
    {
        var at = now ?? h.CollectedUtc;

        foreach (var p in h.Pools)
        {
            if (!string.Equals(p.Status, "ONLINE", StringComparison.OrdinalIgnoreCase) || !p.Healthy)
                yield return new($"pool:{p.Name}:status", "critical",
                    $"El pool {p.Name} está {p.Status}{(p.Healthy ? "" : " y no saludable")}.");

            if (p.SizeBytes > 0 && p.AllocatedBytes is not null)
            {
                var pct = (int)(p.AllocatedBytes.Value * 100 / p.SizeBytes.Value);
                if (pct >= o.NasPoolUsageCriticalPercent)
                    yield return new($"pool:{p.Name}:usage", "critical", $"El pool {p.Name} está al {pct} % de uso.");
                else if (pct >= o.NasPoolUsageWarningPercent)
                    yield return new($"pool:{p.Name}:usage", "warning", $"El pool {p.Name} está al {pct} % de uso.");
            }

            if (p.ScanErrors > 0)
                yield return new($"pool:{p.Name}:scrub-errors", "critical",
                    $"El último {p.ScanFunction?.ToLowerInvariant() ?? "scrub"} del pool {p.Name} encontró {p.ScanErrors} errores.");

            var scrubAge = p.ScanEnd is null ? (double?)null : (at - p.ScanEnd.Value).TotalDays;
            if (scrubAge is null || scrubAge > o.NasScrubMaxAgeDays)
                yield return new($"pool:{p.Name}:scrub-age", "warning",
                    scrubAge is null
                        ? $"No hay registro de scrub del pool {p.Name}."
                        : $"El último scrub del pool {p.Name} fue hace {(int)scrubAge} días.");
        }

        foreach (var a in h.Apps)
        {
            if (o.NasIgnoredApps.Contains(a.Name, StringComparer.OrdinalIgnoreCase)) continue;

            if (string.Equals(a.State, "CRASHED", StringComparison.OrdinalIgnoreCase))
                yield return new($"app:{a.Name}:state", "critical", $"La app {a.Name} se cayó (CRASHED).");
            else if (string.Equals(a.State, "STOPPED", StringComparison.OrdinalIgnoreCase))
                yield return new($"app:{a.Name}:state", "warning", $"La app {a.Name} está detenida.");

            if (a.UpgradeAvailable)
                yield return new($"app:{a.Name}:upgrade", "maintenance", $"Hay actualización para la app {a.Name}.");
        }

        foreach (var t in h.SnapshotTasks.Where(t => t.Enabled))
        {
            if (string.Equals(t.State, "ERROR", StringComparison.OrdinalIgnoreCase))
                yield return new($"snapshot:{t.Dataset}:error", "critical",
                    $"Falló la tarea de snapshots de {t.Dataset}: {t.Error ?? "sin detalle"}.");
            else if (t.Daily && t.LastRun is not null && (at - t.LastRun.Value).TotalHours > o.NasSnapshotMaxAgeHours)
                yield return new($"snapshot:{t.Dataset}:age", "warning",
                    $"El snapshot diario de {t.Dataset} no corre hace {(int)(at - t.LastRun.Value).TotalHours} h.");
        }

        foreach (var s in h.Services.Where(s => s.Enabled))
        {
            if (!string.Equals(s.State, "RUNNING", StringComparison.OrdinalIgnoreCase))
                yield return new($"service:{s.Name}:state", "critical",
                    $"El servicio {ServiceLabel(s.Name)} del NAS está {s.State}.");
        }

        foreach (var iqn in o.NasExpectedIscsiInitiators)
        {
            if (!h.IscsiInitiators.Contains(iqn, StringComparer.OrdinalIgnoreCase))
                yield return new($"iscsi:{iqn}:session", "warning",
                    $"No hay sesión iSCSI de {ShortIqn(iqn)} (disco de respaldos).");
        }

        foreach (var a in h.Alerts)
        {
            // Ya cubiertas por los hallazgos propios (app:*:upgrade, system:update).
            if (a.Klass is "AppUpdate" or "HasUpdate") continue;

            if (a.Klass == "RESTAPIUsage")
            {
                var ips = Regex.Matches(a.Text, @"\b\d{1,3}(?:\.\d{1,3}){3}\b").Select(m => m.Value).Distinct();
                yield return new("alert:RESTAPIUsage", "warning",
                    $"Se usó la API REST del NAS, que se elimina en TrueNAS 26.04, desde: {string.Join(", ", ips)}. " +
                    "Hay que migrar esas integraciones a JSON-RPC antes de actualizar.");
                continue;
            }

            var severity = a.Level.ToUpperInvariant() switch
            {
                "CRITICAL" or "ALERT" or "EMERGENCY" or "ERROR" => "critical",
                "WARNING" => "warning",
                _ => "maintenance",
            };
            yield return new($"alert:{a.Klass}:{ShortHash(a.Id)}", severity, $"Alerta del NAS ({a.Level}): {PlainText(a.Text)}");
        }

        if (!string.IsNullOrEmpty(h.UpdateAvailable))
            yield return new("system:update", "maintenance",
                $"Hay actualización de TrueNAS: {h.Version ?? "?"} → {h.UpdateAvailable}.");

        if (h.UptimeDays >= o.NasUptimeMaintenanceDays)
            yield return new("system:uptime", "maintenance", $"El NAS lleva {h.UptimeDays} días sin reiniciar.");
    }

    private static string ServiceLabel(string name) => name switch
    {
        "cifs" => "SMB",
        "iscsitarget" => "iSCSI",
        _ => name,
    };

    // iqn.1991-05.com.microsoft:srv-core.skynet.local → srv-core
    private static string ShortIqn(string iqn)
    {
        var host = iqn.Contains(':') ? iqn[(iqn.LastIndexOf(':') + 1)..] : iqn;
        return host.Split('.')[0];
    }

    // La "key" de una alerta de TrueNAS puede ser un JSON largo: se acorta a un hash estable.
    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    // El texto "formatted" de las alertas trae HTML (<br>, <a>): se deja en texto plano para Teams.
    private static string PlainText(string html)
    {
        var text = Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
    }
}
