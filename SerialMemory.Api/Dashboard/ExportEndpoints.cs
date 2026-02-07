using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Export Hub — trigger, track, and download exports.
/// </summary>
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/exports")
            .WithTags("Exports")
            .RequireAuthorization();

        // =============================================================================
        // GET /exports/stats - Export overview stats
        // =============================================================================
        group.MapGet("/stats", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var stats = await conn.QueryFirstOrDefaultAsync<ExportStatsDto>(
                """
                SELECT
                    (SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId AND is_active = true) AS active_memories,
                    (SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId) AS total_entities,
                    (SELECT COUNT(*) FROM entity_relationships WHERE tenant_id = @TenantId) AS total_relationships,
                    (SELECT MAX(created_at) FROM exports WHERE tenant_id = @TenantId AND status = 'completed') AS last_export_at,
                    (SELECT COUNT(*) FROM exports WHERE tenant_id = @TenantId) AS total_exports
                """,
                new { TenantId = tenantId.ToString() });

            return Results.Ok(stats ?? new ExportStatsDto());
        })
        .WithName("GetExportStats")
        .WithDescription("Get export overview statistics")
        .Produces<ExportStatsDto>(StatusCodes.Status200OK);

        // =============================================================================
        // POST /exports/trigger - Trigger a new export
        // =============================================================================
        group.MapPost("/trigger", async (
            [FromBody] TriggerExportRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            if (string.IsNullOrWhiteSpace(request.Format) ||
                request.Format is not ("json" or "csv" or "graphml" or "cytoscape" or "markdown"))
                return Results.BadRequest(new { error = "invalid_format", message = "Format must be: json, csv, graphml, cytoscape, or markdown" });

            if (!string.IsNullOrEmpty(request.GroupBy) &&
                request.GroupBy is not ("month" or "layer" or "source"))
                return Results.BadRequest(new { error = "invalid_group_by", message = "GroupBy must be: month, layer, or source" });

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Check no export already running
            var running = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM exports WHERE tenant_id = @TenantId AND status IN ('pending', 'running')",
                new { TenantId = tenantId.ToString() });

            if (running > 0)
                return Results.Conflict(new { error = "export_in_progress", message = "An export is already running" });

            var exportId = Guid.CreateVersion7();
            var options = System.Text.Json.JsonSerializer.Serialize(new
            {
                request.ActiveOnly,
                request.IncludeEntities,
                request.IncludeEvents,
                request.MinConfidence,
                request.GroupBy,
                request.IncludeSessions,
                request.Compress
            });

            await conn.ExecuteAsync(
                """
                INSERT INTO exports (id, tenant_id, format, status, options, created_at)
                VALUES (@Id, @TenantId, @Format, 'pending', @Options::jsonb, NOW())
                """,
                new { Id = exportId, TenantId = tenantId.ToString(), request.Format, Options = options });

            // Guard against excessively large inline exports
            var memoryCount = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId AND is_active = true",
                new { TenantId = tenantId.ToString() });

            if (memoryCount > 100_000)
                return Results.UnprocessableEntity(new { error = "dataset_too_large", message = "Dataset exceeds 100k memories. Use the background export API instead." });

            // Build export inline (for small datasets; large datasets would use a background worker)
            try
            {
                await conn.ExecuteAsync(
                    "UPDATE exports SET status = 'running' WHERE id = @Id",
                    new { Id = exportId });

                var exportData = await BuildExportAsync(conn, tenantId, request);
                var json = System.Text.Json.JsonSerializer.Serialize(exportData,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                var exportDir = Path.Combine(Path.GetTempPath(), "serialmemory_exports");
                Directory.CreateDirectory(exportDir);
                var ext = request.Format switch
                {
                    "csv" => ".csv",
                    "graphml" => ".graphml",
                    "markdown" => ".md",
                    _ => ".json"
                };
                var fileName = $"{request.Format}_{tenantId.ToString()[..8]}_{DateTime.UtcNow:yyyyMMdd_HHmmss}{ext}";
                var filePath = Path.Combine(exportDir, fileName);
                await File.WriteAllTextAsync(filePath, json, ct);

                var fileSize = new FileInfo(filePath).Length;

                await conn.ExecuteAsync(
                    """
                    UPDATE exports SET status = 'completed', file_path = @FilePath,
                           file_size_bytes = @FileSize, completed_at = NOW()
                    WHERE id = @Id
                    """,
                    new { Id = exportId, FilePath = filePath, FileSize = fileSize });

                return Results.Ok(new
                {
                    export_id = exportId,
                    status = "completed",
                    format = request.Format,
                    file_size_bytes = fileSize,
                    download_url = $"/api/exports/download/{exportId}"
                });
            }
            catch (Exception ex)
            {
                await conn.ExecuteAsync(
                    "UPDATE exports SET status = 'failed', error_message = @Error, completed_at = NOW() WHERE id = @Id",
                    new { Id = exportId, Error = ex.Message });

                return Results.Problem(detail: "An internal error occurred while generating the export.", title: "Export failed", statusCode: 500);
            }
        })
        .WithName("TriggerExport")
        .WithDescription("Trigger a new data export")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status409Conflict);

        // =============================================================================
        // GET /exports/history - Recent export history
        // =============================================================================
        group.MapGet("/history", async (
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var exports = await conn.QueryAsync<ExportHistoryDto>(
                """
                SELECT id, format, status, file_size_bytes, options, created_at, completed_at, error_message
                FROM exports
                WHERE tenant_id = @TenantId
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId.ToString(), Limit = Math.Clamp(limit ?? 20, 1, 100) });

            return Results.Ok(new { exports = exports.ToList() });
        })
        .WithName("GetExportHistory")
        .WithDescription("Get recent export history")
        .Produces(StatusCodes.Status200OK);

        // =============================================================================
        // GET /exports/download/{id} - Download an export file
        // =============================================================================
        group.MapGet("/download/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var export = await conn.QueryFirstOrDefaultAsync<ExportFileDto>(
                "SELECT file_path, format FROM exports WHERE id = @Id AND tenant_id = @TenantId AND status = 'completed'",
                new { Id = id, TenantId = tenantId.ToString() });

            if (export == null || string.IsNullOrEmpty(export.file_path))
                return Results.NotFound(new { error = "not_found", message = "Export not found or file missing" });

            // Path traversal protection: ensure file is within the expected export directory
            var expectedDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "serialmemory_exports"));
            var resolvedPath = Path.GetFullPath(export.file_path);
            if (!resolvedPath.StartsWith(expectedDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolvedPath))
                return Results.NotFound(new { error = "not_found", message = "Export not found or file missing" });

            var contentType = export.format switch
            {
                "csv" => "text/csv",
                "graphml" => "application/xml",
                "markdown" => "text/markdown",
                _ => "application/json"
            };

            return Results.File(resolvedPath, contentType, Path.GetFileName(resolvedPath));
        })
        .WithName("DownloadExport")
        .WithDescription("Download an exported file")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<object> BuildExportAsync(System.Data.IDbConnection conn, Guid tenantId, TriggerExportRequest request)
    {
        var activeFilter = request.ActiveOnly ? "AND is_active = true" : "";
        var confidenceFilter = request.MinConfidence > 0 ? "AND confidence >= @MinConfidence" : "";

        var memories = await conn.QueryAsync<dynamic>(
            $"""
            SELECT id, content, layer, confidence, source, created_at, updated_at, metadata
            FROM memories
            WHERE tenant_id = @TenantId {activeFilter} {confidenceFilter}
            ORDER BY created_at DESC
            LIMIT 50000
            """,
            new { TenantId = tenantId.ToString(), MinConfidence = request.MinConfidence });

        object? entities = null;
        object? relationships = null;

        if (request.IncludeEntities)
        {
            entities = await conn.QueryAsync<dynamic>(
                "SELECT id, name, entity_type, canonical_name, created_at FROM entities WHERE tenant_id = @TenantId ORDER BY name LIMIT 50000",
                new { TenantId = tenantId.ToString() });

            relationships = await conn.QueryAsync<dynamic>(
                "SELECT id, source_entity_id, target_entity_id, relationship_type, confidence, created_at FROM entity_relationships WHERE tenant_id = @TenantId LIMIT 50000",
                new { TenantId = tenantId.ToString() });
        }

        return new
        {
            exported_at = DateTimeOffset.UtcNow,
            format = request.Format,
            memory_count = memories.Count(),
            memories,
            entities,
            relationships
        };
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
        => DashboardHelpers.GetTenantId(user, selfHosted);
}

// DTOs
public sealed class ExportStatsDto
{
    public long active_memories { get; set; }
    public long total_entities { get; set; }
    public long total_relationships { get; set; }
    public DateTimeOffset? last_export_at { get; set; }
    public long total_exports { get; set; }
}

public sealed class ExportHistoryDto
{
    public Guid id { get; set; }
    public string format { get; set; } = "";
    public string status { get; set; } = "";
    public long? file_size_bytes { get; set; }
    public object? options { get; set; }
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset? completed_at { get; set; }
    public string? error_message { get; set; }
}

public sealed class ExportFileDto
{
    public string? file_path { get; set; }
    public string format { get; set; } = "";
}

public sealed class TriggerExportRequest
{
    public string Format { get; init; } = "json";
    public bool ActiveOnly { get; init; } = true;
    public bool IncludeEntities { get; init; } = true;
    public bool IncludeEvents { get; init; }
    public float MinConfidence { get; init; }
    public string? GroupBy { get; init; }
    public bool IncludeSessions { get; init; }
    public bool Compress { get; init; }
}
