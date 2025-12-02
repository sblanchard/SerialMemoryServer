using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// Dashboard endpoints for Shadow Branches.
/// FIX #7: Create branch returns 404 - Fixed RLS, missing fields, and wrong paths.
/// </summary>
public static class BranchesEndpoints
{
    public static void MapBranchesEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/shadow/branches")
            .WithTags("Shadow Branches")
            .RequireAuthorization();

        // =============================================================================
        // GET /branches - List all branches
        // =============================================================================
        group.MapGet("", async (
            string? status,
            string? branchType,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var filters = new List<string> { "sb.tenant_id = @TenantId" };
            if (!string.IsNullOrEmpty(status))
                filters.Add("sb.status = @Status::shadow_branch_status");
            if (!string.IsNullOrEmpty(branchType))
                filters.Add("sb.branch_type = @BranchType::shadow_branch_type");

            var whereClause = string.Join(" AND ", filters);

            var branches = await conn.QueryAsync<BranchListDto>(
                $"""
                SELECT
                    sb.id,
                    sb.name,
                    sb.description,
                    sb.branch_type,
                    sb.status,
                    sb.parent_branch_id,
                    sb.source_memory_id,
                    sb.confidence,
                    sb.hypothesis,
                    sb.ttl_hours,
                    sb.expires_at,
                    sb.created_at,
                    sb.frozen_at,
                    sb.promoted_at,
                    sb.discarded_at,
                    (SELECT COUNT(*) FROM shadow_memories sm WHERE sm.branch_id = sb.id) AS memory_count,
                    (SELECT COUNT(*) FROM shadow_entities se WHERE se.branch_id = sb.id) AS entity_count
                FROM shadow_branches sb
                WHERE {whereClause}
                ORDER BY sb.created_at DESC
                """,
                new
                {
                    TenantId = tenantId.ToString(),
                    Status = status?.ToLowerInvariant(),
                    BranchType = branchType?.ToLowerInvariant()
                });

            return Results.Ok(new
            {
                branches = branches.ToList(),
                total = branches.Count()
            });
        })
        .WithName("ListBranches")
        .WithDescription("Lists all shadow branches")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /branches/create - Create a new branch (FIX #7)
        // =============================================================================
        group.MapPost("/create", async (
            [FromBody] CreateBranchApiRequest request,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IShadowMemoryStore? shadowStore,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            // Validate branch name
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "validation_error", message = "Branch name is required" });

            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // CRITICAL FIX: Set internal_admin role to bypass RLS for INSERT
            await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
            await conn.ExecuteAsync("SELECT set_config('app.current_tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            // Check for duplicate name
            var existingBranch = await conn.QueryFirstOrDefaultAsync<Guid?>(
                """
                SELECT id FROM shadow_branches
                WHERE tenant_id = @TenantId AND name = @Name AND status != 'discarded'
                """,
                new { TenantId = tenantId.ToString(), Name = request.Name });

            if (existingBranch.HasValue)
                return Results.Conflict(new { error = "duplicate", message = "A branch with this name already exists" });

            var branchId = Guid.CreateVersion7();
            var branchType = request.BranchType?.ToLowerInvariant() ?? "exploration";
            var now = DateTimeOffset.UtcNow;

            // Calculate expiration if TTL provided
            DateTimeOffset? expiresAt = request.TtlHours.HasValue
                ? now.AddHours(request.TtlHours.Value)
                : null;

            // CRITICAL FIX: Insert with all required fields
            await conn.ExecuteAsync(
                """
                INSERT INTO shadow_branches (
                    id, tenant_id, name, description, branch_type, status,
                    parent_branch_id, source_memory_id, confidence, hypothesis,
                    reasoning_context, ttl_hours, auto_discard_threshold, expires_at,
                    created_at, metadata
                ) VALUES (
                    @Id, @TenantId, @Name, @Description, @BranchType::shadow_branch_type, 'active'::shadow_branch_status,
                    @ParentBranchId, @SourceMemoryId, @Confidence, @Hypothesis,
                    @ReasoningContext, @TtlHours, @AutoDiscardThreshold, @ExpiresAt,
                    @CreatedAt, @Metadata::jsonb
                )
                """,
                new
                {
                    Id = branchId,
                    TenantId = tenantId.ToString(),
                    Name = request.Name,
                    Description = request.Description,
                    BranchType = branchType,
                    ParentBranchId = request.ParentBranchId,
                    SourceMemoryId = request.SourceMemoryId,
                    Confidence = request.Confidence ?? 0.5f,
                    Hypothesis = request.Hypothesis,
                    ReasoningContext = request.ReasoningContext,
                    TtlHours = request.TtlHours,
                    AutoDiscardThreshold = request.AutoDiscardThreshold,
                    ExpiresAt = expiresAt,
                    CreatedAt = now,
                    Metadata = request.Metadata != null
                        ? System.Text.Json.JsonSerializer.Serialize(request.Metadata)
                        : null
                });

            // Create default snapshot if source memory provided
            if (request.SourceMemoryId.HasValue)
            {
                var sourceMemory = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT content, embedding FROM memories WHERE id = @Id",
                    new { Id = request.SourceMemoryId.Value });

                if (sourceMemory != null)
                {
                    var shadowMemoryId = Guid.CreateVersion7();
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO shadow_memories (
                            id, branch_id, tenant_id, content, embedding, confidence,
                            shadows_memory_id, reasoning_step, source, created_at
                        ) VALUES (
                            @Id, @BranchId, @TenantId, @Content, @Embedding, 1.0,
                            @ShadowsMemoryId, 0, 'branch_creation', NOW()
                        )
                        """,
                        new
                        {
                            Id = shadowMemoryId,
                            BranchId = branchId,
                            TenantId = tenantId.ToString(),
                            Content = (string)sourceMemory.content,
                            Embedding = sourceMemory.embedding?.ToString(),
                            ShadowsMemoryId = request.SourceMemoryId.Value
                        });
                }
            }

            return Results.Created($"/branches/{branchId}", new
            {
                id = branchId,
                name = request.Name,
                branchType,
                status = "active",
                confidence = request.Confidence ?? 0.5f,
                expiresAt,
                createdAt = now,
                message = "Branch created successfully"
            });
        })
        .WithName("CreateBranch")
        .WithDescription("Creates a new shadow branch")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status409Conflict);

        // =============================================================================
        // GET /branches/{branchId} - Get branch details (FIX #7)
        // =============================================================================
        group.MapGet("/{branchId:guid}", async (
            Guid branchId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var branch = await conn.QueryFirstOrDefaultAsync<BranchDetailDto>(
                """
                SELECT
                    sb.id,
                    sb.name,
                    sb.description,
                    sb.branch_type,
                    sb.status,
                    sb.parent_branch_id,
                    sb.source_memory_id,
                    sb.confidence,
                    sb.hypothesis,
                    sb.reasoning_context,
                    sb.ttl_hours,
                    sb.auto_discard_threshold,
                    sb.expires_at,
                    sb.created_at,
                    sb.frozen_at,
                    sb.promoted_at,
                    sb.discarded_at,
                    sb.metadata
                FROM shadow_branches sb
                WHERE sb.id = @BranchId AND sb.tenant_id = @TenantId
                """,
                new { BranchId = branchId, TenantId = tenantId.ToString() });

            if (branch == null)
                return Results.NotFound(new { error = "not_found", message = "Branch not found" });

            // Get stats
            var stats = await conn.QueryFirstOrDefaultAsync<BranchStatsDto>(
                """
                SELECT
                    (SELECT COUNT(*) FROM shadow_memories WHERE branch_id = @BranchId) AS memory_count,
                    (SELECT COUNT(*) FROM shadow_entities WHERE branch_id = @BranchId) AS entity_count,
                    (SELECT COUNT(*) FROM shadow_relationships WHERE branch_id = @BranchId) AS relationship_count,
                    (SELECT COUNT(*) FROM shadow_branches WHERE parent_branch_id = @BranchId) AS child_branch_count,
                    (SELECT AVG(confidence) FROM shadow_memories WHERE branch_id = @BranchId) AS avg_confidence
                """,
                new { BranchId = branchId });

            return Results.Ok(new
            {
                branch,
                stats = stats ?? new BranchStatsDto()
            });
        })
        .WithName("GetBranch")
        .WithDescription("Gets branch details")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // POST /branches/{branchId}/promote - Promote branch to main (FIX #7)
        // =============================================================================
        group.MapPost("/{branchId:guid}/promote", async (
            Guid branchId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            [FromServices] IShadowMemoryStore? shadowStore,
            [FromServices] IKnowledgeGraphStore? mainStore,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);
            var userId = GetUserId(user);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            // Verify branch exists and is active or frozen
            var branch = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, name, status, confidence
                FROM shadow_branches
                WHERE id = @BranchId AND tenant_id = @TenantId
                """,
                new { BranchId = branchId, TenantId = tenantId.ToString() });

            if (branch == null)
                return Results.NotFound(new { error = "not_found", message = "Branch not found" });

            var status = (string)branch.status;
            if (status != "active" && status != "frozen")
                return Results.BadRequest(new { error = "invalid_status", message = $"Cannot promote branch with status: {status}" });

            // Use shadow store service if available
            if (shadowStore != null && mainStore != null)
            {
                var result = await shadowStore.PromoteBranchAsync(branchId, mainStore, ct);
                if (!result.Success)
                    return Results.Problem(detail: result.ErrorMessage, title: "Promotion failed");

                return Results.Ok(new
                {
                    branchId,
                    status = "promoted",
                    memoriesPromoted = result.MemoriesPromoted,
                    entitiesPromoted = result.EntitiesPromoted,
                    relationshipsPromoted = result.RelationshipsPromoted,
                    message = "Branch promoted successfully"
                });
            }

            // Fall back to SQL promotion
            // Mark branch as promoted
            await conn.ExecuteAsync(
                """
                UPDATE shadow_branches
                SET status = 'promoted'::shadow_branch_status, promoted_at = NOW()
                WHERE id = @BranchId
                """,
                new { BranchId = branchId });

            // Count promoted items
            var memoriesCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM shadow_memories WHERE branch_id = @BranchId",
                new { BranchId = branchId });

            var entitiesCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM shadow_entities WHERE branch_id = @BranchId",
                new { BranchId = branchId });

