using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Phipes.Assistant.OpsBroker.Configuration;

namespace Phipes.Assistant.OpsBroker.Services;

// Lee infraestructura desde fuentes que solo svc-sarah-ops puede tocar: el archivo
// .context\infrastructure.md y el grafo de Felipe (todos los rings). Nunca devuelve
// secretos — credentials.md / secrets-paths.md quedan FUERA a propósito.
public sealed class InfraReader
{
    private readonly OpsBrokerOptions _o;

    public InfraReader(IOptions<OpsBrokerOptions> options) => _o = options.Value;

    // Devuelve el contenido completo del infrastructure.md (topología, hosts, servicios).
    public async Task<string> ReadInfrastructureAsync(CancellationToken ct)
    {
        if (!File.Exists(_o.InfrastructureFilePath))
            throw new FileNotFoundException($"no existe {_o.InfrastructureFilePath}");
        return await File.ReadAllTextAsync(_o.InfrastructureFilePath, ct);
    }

    // Vecinos de una entidad en el grafo de infra (read-only). Útil para "¿qué corre en
    // srv-pacificdev?" o "¿con qué se relaciona el UDM?".
    public async Task<List<GraphNeighbor>> QueryGraphAsync(string entity, CancellationToken ct)
    {
        var result = new List<GraphNeighbor>();
        if (string.IsNullOrWhiteSpace(_o.GraphConnectionString)) return result;

        await using var cn = new SqlConnection(_o.GraphConnectionString);
        await cn.OpenAsync(ct);
        // Vecindad bidireccional por nombre de entidad. Solo lectura.
        const string sql = @"
SELECT n2.name AS neighbor, n2.node_type AS ntype, e.edge_type, e.ring, e.weight
FROM graph.Nodes n1, graph.Edges e, graph.Nodes n2
WHERE MATCH(n1-(e)->n2) AND n1.name = @e
UNION ALL
SELECT n1.name AS neighbor, n1.node_type AS ntype, e.edge_type, e.ring, e.weight
FROM graph.Nodes n1, graph.Edges e, graph.Nodes n2
WHERE MATCH(n1-(e)->n2) AND n2.name = @e
ORDER BY weight DESC";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@e", entity);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new GraphNeighbor(
                rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                rd.IsDBNull(4) ? 0 : rd.GetDouble(4)));
        }
        return result;
    }
}

public sealed record GraphNeighbor(string Name, string NodeType, string EdgeType, string Ring, double Weight);
