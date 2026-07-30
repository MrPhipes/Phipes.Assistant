using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Phipes.Assistant.OpsBroker.Configuration;
using Phipes.Assistant.OpsBroker.Utilities;

namespace Phipes.Assistant.OpsBroker.Services;

// Consulta el estado de las apps (Docker / Custom Apps) del NAS TrueNAS via su API REST.
// El token del NAS vive SOLO en la config del broker (User Secrets de svc-sarah-ops) y
// NUNCA sale de aqui: este servicio devuelve unicamente el estado (nombre + state),
// jamas la credencial. Asi Sarah-server puede saber "estan arriba" sin tocar el secreto.
public sealed class NasReader(IOptions<OpsBrokerOptions> options)
{
	private readonly OpsBrokerOptions _o = options.Value;

	// Cliente con bypass del cert self-signed del NAS en la LAN interna (solo este cliente).
	private static readonly HttpClient _http = new(new HttpClientHandler
	{
		ServerCertificateCustomValidationCallback = (_, _, _, _) => true
	})
	{ Timeout = TimeSpan.FromSeconds(20) };

	public async Task<IReadOnlyList<NasApp>> GetAppsAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(_o.NasBaseUrl) || string.IsNullOrWhiteSpace(_o.NasApiToken))
			throw new InvalidOperationException("NAS no configurado (NasBaseUrl / NasApiToken)");

		var url = _o.NasBaseUrl.TrimEnd('/') + "/api/v2.0/app";
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Authorization = new("Bearer", _o.NasApiToken);
		using var resp = await _http.SendAsync(req, ct);
		resp.EnsureSuccessStatusCode();
		var apps = await resp.Content.ReadFromJsonAsync<List<TrueNasApp>>(cancellationToken: ct) ?? [];
		FileLogger.Write("Nas", $"status query OK ({apps.Count} apps)");
		// Solo nombre + estado: nada de puertos, config ni el token.
		return [.. apps.Select(a => new NasApp(a.Name ?? "?", a.State ?? "?"))];
	}

	private sealed class TrueNasApp
	{
		[JsonPropertyName("name")] public string? Name { get; set; }
		[JsonPropertyName("state")] public string? State { get; set; }
	}
}

public sealed record NasApp(string Name, string State);
