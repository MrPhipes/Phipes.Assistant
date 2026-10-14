using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

// Tests de la logica pura del NasMonitor: que avisa (nuevo / agravado / resuelto), que
// calla (sin cambios, maintenance) y cuando toca el resumen semanal.
public sealed class NasFindingsDiffTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, TrackedFinding> Active(params (string Key, string Severity)[] items) =>
        items.ToDictionary(i => i.Key, i => new TrackedFinding(i.Severity, $"msg {i.Key}", Now.AddHours(-1)));

    [Fact]
    public void NewCriticalIsRaised()
    {
        var diff = NasFindingsDiff.Compute(Active(), [new("pool:Tank:status", "critical", "Tank DEGRADED")], Now);

        Assert.Single(diff.Raised);
        Assert.Empty(diff.Resolved);
        Assert.Contains("🔴 Tank DEGRADED", NasFindingsDiff.FormatChanges(diff));
    }

    [Fact]
    public void UnchangedFindingIsSilentAndKeepsFirstSeen()
    {
        var previous = Active(("app:odoo:state", "warning"));
        var diff = NasFindingsDiff.Compute(previous, [new("app:odoo:state", "warning", "odoo detenida")], Now);

        Assert.Empty(diff.Raised);
        Assert.Null(NasFindingsDiff.FormatChanges(diff));
        Assert.Equal(Now.AddHours(-1), diff.Active["app:odoo:state"].FirstSeenUtc);
    }

    [Fact]
    public void EscalationFromWarningToCriticalIsRaised()
    {
        var previous = Active(("pool:Tank:usage", "warning"));
        var diff = NasFindingsDiff.Compute(previous, [new("pool:Tank:usage", "critical", "Tank al 92 %")], Now);

        Assert.Single(diff.Raised);
    }

    [Fact]
    public void DisappearedFindingIsResolved()
    {
        var diff = NasFindingsDiff.Compute(Active(("app:odoo:state", "warning")), [], Now);

        Assert.Single(diff.Resolved);
        Assert.Empty(diff.Active);
        Assert.Contains("🟢 Resuelto: msg app:odoo:state", NasFindingsDiff.FormatChanges(diff));
    }

    [Fact]
    public void MaintenanceIsNotAlerted()
    {
        var diff = NasFindingsDiff.Compute(Active(), [new("system:update", "maintenance", "hay 25.10.7")], Now);

        Assert.Empty(diff.Raised);
        Assert.Empty(diff.Active);
    }

    [Fact]
    public void FindingFromFailedQueryIsNotResolved()
    {
        var previous = Active(("pool:Tank:status", "critical"), ("app:odoo:state", "warning"));
        var unknown = NasFindingsDiff.UnknownPrefixes(["pool.query: timeout"]);

        var diff = NasFindingsDiff.Compute(previous, [], Now, unknown);

        Assert.True(diff.Active.ContainsKey("pool:Tank:status"));
        Assert.Equal("app:odoo:state", Assert.Single(diff.Resolved).Key);
    }

    [Theory]
    // Lunes 09:00 programado. Hoy miercoles 14-10-2026 12:00 (lunes previo = 12-10).
    [InlineData("2026-10-14T12:00:00-03:00", null, true)]
    [InlineData("2026-10-14T12:00:00-03:00", "2026-10-12T09:05:00-03:00", false)]
    [InlineData("2026-10-14T12:00:00-03:00", "2026-10-11T20:00:00-03:00", true)]
    // Lunes 19-10 08:59: el de esta semana aun no toca; el anterior ya se envio.
    [InlineData("2026-10-19T08:59:00-03:00", "2026-10-12T09:05:00-03:00", false)]
    [InlineData("2026-10-19T09:00:00-03:00", "2026-10-12T09:05:00-03:00", true)]
    public void WeeklySummaryDue(string now, string? last, bool expected)
    {
        var due = NasFindingsDiff.IsWeeklySummaryDue(
            DateTimeOffset.Parse(now), last is null ? null : DateTimeOffset.Parse(last), DayOfWeek.Monday, 9);

        Assert.Equal(expected, due);
    }
}
