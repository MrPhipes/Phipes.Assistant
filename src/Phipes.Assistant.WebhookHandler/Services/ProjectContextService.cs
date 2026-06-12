using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;

namespace Phipes.Assistant.WebhookHandler.Services;

// Acceso a las tablas dbo.ProjectContext y dbo.Sessions de la DB PhipesAssistant.
// Envuelve los SPs dbo.UpsertProjectContext y dbo.CloseSession para uso de los
// endpoints REST /api/context y /api/sessions.
//
// Nota: las skills /contexto, /escarbar, /cerrar-sesion siguen conectando
// directo a SQL via Integrated Security desde PowerShell. Estos endpoints
// REST estan para acceso CROSS-PC (oficina, notebook) donde una skill no
// tiene SQL directo pero si HTTPS al WebhookHandler.
public interface IProjectContextService
{
    Task<ProjectContextDto?> GetAsync(string slug, CancellationToken ct = default);
    Task UpsertAsync(string slug, ProjectContextUpsertDto dto, CancellationToken ct = default);
    Task<long> CloseSessionAsync(SessionCloseDto dto, CancellationToken ct = default);
}

public sealed record ProjectContextDto(
    string Slug,
    string? Stack,
    string? Conventions,
    string? Decisions,
    string? OpenThreads,
    DateTime LastUpdated,
    string Ring);

public sealed record ProjectContextUpsertDto(
    string? Stack,
    string? Conventions,
    string? Decisions,
    string? OpenThreads,
    string? Ring);

public sealed record SessionCloseDto(
    string HostName,
    string Project,
    string Summary,
    string? OpenThreads,
    string? LinkedMemories,
    string? Ring);

public sealed class ProjectContextService : IProjectContextService
{
    private readonly PhipesAssistantOptions _options;

    public ProjectContextService(IOptions<PhipesAssistantOptions> options)
    {
        _options = options.Value;
    }

    public async Task<ProjectContextDto?> GetAsync(string slug, CancellationToken ct = default)
    {
        using var cn = new SqlConnection(_options.ConnectionString);
        await cn.OpenAsync(ct);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT slug, stack, conventions, decisions, open_threads, last_updated, ring
            FROM dbo.ProjectContext WHERE slug = @slug;";
        cmd.Parameters.Add(new SqlParameter("@slug", slug));

        using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;

        return new ProjectContextDto(
            Slug: rdr.GetString(0),
            Stack: rdr.IsDBNull(1) ? null : rdr.GetString(1),
            Conventions: rdr.IsDBNull(2) ? null : rdr.GetString(2),
            Decisions: rdr.IsDBNull(3) ? null : rdr.GetString(3),
            OpenThreads: rdr.IsDBNull(4) ? null : rdr.GetString(4),
            LastUpdated: rdr.GetDateTime(5),
            Ring: rdr.GetString(6));
    }

    public async Task UpsertAsync(string slug, ProjectContextUpsertDto dto, CancellationToken ct = default)
    {
        using var cn = new SqlConnection(_options.ConnectionString);
        await cn.OpenAsync(ct);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "dbo.UpsertProjectContext";
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.Parameters.Add(new SqlParameter("@slug", slug));
        cmd.Parameters.Add(new SqlParameter("@stack", (object?)dto.Stack ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@conventions", (object?)dto.Conventions ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@decisions", (object?)dto.Decisions ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@open_threads", (object?)dto.OpenThreads ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ring", (object?)dto.Ring ?? "operational"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CloseSessionAsync(SessionCloseDto dto, CancellationToken ct = default)
    {
        using var cn = new SqlConnection(_options.ConnectionString);
        await cn.OpenAsync(ct);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "dbo.CloseSession";
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.Parameters.Add(new SqlParameter("@host", dto.HostName));
        cmd.Parameters.Add(new SqlParameter("@project", dto.Project));
        cmd.Parameters.Add(new SqlParameter("@summary", dto.Summary));
        cmd.Parameters.Add(new SqlParameter("@open_threads", (object?)dto.OpenThreads ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@linked_memories", (object?)dto.LinkedMemories ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ring", (object?)dto.Ring ?? "operational"));
        var idObj = await cmd.ExecuteScalarAsync(ct);
        return idObj is null || idObj is DBNull ? 0L : Convert.ToInt64(idObj);
    }
}
