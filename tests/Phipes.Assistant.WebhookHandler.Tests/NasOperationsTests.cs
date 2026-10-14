using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

// Aviso de termino de las mantenciones que lanza Sarah-server: solo terminadas, una sola vez, y el texto.
public sealed class NasOperationsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 20, 45, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyFinishedAndNotReportedAreReturned()
    {
        var ops = new[]
        {
            new NasOperationDto(1, "actualización de TrueNAS a 25.10.7", "success", Start, Start.AddMinutes(4), null),
            new NasOperationDto(2, "actualización de la app odoo", "running", Start, null, null),
            new NasOperationDto(3, "actualización de la app twenty", "failed", Start, Start.AddMinutes(2), "pull falló"),
        };

        var finished = NasFindingsDiff.FinishedOperations(ops, new HashSet<long> { 3 });

        Assert.Equal(1, Assert.Single(finished).JobId);
    }

    [Fact]
    public void SuccessMessageIncludesDuration()
    {
        var msg = NasFindingsDiff.FormatOperation(
            new NasOperationDto(1, "actualización de TrueNAS a 25.10.7", "success", Start, Start.AddMinutes(4), null));

        Assert.Equal("✅ NAS: terminó la actualización de TrueNAS a 25.10.7 (4 min). Todo en orden.", msg);
    }

    [Fact]
    public void FailureMessageIncludesEscapedError()
    {
        var msg = NasFindingsDiff.FormatOperation(
            new NasOperationDto(3, "actualización de la app twenty", "failed", Start, Start.AddMinutes(2), "imagen <latest> no existe"));

        Assert.Contains("❌ NAS: falló la actualización de la app twenty (2 min): imagen &lt;latest&gt; no existe", msg);
    }
}
