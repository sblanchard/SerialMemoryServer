using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Api.Dashboard;

/// <summary>
/// RAG (Retrieval-Augmented Generation) endpoints for personalized memory-based Q&A.
/// Uses L2 summaries as the primary retrieval surface, enriched with L1/L3/L4.
/// </summary>
public static class RagEndpoints
{
    public static void MapRagEndpoints(this WebApplication app, bool selfHostedMode)
    {
        var group = app.MapGroup("/api/rag")
            .WithTags("RAG")
            .RequireAuthorization();

        // =============================================================================
        // POST /rag/answer - Answer a question using RAG over user memories
        // =============================================================================
        group.MapPost("/answer", async (
            [FromBody] RagAnswerRequest request,
            ClaimsPrincipal user,
            IPersonalizedRagService ragService,
            CancellationToken ct) =>
        {
            var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new { error = "validation_error", message = "Query is required" });

            try
            {
                var options = new RagOptions
                {
                    MaxMemories = request.MaxMemories ?? 5,
                    IncludeL1 = request.IncludeL1 ?? true,
                    IncludeL3 = request.IncludeL3 ?? true,
                    IncludeL4 = request.IncludeL4 ?? true,
                    SimilarityThreshold = request.SimilarityThreshold ?? 0.5m,
                    Temperature = request.Temperature ?? 0.3f,
                    IncludeReasoningTrace = request.IncludeReasoningTrace ?? false
                };

                var result = await ragService.AnswerAsync(tenantId, userId, request.Query, options, ct);

                return Results.Ok(new RagAnswerResponse
                {
                    Answer = result.Answer,
                    QueryId = result.QueryId,
                    Memories = result.Memories.Select(m => new RetrievedMemoryDto
                    {
                        MemoryId = m.MemoryId,
                        L2Summary = m.L2Summary,
                        L2OneLiner = m.L2OneLiner,
                        L1Context = m.L1Context,
                        L3Facts = m.L3Facts.ToList(),
                        L4Heuristics = m.L4Heuristics.ToList(),
                        Keywords = m.Keywords.ToList(),
                        Similarity = m.Similarity,
                        Importance = m.Importance,
                        CreatedAt = m.CreatedAt
                    }).ToList(),
                    ReasoningTrace = result.ReasoningTrace,
                    ModelName = result.ModelName,
                    LatencyMs = result.LatencyMs
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "RAG Query Failed");
            }
        })
        .WithName("RagAnswer")
        .WithDescription("Answers a question using personalized RAG over the user's memories")
        .Produces<RagAnswerResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status500InternalServerError);

