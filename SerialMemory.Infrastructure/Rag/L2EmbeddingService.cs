using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Rag;

/// <summary>
/// Service for indexing L2 (summary) embeddings for RAG retrieval.
/// Extracts summary and keywords from L2 layer and stores vector embeddings.
/// </summary>
public sealed class L2EmbeddingService(
    NpgsqlDataSource dataSource,
    IEmbeddingService embeddingService,
    ILogger<L2EmbeddingService> logger)
    : IL2EmbeddingService
{
    /// <inheritdoc />
    public async Task<Guid> IndexL2Async(Guid tenantId, Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        // Set tenant context
        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = tenantId.ToString() });

        // Fetch L2 layer content
        var l2Content = await conn.QueryFirstOrDefaultAsync<L2LayerContent>(
            """
            SELECT content_json
            FROM memory_layers
            WHERE tenant_id = @TenantId
              AND memory_id = @MemoryId
              AND layer = 'L2_SUMMARY'
              AND is_current = TRUE
            """,
            new { TenantId = tenantId, MemoryId = memoryId });

        if (l2Content?.content_json == null)
        {
            logger.LogWarning("No L2 layer found for memory {MemoryId}, skipping embedding", memoryId);
            return Guid.Empty;
        }

        // Parse L2 content
        var (summary, keywords, importance) = ParseL2Content(l2Content.content_json);

        if (string.IsNullOrWhiteSpace(summary))
        {
            logger.LogWarning("L2 summary is empty for memory {MemoryId}, skipping embedding", memoryId);
            return Guid.Empty;
        }

        // Generate embedding for the summary
        var embedding = await embeddingService.EmbedTextAsync(summary, ct);

        // Upsert the L2 embedding
        var embeddingId = await UpsertEmbeddingAsync(
            conn,
            tenantId,
            memoryId,
            embedding,
            summary,
            keywords,
            importance);

        logger.LogDebug("Indexed L2 embedding for memory {MemoryId}, id: {EmbeddingId}", memoryId, embeddingId);

        return embeddingId;
    }

    /// <inheritdoc />
    public async Task<int> BulkReindexTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        // Set tenant context
        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = tenantId.ToString() });

        // Get all memories with L2 layers
        var memoryIds = (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT memory_id
            FROM memory_layers
            WHERE tenant_id = @TenantId
              AND layer = 'L2_SUMMARY'
              AND is_current = TRUE
            """,
            new { TenantId = tenantId })).ToList();

        logger.LogInformation("Bulk reindexing {Count} memories for tenant {TenantId}", memoryIds.Count, tenantId);

        var indexed = 0;
        foreach (var memoryId in memoryIds)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var embeddingId = await IndexL2Async(tenantId, memoryId, ct);
                if (embeddingId != Guid.Empty)
                    indexed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to index L2 for memory {MemoryId}", memoryId);
            }
        }

        logger.LogInformation("Bulk reindex complete: {Indexed}/{Total} memories indexed", indexed, memoryIds.Count);

        return indexed;
    }

    /// <inheritdoc />
    public async Task RemoveL2EmbeddingAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            DELETE FROM memory_l2_embeddings
            WHERE tenant_id = @TenantId AND memory_id = @MemoryId
            """,
            new { TenantId = tenantId, MemoryId = memoryId });

        logger.LogDebug("Removed L2 embedding for memory {MemoryId}", memoryId);
    }

    /// <inheritdoc />
    public async Task<bool> HasL2EmbeddingAsync(Guid tenantId, Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var exists = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM memory_l2_embeddings
                WHERE tenant_id = @TenantId AND memory_id = @MemoryId
            )
            """,
            new { TenantId = tenantId, MemoryId = memoryId });

        return exists;
    }

    /// <summary>
    /// Opens a connection with internal admin role for RLS bypass.
    /// </summary>
    private async Task<NpgsqlConnection> OpenInternalConnectionAsync(CancellationToken ct)
    {
        var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        return conn;
    }

    /// <summary>
    /// Parses L2 layer content JSON to extract summary, keywords, and importance.
    /// </summary>
    private static (string summary, string[] keywords, decimal importance) ParseL2Content(string contentJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;

            // Extract summary
            var summary = root.TryGetProperty("summary", out var summaryProp)
                ? summaryProp.GetString() ?? ""
                : "";

            // Extract keywords
            var keywords = Array.Empty<string>();
            if (root.TryGetProperty("keywords", out var keywordsProp) && keywordsProp.ValueKind == JsonValueKind.Array)
            {
                keywords = keywordsProp.EnumerateArray()
                    .Where(k => k.ValueKind == JsonValueKind.String)
                    .Select(k => k.GetString()!)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToArray();
            }

            // Extract importance score
            var importance = 0.5m;
            if (root.TryGetProperty("importance_score", out var importanceProp))
            {
                importance = importanceProp.TryGetDecimal(out var imp) ? imp : 0.5m;
            }

            return (summary, keywords, importance);
        }
        catch (JsonException)
        {
            return (contentJson, Array.Empty<string>(), 0.5m);
        }
    }

    /// <summary>
    /// Upserts an L2 embedding using raw SQL with pgvector parameter.
    /// </summary>
    private async Task<Guid> UpsertEmbeddingAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid memoryId,
        float[] embedding,
        string summary,
        string[] keywords,
        decimal importance)
    {
        const string sql = """
            INSERT INTO memory_l2_embeddings (tenant_id, memory_id, embedding, summary, keywords, importance)
            VALUES (@TenantId, @MemoryId, @Embedding, @Summary, @Keywords, @Importance)
            ON CONFLICT (tenant_id, memory_id)
            DO UPDATE SET
                embedding = EXCLUDED.embedding,
                summary = EXCLUDED.summary,
                keywords = EXCLUDED.keywords,
                importance = EXCLUDED.importance,
                updated_at = NOW()
            RETURNING id
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@MemoryId", memoryId);
        cmd.Parameters.AddWithValue("@Embedding", new Vector(embedding));
        cmd.Parameters.AddWithValue("@Summary", summary);
        cmd.Parameters.AddWithValue("@Keywords", keywords);
        cmd.Parameters.AddWithValue("@Importance", importance);

        var result = await cmd.ExecuteScalarAsync();
        return result is Guid id ? id : Guid.Empty;
    }

    private sealed class L2LayerContent
    {
        public string? content_json { get; set; }
    }
}