            return Results.Ok(new
            {
                branchId,
                status = "promoted",
                memoriesPromoted = memoriesCount,
                entitiesPromoted = entitiesCount,
                message = "Branch promoted successfully (manual promotion required for full merge)"
            });
        })
        .WithName("PromoteBranch")
        .WithDescription("Promotes a shadow branch to main")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // POST /branches/{branchId}/freeze - Freeze a branch
        // =============================================================================
        group.MapPost("/{branchId:guid}/freeze", async (
            Guid branchId,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var affected = await conn.ExecuteAsync(
                """
                UPDATE shadow_branches
                SET status = 'frozen'::shadow_branch_status, frozen_at = NOW()
                WHERE id = @BranchId AND tenant_id = @TenantId AND status = 'active'
                """,
                new { BranchId = branchId, TenantId = tenantId.ToString() });

            if (affected == 0)
                return Results.NotFound(new { error = "not_found", message = "Branch not found or not active" });

            return Results.Ok(new { branchId, status = "frozen", message = "Branch frozen successfully" });
        })
        .WithName("FreezeBranch")
        .WithDescription("Freezes a branch (prevents further modifications)")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // =============================================================================
        // DELETE /branches/{branchId} - Discard a branch
        // =============================================================================
        group.MapDelete("/{branchId:guid}", async (
            Guid branchId,
            [FromQuery] string? reason,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var tenantId = GetTenantId(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.SetInternalAdminWithTenantAsync(tenantId);

            var affected = await conn.ExecuteAsync(
                """
                UPDATE shadow_branches
                SET status = 'discarded'::shadow_branch_status, discarded_at = NOW(),
                    metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_build_object('discard_reason', @Reason)
                WHERE id = @BranchId AND tenant_id = @TenantId AND status NOT IN ('promoted', 'discarded')
                """,
                new { BranchId = branchId, TenantId = tenantId.ToString(), Reason = reason });

            if (affected == 0)
                return Results.NotFound(new { error = "not_found", message = "Branch not found or already discarded/promoted" });

            return Results.Ok(new { branchId, status = "discarded", message = "Branch discarded successfully" });
        })
        .WithName("DiscardBranch")
        .WithDescription("Discards a shadow branch")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);
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

