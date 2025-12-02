using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Conflicts and Contradictions detection.
/// FIX #2: Run Detection Scan must actually run and detect contradictions.
/// </summary>
public static class ConflictsEndpoints
{
    public static void MapConflictsEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/conflicts")
            .WithTags("Conflicts")
            .RequireAuthorization();

        // =============================================================================
        // GET /conflicts - List all detected conflicts
        // =============================================================================
        group.MapGet("", async (
            bool? includeResolved,
            string? conflictType,
            int? limit,
            int? offset,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var statusFilter = includeResolved == true
                ? ""
                : "AND cl.status != 'resolved'";

            var typeFilter = string.IsNullOrEmpty(conflictType)
                ? ""
                : "AND cl.conflict_type = @ConflictType";

            var conflicts = await conn.QueryAsync<ConflictLogDto>(
                $"""
                SELECT
                    cl.id,
                    cl.memory_a,
                    cl.memory_b,
                    cl.conflict_type,
                    cl.severity,
                    cl.description,
                    cl.entity_involved,
                    cl.detected_at,
                    cl.status,
                    cl.resolved_at,
                    cl.resolution_strategy,
                    cl.resolution_notes,
                    cl.metadata,
                    m1.content AS memory_a_content,
                    m2.content AS memory_b_content,
                    e.name AS entity_name
                FROM conflict_log cl
                LEFT JOIN memories m1 ON cl.memory_a = m1.id
                LEFT JOIN memories m2 ON cl.memory_b = m2.id
                LEFT JOIN entities e ON cl.entity_involved = e.id
                WHERE cl.tenant_id = @TenantId
                    {statusFilter}
                    {typeFilter}
                ORDER BY cl.detected_at DESC
                LIMIT @Limit
                OFFSET @Offset
                """,
                new
                {
                    TenantId = tenantId,
                    ConflictType = conflictType,
                    Limit = limit ?? 50,
                    Offset = offset ?? 0
                });

            // Get counts by status
            var counts = await conn.QueryAsync<StatusCountDto>(
                """
                SELECT status, COUNT(*) AS count
                FROM conflict_log
                WHERE tenant_id = @TenantId
                GROUP BY status
                """,
                new { TenantId = tenantId });

            var total = counts.Sum(c => c.count);
            var pending = counts.FirstOrDefault(c => c.status == "pending")?.count ?? 0;
            var resolved = counts.FirstOrDefault(c => c.status == "resolved")?.count ?? 0;

            return Results.Ok(new
            {
                conflicts = conflicts.ToList(),
                total,
                pending,
                resolved,
                limit = limit ?? 50,
                offset = offset ?? 0
            });
        })
        .WithName("ListConflicts")
        .WithDescription("Lists all detected conflicts and contradictions")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /conflicts/run-scan - RUN THE DETECTION SCAN (FIX #2)
        // =============================================================================
        group.MapPost("/run-scan", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IConflictScanner? conflictScanner,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // CRITICAL: Set internal_admin role to bypass RLS for scan operations
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
            await conn.ExecuteAsync("SELECT set_config('app.current_tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var scanId = Guid.CreateVersion7();
            var startTime = DateTimeOffset.UtcNow;
            var conflictsDetected = 0;

            try
            {
                // Use service if available, otherwise use SQL function
                if (conflictScanner != null)
                {
                    var result = await conflictScanner.RunScanAsync(tenantId, 0.85f, ct);
                    conflictsDetected = result.ConflictsDetected;
                }
                else
                {
                    // Fall back to SQL function
                    var scanResult = await conn.QueryFirstOrDefaultAsync<ScanResultDto>(
                        "SELECT * FROM run_contradiction_scan(@TenantId)",
                        new { TenantId = tenantId });

                    conflictsDetected = scanResult?.conflicts_detected ?? 0;
                }

                // Log the scan event
                await conn.ExecuteAsync(
                    """
                    INSERT INTO event_log (id, tenant_id, event_type, category, severity, actor, payload, created_at)
                    VALUES (@Id, @TenantId, 'conflict_scan_completed', 'conflicts', 'info', @Actor,
                            @Payload::jsonb, NOW())
                    """,
                    new
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        Actor = userId,
                        Payload = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            scan_id = scanId,
                            conflicts_detected = conflictsDetected,
                            duration_ms = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds
                        })
                    });

                return Results.Ok(new
                {
                    scan_id = scanId,
                    status = "completed",
                    conflicts_detected = conflictsDetected,
                    duration_ms = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds,
                    message = conflictsDetected > 0
                        ? $"Detected {conflictsDetected} new conflicts"
                        : "No new conflicts detected"
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    title: "Conflict scan failed",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("RunConflictScan")
        .WithDescription("Runs contradiction detection scan on L3 facts")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status500InternalServerError);

        // =============================================================================
        // GET /conflicts/{conflictId} - Get specific conflict details
        // =============================================================================
        group.MapGet("/{conflictId:guid}", async (
            Guid conflictId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var conflict = await conn.QueryFirstOrDefaultAsync<ConflictDetailDto>(
                """
                SELECT
                    cl.id,
                    cl.memory_a,
                    cl.memory_b,
                    cl.conflict_type,
                    cl.severity,
                    cl.description,
                    cl.entity_involved,
                    cl.detected_at,
                    cl.status,
                    cl.resolved_at,
                    cl.resolution_strategy,
                    cl.resolution_notes,
                    cl.metadata,
                    m1.content AS memory_a_content,
                    m1.layer AS memory_a_layer,
                    m1.confidence AS memory_a_confidence,
                    m2.content AS memory_b_content,
                    m2.layer AS memory_b_layer,
                    m2.confidence AS memory_b_confidence,
                    e.name AS entity_name,
                    e.entity_type AS entity_type_name
                FROM conflict_log cl
                LEFT JOIN memories m1 ON cl.memory_a = m1.id
                LEFT JOIN memories m2 ON cl.memory_b = m2.id
                LEFT JOIN entities e ON cl.entity_involved = e.id
                WHERE cl.id = @ConflictId AND cl.tenant_id = @TenantId
                """,
                new { ConflictId = conflictId, TenantId = tenantId });

            if (conflict == null)
                return Results.NotFound(new { error = "not_found", message = "Conflict not found" });

            return Results.Ok(conflict);
        })
        .WithName("GetConflict")
        .WithDescription("Gets details of a specific conflict")
        .Produces<ConflictDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // POST /conflicts/{conflictId}/resolve - Resolve a conflict
        // =============================================================================
        group.MapPost("/{conflictId:guid}/resolve", async (
            Guid conflictId,
            [FromBody] ResolveConflictRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var affected = await conn.ExecuteAsync(
                """
                UPDATE conflict_log
                SET status = 'resolved',
                    resolved_at = NOW(),
                    resolution_strategy = @Strategy,
                    resolution_notes = @Notes
                WHERE id = @ConflictId AND tenant_id = @TenantId AND status != 'resolved'
                """,
                new
                {
                    ConflictId = conflictId,
                    TenantId = tenantId,
                    Strategy = request.Strategy,
                    Notes = request.Notes
                });

            if (affected == 0)
                return Results.NotFound(new { error = "not_found", message = "Conflict not found or already resolved" });

            // Apply resolution strategy if specified
            if (!string.IsNullOrEmpty(request.Strategy))
            {
                switch (request.Strategy.ToLowerInvariant())
                {
                    case "prefer_memory_a":
                        // Lower confidence of memory B
                        await conn.ExecuteAsync(
                            """
                            UPDATE memories SET confidence = confidence * 0.5
                            WHERE id = (SELECT memory_b FROM conflict_log WHERE id = @ConflictId)
                            """,
                            new { ConflictId = conflictId });
                        break;

                    case "prefer_memory_b":
                        // Lower confidence of memory A
                        await conn.ExecuteAsync(
                            """
                            UPDATE memories SET confidence = confidence * 0.5
                            WHERE id = (SELECT memory_a FROM conflict_log WHERE id = @ConflictId)
                            """,
                            new { ConflictId = conflictId });
                        break;

                    case "invalidate_both":
                        // Mark both as invalidated
                        await conn.ExecuteAsync(
                            """
                            UPDATE memories SET is_active = FALSE
                            WHERE id IN (
                                SELECT memory_a FROM conflict_log WHERE id = @ConflictId
                                UNION
                                SELECT memory_b FROM conflict_log WHERE id = @ConflictId
                            )
                            """,
                            new { ConflictId = conflictId });
                        break;
                }
            }

            // Log the resolution event
            await conn.ExecuteAsync(
                """
                INSERT INTO event_log (id, tenant_id, event_type, category, severity, actor, payload, created_at)
                VALUES (@Id, @TenantId, 'conflict_resolved', 'conflicts', 'info', @Actor,
                        @Payload::jsonb, NOW())
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Actor = userId,
                    Payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        conflict_id = conflictId,
                        strategy = request.Strategy,
                        notes = request.Notes
                    })
                });

            return Results.Ok(new
            {
                conflict_id = conflictId,
                status = "resolved",
                strategy = request.Strategy,
                message = "Conflict resolved successfully"
            });
        })
        .WithName("ResolveConflict")
        .WithDescription("Resolves a detected conflict")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // POST /conflicts/{conflictId}/dismiss - Dismiss a conflict (false positive)
        // =============================================================================
        group.MapPost("/{conflictId:guid}/dismiss", async (
            Guid conflictId,
            [FromBody] DismissConflictRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var affected = await conn.ExecuteAsync(
                """
                UPDATE conflict_log
                SET status = 'dismissed',
                    resolved_at = NOW(),
                    resolution_notes = @Reason
                WHERE id = @ConflictId AND tenant_id = @TenantId
                """,
                new
                {
                    ConflictId = conflictId,
                    TenantId = tenantId,
                    Reason = request.Reason ?? "Dismissed as false positive"
                });

            if (affected == 0)
                return Results.NotFound(new { error = "not_found", message = "Conflict not found" });

            return Results.Ok(new
            {
                conflict_id = conflictId,
                status = "dismissed",
                message = "Conflict dismissed"
            });
        })
        .WithName("DismissConflict")
        .WithDescription("Dismisses a conflict as a false positive")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // GET /conflicts/summary - Get conflict summary stats
        // =============================================================================
        group.MapGet("/summary", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var summary = await conn.QueryFirstOrDefaultAsync<ConflictSummaryDto>(
                """
                SELECT
                    COUNT(*) AS total_conflicts,
                    COUNT(*) FILTER (WHERE status = 'pending') AS pending_count,
                    COUNT(*) FILTER (WHERE status = 'resolved') AS resolved_count,
                    COUNT(*) FILTER (WHERE status = 'dismissed') AS dismissed_count,
                    COUNT(*) FILTER (WHERE conflict_type = 'semantic_contradiction') AS semantic_count,
                    COUNT(*) FILTER (WHERE conflict_type = 'entity_value_mismatch') AS entity_mismatch_count,
                    COUNT(*) FILTER (WHERE conflict_type = 'timeline_drift') AS timeline_drift_count,
                    AVG(severity) FILTER (WHERE status = 'pending') AS avg_pending_severity,
                    MAX(detected_at) AS last_scan_at
                FROM conflict_log
                WHERE tenant_id = @TenantId
                """,
                new { TenantId = tenantId });

            return Results.Ok(summary ?? new ConflictSummaryDto());
        })
        .WithName("GetConflictSummary")
        .WithDescription("Gets summary statistics of detected conflicts")
        .Produces<ConflictSummaryDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    private static Guid GetTenantId(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
            return Guid.Parse("00000000-0000-0000-0000-000000000000");

        var tenantIdClaim = user.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

        return Guid.Parse(tenantIdClaim);
    }

    private static string GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "system";
    }
}

// DTOs for Conflicts endpoints
public class ConflictLogDto
{
    public Guid id { get; set; }
    public Guid memory_a { get; set; }
    public Guid memory_b { get; set; }
    public string conflict_type { get; set; } = "";
    public decimal severity { get; set; }
    public string? description { get; set; }
    public Guid? entity_involved { get; set; }
    public DateTimeOffset detected_at { get; set; }
    public string status { get; set; } = "pending";
    public DateTimeOffset? resolved_at { get; set; }
    public string? resolution_strategy { get; set; }
    public string? resolution_notes { get; set; }
    public object? metadata { get; set; }
    public string? memory_a_content { get; set; }
    public string? memory_b_content { get; set; }
    public string? entity_name { get; set; }
}

public sealed class ConflictDetailDto : ConflictLogDto
{
    public string? memory_a_layer { get; set; }
    public decimal? memory_a_confidence { get; set; }
    public string? memory_b_layer { get; set; }
    public decimal? memory_b_confidence { get; set; }
    public string? entity_type_name { get; set; }
}

public sealed class ScanResultDto
{
    public int conflicts_detected { get; set; }
    public int conflicts_resolved { get; set; }
    public int scan_duration_ms { get; set; }
}

public sealed class StatusCountDto
{
    public string status { get; set; } = "";
    public long count { get; set; }
}

public sealed class ConflictSummaryDto
{
    public long total_conflicts { get; set; }
    public long pending_count { get; set; }
    public long resolved_count { get; set; }
    public long dismissed_count { get; set; }
    public long semantic_count { get; set; }
    public long entity_mismatch_count { get; set; }
    public long timeline_drift_count { get; set; }
    public decimal? avg_pending_severity { get; set; }
    public DateTimeOffset? last_scan_at { get; set; }
}

public sealed record ResolveConflictRequest(string? Strategy = null, string? Notes = null);
public sealed record DismissConflictRequest(string? Reason = null);