        // =============================================================================
        // POST /rag/search - Search memories without generating an answer
        // =============================================================================
        group.MapPost("/search", async (
            [FromBody] RagSearchRequest request,
            ClaimsPrincipal user,
            IPersonalizedRagService ragService,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new { error = "validation_error", message = "Query is required" });

            try
            {
                var memories = await ragService.SearchMemoriesAsync(
                    tenantId,
                    request.Query,
                    request.Limit ?? 10,
                    ct);

                return Results.Ok(new RagSearchResponse
                {
                    Query = request.Query,
                    Memories = memories.Select(m => new RetrievedMemoryDto
                    {
                        MemoryId = m.MemoryId,
                        L2Summary = m.L2Summary,
                        L2OneLiner = m.L2OneLiner,
                        L1Context = m.L1Context,
                        L3Facts = m.L3Facts.ToList(),
                        L4Heuristics = m.L4Heuristics.ToList(),
                        Keywords = m.Keywords.ToList(),
                        Similarity = m.Similarity,
                        Importance = m.Importance,
                        CreatedAt = m.CreatedAt
                    }).ToList(),
                    Count = memories.Count
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "RAG Search Failed");
            }
        })
        .WithName("RagSearch")
        .WithDescription("Searches memories using L2 vector similarity without generating an answer")
        .Produces<RagSearchResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status500InternalServerError);

        // =============================================================================
        // GET /rag/history - Get recent RAG queries for the user
        // =============================================================================
        group.MapGet("/history", async (
            int? limit,
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var (tenantId, userId, _) = GetTenantContext(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var history = await conn.QueryAsync<RagQueryHistoryDto>(
                """
                SELECT id, query, answer, memory_ids, reasoning_trace, latency_ms, model_name, created_at
                FROM rag_query_results
                WHERE tenant_id = @TenantId AND user_id = @UserId
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new { TenantId = tenantId, UserId = userId, Limit = limit ?? 20 });

            return Results.Ok(new RagHistoryResponse
            {
                Queries = history.ToList(),
                Count = history.Count()
            });
        })
        .WithName("RagHistory")
        .WithDescription("Gets recent RAG query history for the current user")
        .Produces<RagHistoryResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // GET /rag/stats - Get RAG usage statistics for the tenant
        // =============================================================================
        group.MapGet("/stats", async (
            ClaimsPrincipal user,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @TenantId, false)",
                new { TenantId = tenantId.ToString() });

            var stats = await conn.QueryFirstOrDefaultAsync<RagStatsDto>(
                """
                SELECT
                    COUNT(*) AS total_queries,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '24 hours') AS queries_last_24h,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '7 days') AS queries_last_7d,
                    AVG(latency_ms)::INTEGER AS avg_latency_ms,
                    AVG(array_length(memory_ids, 1))::DECIMAL(4,2) AS avg_memories_per_query,
                    COUNT(DISTINCT user_id) AS unique_users
                FROM rag_query_results
                WHERE tenant_id = @TenantId
                """,
                new { TenantId = tenantId });

            var l2Stats = await conn.QueryFirstOrDefaultAsync<L2StatsDto>(
                """
                SELECT
                    COUNT(*) AS total_indexed,
                    COUNT(*) FILTER (WHERE created_at > NOW() - INTERVAL '24 hours') AS indexed_last_24h
                FROM memory_l2_embeddings
                WHERE tenant_id = @TenantId
                """,
                new { TenantId = tenantId });

            return Results.Ok(new RagStatsResponse
            {
                TotalQueries = stats?.total_queries ?? 0,
                QueriesLast24h = stats?.queries_last_24h ?? 0,
                QueriesLast7d = stats?.queries_last_7d ?? 0,
                AvgLatencyMs = stats?.avg_latency_ms ?? 0,
                AvgMemoriesPerQuery = stats?.avg_memories_per_query ?? 0,
                UniqueUsers = stats?.unique_users ?? 0,
                TotalIndexedMemories = l2Stats?.total_indexed ?? 0,
                IndexedLast24h = l2Stats?.indexed_last_24h ?? 0
            });
        })
        .WithName("RagStats")
        .WithDescription("Gets RAG usage statistics for the tenant")
        .Produces<RagStatsResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // =============================================================================
        // POST /rag/reindex - Reindex L2 embeddings for the tenant (admin only)
        // =============================================================================
        group.MapPost("/reindex", async (
            ClaimsPrincipal user,
            IL2EmbeddingService l2Service,
            CancellationToken ct) =>
        {
            var (tenantId, _, _) = GetTenantContext(user, selfHostedMode);

            var count = await l2Service.BulkReindexTenantAsync(tenantId, ct);

            return Results.Ok(new { message = "Reindex completed", memories_indexed = count });
        })
        .WithName("RagReindex")
        .WithDescription("Reindexes all L2 embeddings for the tenant (admin only)")
        .RequireAuthorization("Admin")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }

    private static (Guid TenantId, string UserId, string WorkspaceId) GetTenantContext(ClaimsPrincipal user, bool selfHosted)
    {
        if (selfHosted)
        {
            return (Guid.Parse("00000000-0000-0000-0000-000000000000"), "self-hosted", "default");
        }

        var tenantIdClaim = user.FindFirst("tenant_id")?.Value
            ?? throw new UnauthorizedAccessException("Missing tenant_id claim");

        if (!Guid.TryParse(tenantIdClaim, out var tenantId))
            throw new UnauthorizedAccessException("Invalid tenant_id claim");

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? throw new UnauthorizedAccessException("Missing user identifier");

        var workspaceId = user.FindFirst("workspace_id")?.Value ?? "default";

        return (tenantId, userId, workspaceId);
    }
}

// =============================================================================
// Request/Response DTOs
// =============================================================================

public sealed record RagAnswerRequest
{
    public string Query { get; init; } = default!;
    public int? MaxMemories { get; init; }
    public bool? IncludeL1 { get; init; }
    public bool? IncludeL3 { get; init; }
    public bool? IncludeL4 { get; init; }
    public decimal? SimilarityThreshold { get; init; }
    public float? Temperature { get; init; }
    public bool? IncludeReasoningTrace { get; init; }
}

public sealed class RagAnswerResponse
{
    public string Answer { get; init; } = default!;
    public Guid QueryId { get; init; }
    public List<RetrievedMemoryDto> Memories { get; init; } = [];
    public string? ReasoningTrace { get; init; }
    public string ModelName { get; init; } = default!;
    public int LatencyMs { get; init; }
}

public sealed record RagSearchRequest
{
    public string Query { get; init; } = default!;
    public int? Limit { get; init; }
}

public sealed class RagSearchResponse
{
    public string Query { get; init; } = default!;
    public List<RetrievedMemoryDto> Memories { get; init; } = [];
    public int Count { get; init; }
}

public sealed class RetrievedMemoryDto
{
    public Guid MemoryId { get; init; }
    public string L2Summary { get; init; } = default!;
    public string? L2OneLiner { get; init; }
    public string? L1Context { get; init; }
    public List<string> L3Facts { get; init; } = [];
    public List<string> L4Heuristics { get; init; } = [];
    public List<string> Keywords { get; init; } = [];
    public decimal Similarity { get; init; }
    public decimal Importance { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class RagHistoryResponse
{
    public List<RagQueryHistoryDto> Queries { get; init; } = [];
    public int Count { get; init; }
}

public sealed class RagQueryHistoryDto
{
    public Guid id { get; set; }
    public string query { get; set; } = "";
    public string answer { get; set; } = "";
    public Guid[] memory_ids { get; set; } = [];
    public string? reasoning_trace { get; set; }
    public int latency_ms { get; set; }
    public string model_name { get; set; } = "";
    public DateTimeOffset created_at { get; set; }
}

public sealed class RagStatsResponse
{
    public long TotalQueries { get; init; }
    public long QueriesLast24h { get; init; }
    public long QueriesLast7d { get; init; }
    public int AvgLatencyMs { get; init; }
    public decimal AvgMemoriesPerQuery { get; init; }
    public int UniqueUsers { get; init; }
    public long TotalIndexedMemories { get; init; }
    public long IndexedLast24h { get; init; }
}

// Internal DTOs for database queries
internal sealed class RagStatsDto
{
    public long total_queries { get; set; }
    public long queries_last_24h { get; set; }
    public long queries_last_7d { get; set; }
    public int avg_latency_ms { get; set; }
    public decimal avg_memories_per_query { get; set; }
    public int unique_users { get; set; }
}

internal sealed class L2StatsDto
{
    public long total_indexed { get; set; }
    public long indexed_last_24h { get; set; }
}