// DTOs for Branches endpoints
public sealed class BranchListDto
{
    public Guid id { get; set; }
    public string name { get; set; } = "";
    public string? description { get; set; }
    public string branch_type { get; set; } = "";
    public string status { get; set; } = "";
    public Guid? parent_branch_id { get; set; }
    public Guid? source_memory_id { get; set; }
    public float confidence { get; set; }
    public string? hypothesis { get; set; }
    public int? ttl_hours { get; set; }
    public DateTimeOffset? expires_at { get; set; }
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset? frozen_at { get; set; }
    public DateTimeOffset? promoted_at { get; set; }
    public DateTimeOffset? discarded_at { get; set; }
    public int memory_count { get; set; }
    public int entity_count { get; set; }
}

public sealed class BranchDetailDto
{
    public Guid id { get; set; }
    public string name { get; set; } = "";
    public string? description { get; set; }
    public string branch_type { get; set; } = "";
    public string status { get; set; } = "";
    public Guid? parent_branch_id { get; set; }
    public Guid? source_memory_id { get; set; }
    public float confidence { get; set; }
    public string? hypothesis { get; set; }
    public string? reasoning_context { get; set; }
    public int? ttl_hours { get; set; }
    public float? auto_discard_threshold { get; set; }
    public DateTimeOffset? expires_at { get; set; }
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset? frozen_at { get; set; }
    public DateTimeOffset? promoted_at { get; set; }
    public DateTimeOffset? discarded_at { get; set; }
    public object? metadata { get; set; }
}

public sealed class BranchStatsDto
{
    public int memory_count { get; set; }
    public int entity_count { get; set; }
    public int relationship_count { get; set; }
    public int child_branch_count { get; set; }
    public float? avg_confidence { get; set; }
}

public sealed record CreateBranchApiRequest(
    string Name,
    string? Description = null,
    string? BranchType = null,
    Guid? ParentBranchId = null,
    Guid? SourceMemoryId = null,
    float? Confidence = null,
    string? Hypothesis = null,
    string? ReasoningContext = null,
    int? TtlHours = null,
    float? AutoDiscardThreshold = null,
    Dictionary<string, object>? Metadata = null
);
