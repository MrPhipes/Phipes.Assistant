using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Phipes.Assistant.WebhookHandler.Utilities;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

// Receptor del contrato webhook v1 de InfraMonitor: firma (HMAC "{ts}.{cuerpo}", ventana 300 s)
// y formato del mensaje para Teams.
public sealed class InfraMonitorWebhookTests
{
    private const string Secret = "secreto-de-prueba";
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 19, 30, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"version\":\"1\",\"event\":\"test\"}");

    private static string Sign(string secret, string ts, byte[] body) =>
        "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + ".").Concat(body).ToArray()));

    [Fact]
    public void ValidSignatureIsAccepted()
    {
        var ts = Now.ToUnixTimeSeconds().ToString();
        Assert.True(InfraMonitorWebhook.VerifySignature(Secret, ts, Body, Sign(Secret, ts, Body), Now));
    }

    [Fact]
    public void WrongSecretOrTamperedBodyIsRejected()
    {
        var ts = Now.ToUnixTimeSeconds().ToString();
        Assert.False(InfraMonitorWebhook.VerifySignature(Secret, ts, Body, Sign("otro", ts, Body), Now));
        Assert.False(InfraMonitorWebhook.VerifySignature(Secret, ts, Encoding.UTF8.GetBytes("{}"), Sign(Secret, ts, Body), Now));
    }

    [Fact]
    public void StaleTimestampIsRejected()
    {
        var ts = Now.AddSeconds(-301).ToUnixTimeSeconds().ToString();
        Assert.False(InfraMonitorWebhook.VerifySignature(Secret, ts, Body, Sign(Secret, ts, Body), Now));
    }

    [Fact]
    public void EmptySecretRejectsEverything()
    {
        var ts = Now.ToUnixTimeSeconds().ToString();
        Assert.False(InfraMonitorWebhook.VerifySignature("", ts, Body, Sign("", ts, Body), Now));
    }

    [Fact]
    public void FormatsTitleTextAndLinkAsEscapedHtml()
    {
        using var doc = JsonDocument.Parse("""
            {"title":"🔴 Crítico en SRV-CORE: Respaldos","text":"• Sin respaldo <26 h>\n\nDesde 15-10",
             "url":"https://inframonitor.skynet.local/Servers/1","alert":{"level":"critical"},"campoNuevo":1}
            """);

        var html = InfraMonitorWebhook.FormatForTeams(doc.RootElement);

        Assert.Contains("<b>🔴 Crítico en SRV-CORE: Respaldos</b>", html);
        Assert.Contains("• Sin respaldo &lt;26 h&gt;<br><br>Desde 15-10", html);
        Assert.Contains("href=\"https://inframonitor.skynet.local/Servers/1\"", html);
    }

    [Fact]
    public void NonHttpUrlIsNotLinked()
    {
        using var doc = JsonDocument.Parse("""{"title":"x","url":"javascript:alert(1)"}""");
        Assert.DoesNotContain("href", InfraMonitorWebhook.FormatForTeams(doc.RootElement));
    }
}
