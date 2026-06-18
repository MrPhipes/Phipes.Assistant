using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Phipes.Assistant.OpsBroker.Configuration;

namespace Phipes.Assistant.OpsBroker.Services;

// Valida el token efímero que el WebhookHandler emite SOLO en turnos del Owner.
// Formato: "{expUnixSeconds}.{base64url(HMACSHA256(key, expUnixSeconds))}".
// El handler usa la MISMA clave para firmar; sin la clave no se puede forjar un token
// válido, así que un token válido prueba "request originado en un turno Owner autenticado".
public sealed class OwnerTokenValidator
{
    private readonly byte[] _key;
    private readonly int _windowSeconds;

    public OwnerTokenValidator(IOptions<OpsBrokerOptions> options)
    {
        var o = options.Value;
        _key = Encoding.UTF8.GetBytes(o.SharedHmacKey);
        _windowSeconds = o.TokenWindowSeconds;
    }

    public bool Validate(string? token, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(token)) { reason = "token ausente"; return false; }

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot >= token.Length - 1) { reason = "formato invalido"; return false; }

        var expPart = token[..dot];
        var sigPart = token[(dot + 1)..];

        if (!long.TryParse(expPart, out var exp)) { reason = "exp no numerico"; return false; }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // exp es el instante de emisión + ventana; rechazamos si ya pasó o si viene del
        // futuro más allá de la ventana (clock skew tolerable).
        if (exp < now) { reason = "token expirado"; return false; }
        if (exp > now + _windowSeconds + 30) { reason = "exp fuera de rango"; return false; }

        var expected = Convert.ToBase64String(
            HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(expPart)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(sigPart), Encoding.UTF8.GetBytes(expected)))
        {
            reason = "firma invalida";
            return false;
        }
        return true;
    }
}
