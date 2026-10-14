using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phipes.Assistant.OpsBroker.Configuration;

namespace Phipes.Assistant.OpsBroker.Services;

// Cliente JSON-RPC 2.0 sobre WebSocket para TrueNAS (wss://<nas>/api/current), la API que
// reemplaza a la REST /api/v2.0 (eliminada en 26.04). Una conexion por consulta: el broker
// consulta pocas veces (monitor cada ~15 min + preguntas de Felipe), no vale la pena
// mantener la conexion viva ni reconectar.
//
// Certificado: el NAS usa un cert self-signed. En vez de aceptar cualquier cert, se fija
// la huella SHA-256 (OpsBroker:NasCertThumbprint). Sin huella configurada se exige cadena
// valida. El token (OpsBroker:NasApiToken) nunca sale de esta clase.
public sealed class TrueNasRpcClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _nextId;

    private TrueNasRpcClient() { }

    public static async Task<TrueNasRpcClient> ConnectAsync(OpsBrokerOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.NasBaseUrl) || string.IsNullOrWhiteSpace(o.NasApiToken))
            throw new InvalidOperationException("NAS no configurado (NasBaseUrl / NasApiToken)");

        var client = new TrueNasRpcClient();
        var pin = NormalizeThumbprint(o.NasCertThumbprint);
        client._ws.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            pin is null
                ? errors == SslPolicyErrors.None
                : cert is not null && string.Equals(Sha256Thumbprint(cert), pin, StringComparison.Ordinal);

        var uri = new UriBuilder(o.NasBaseUrl.TrimEnd('/') + "/api/current")
        {
            Scheme = "wss",
            Port = -1,
        }.Uri;

        try
        {
            await client._ws.ConnectAsync(uri, ct);
            var ok = await client.CallAsync("auth.login_with_api_key", ct, o.NasApiToken);
            if (ok?.GetValueKind() != JsonValueKind.True)
                throw new InvalidOperationException("el NAS rechazo la llave API");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    // Llama un metodo y devuelve "result". Un error JSON-RPC se lanza como TrueNasRpcException
    // (con el codigo, para distinguir "metodo no existe" de una falla real).
    public async Task<JsonNode?> CallAsync(string method, CancellationToken ct, params object?[] args)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = args,
        });
        await _ws.SendAsync(request, WebSocketMessageType.Text, endOfMessage: true, ct);

        // El NAS puede intercalar notificaciones (sin id): se descartan hasta llegar a la respuesta.
        while (true)
        {
            var message = await ReceiveAsync(ct);
            if (message["id"]?.GetValue<int>() != id) continue;

            if (message["error"] is JsonObject error)
            {
                var code = error["code"]?.GetValue<int>() ?? 0;
                var text = error["data"]?["reason"]?.GetValue<string>()
                    ?? error["message"]?.GetValue<string>()
                    ?? "error desconocido";
                throw new TrueNasRpcException(method, code, text);
            }
            return message["result"];
        }
    }

    private async Task<JsonNode> ReceiveAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        ValueWebSocketReceiveResult r;
        do
        {
            r = await _ws.ReceiveAsync(chunk.AsMemory(), ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("el NAS cerro la conexion");
            buffer.Write(chunk, 0, r.Count);
        } while (!r.EndOfMessage);

        return JsonNode.Parse(buffer.ToArray()) ?? throw new InvalidOperationException("respuesta vacia del NAS");
    }

    private static string Sha256Thumbprint(X509Certificate cert) =>
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    private static string? NormalizeThumbprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            if (Uri.IsHexDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
        }
        catch
        {
            // Cierre best-effort: si el NAS ya corto, no hay nada que hacer.
        }
        _ws.Dispose();
    }
}

public sealed class TrueNasRpcException(string method, int code, string message)
    : Exception($"{method}: {message}")
{
    public string Method { get; } = method;
    public int Code { get; } = code;
}
