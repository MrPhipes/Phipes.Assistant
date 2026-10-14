using System.Security.Cryptography;
using System.Text;

namespace Phipes.Assistant.WebhookHandler.Utilities;

// Firma un token efimero para el Ops Broker: "{expUnix}.{base64url(HMACSHA256(key, expUnix))}".
// El broker recomputa la firma con la misma clave y valida la expiracion. Ventana corta
// (~3 min): suficiente para el turno, minimiza replay (loopback only de por si).
// Lo usan los turnos del Owner (ClaudeCodeInvoker) y el NasMonitor, que consulta el broker
// en nombre de Felipe para avisarle; ningun otro proceso tiene la clave.
internal static class OpsTokenMinter
{
    public static string Mint(string hmacKey)
    {
        var exp = DateTimeOffset.UtcNow.AddSeconds(180).ToUnixTimeSeconds().ToString();
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(hmacKey), Encoding.UTF8.GetBytes(exp));
        var sig64 = Convert.ToBase64String(sig).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{exp}.{sig64}";
    }
}
