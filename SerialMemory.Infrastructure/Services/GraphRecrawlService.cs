using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// Service for re-crawling memories to extract entities/relationships with new graph schema.
/// Supports batched processing, retry logic, and resumable jobs.
/// </summary>
public sealed class GraphRecrawlService(
    string connectionString,
    IEntityExtractionService entityExtractor,
    IKnowledgeGraphStore graphStore,
    ILogger<GraphRecrawlService> logger)
    : IGraphRecrawlService
{
    private readonly IKnowledgeGraphStore _graphStore = graphStore;

    public const int CurrentGraphVersion = 2;
    public const int DefaultBatchSize = 100;
    public const int MaxRetries = 3;

    /// <summary>
    /// Creates a new recrawl job for a tenant.
    /// </summary>
    public async Task<Guid> CreateRecrawlJobAsync(
        Guid tenantId,
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var jobId = await conn.QuerySingleAsync<Guid>(
            "SELECT create_recrawl_job(@TenantId, @TargetVersion, @BatchSize)",
            new { TenantId = tenantId, TargetVersion = CurrentGraphVersion, BatchSize = batchSize });

        logger.LogInformation(
            "Created recrawl job {JobId} for tenant {TenantId}, target version {Version}",
            jobId, tenantId, CurrentGraphVersion);

        return jobId;
    }

    /// <summary>
    /// Processes a recrawl job in batches.
    /// </summary>
    public async Task<RecrawlJobResult> ProcessRecrawlJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        // Get job details
        var job = await conn.QuerySingleOrDefaultAsync<RecrawlJob>(
            @"SELECT id, tenant_id AS TenantId, target_version AS TargetVersion,
                     total_memories AS TotalMemories, batch_size AS BatchSize,
                     status, processed_count AS ProcessedCount,
                     success_count AS SuccessCount, failure_count AS FailureCount
              FROM memory_recrawl_jobs WHERE id = @JobId",
            new { JobId = jobId });

        if (job == null)
            throw new InvalidOperationException($"Recrawl job {jobId} not found");

        if (job.Status is "completed" or "cancelled")
        {
            logger.LogWarning("Job {JobId} already {Status}", jobId, job.Status);
            return new RecrawlJobResult(
                jobId,
                job.Status,
                job.ProcessedCount,
                job.SuccessCount,
                job.FailureCount);
        }

        // Start the job
        await conn.ExecuteAsync("SELECT start_recrawl_job(@JobId)", new { JobId = jobId });
        logger.LogInformation("Started recrawl job {JobId}", jobId);

        var totalProcessed = 0;
        var successCount = 0;
        var failureCount = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Get batch of memories needing recrawl
                var memories = (await conn.QueryAsync<MemoryToRecrawl>(
                    @"SELECT memory_id AS MemoryId, content, current_version AS CurrentVersion
                      FROM get_memories_needing_recrawl(@TenantId, @TargetVersion, @BatchSize, @JobId)",
                    new
                    {
                        TenantId = job.TenantId,
                        TargetVersion = job.TargetVersion,
                        BatchSize = job.BatchSize,
                        JobId = jobId
                    })).ToList();

                if (memories.Count == 0)
                {
                    logger.LogInformation("No more memories to process for job {JobId}", jobId);
                    break;
                }

                logger.LogInformation(
                    "Processing batch of {Count} memories for job {JobId}",
                    memories.Count, jobId);

                foreach (var memory in memories)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var result = await ProcessSingleMemoryAsync(
                        conn, jobId, job.TenantId, memory, job.TargetVersion, cancellationToken);

                    totalProcessed++;
                    if (result.Success)
                        successCount++;
                    else
                        failureCount++;
                }
            }
        }
        finally
        {
            // Complete the job
            await conn.ExecuteAsync("SELECT complete_recrawl_job(@JobId)", new { JobId = jobId });
            logger.LogInformation(
                "Completed recrawl job {JobId}: {Success} succeeded, {Failed} failed",
                jobId, successCount, failureCount);
        }

        return new RecrawlJobResult(
            jobId,
            failureCount > 0 && successCount == 0 ? "failed" : "completed",
            totalProcessed,
            successCount,
            failureCount);
    }

    /// <summary>
    /// Processes a single memory with retry logic.
    /// </summary>
    private async Task<SingleMemoryResult> ProcessSingleMemoryAsync(
        NpgsqlConnection conn,
        Guid jobId,
        Guid tenantId,
        MemoryToRecrawl memory,
        int targetVersion,
        CancellationToken cancellationToken)
    {
        // Mark as started
        await conn.ExecuteAsync(
            "SELECT start_memory_recrawl(@JobId, @MemoryId)",
            new { JobId = jobId, MemoryId = memory.MemoryId });

        try
        {
            // Extract entities and relationships using software-aware extractor
            var (entities, relationships) = await entityExtractor.ExtractAllAsync(
                memory.Content, cancellationToken);

            // Store entities with new version (don't delete old ones)
            var entityCount = 0;
            var relationshipCount = 0;

            foreach (var extractedEntity in entities)
            {
                // Validate entity type
                var normalizedType = SoftwareGraphTypes.IsValidEntityType(extractedEntity.Label)
                    ? SoftwareGraphTypes.NormalizeEntityType(extractedEntity.Label)
                    : "CUSTOM";

                var entity = new Entity
                {
                    Id = Guid.CreateVersion7(),
                    Name = SoftwareGraphTypes.NormalizeEntityName(extractedEntity.Text),
                    EntityType = normalizedType,
                    CanonicalName = SoftwareGraphTypes.NormalizeEntityName(extractedEntity.Text).ToLowerInvariant(),
                    FirstSeenMemoryId = memory.MemoryId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["confidence"] = extractedEntity.Confidence,
                        ["graph_version"] = targetVersion,
                        ["start_offset"] = extractedEntity.Start,
                        ["end_offset"] = extractedEntity.End
                    }
                };

                // Upsert entity (may already exist from previous extraction)
                await UpsertEntityAsync(conn, tenantId, entity, memory.MemoryId, targetVersion);
                entityCount++;
            }

            // Store relationships with new version
            foreach (var extractedRel in relationships)
            {
                var normalizedType = SoftwareGraphTypes.IsValidRelationshipType(extractedRel.RelationType)
                    ? SoftwareGraphTypes.NormalizeRelationshipType(extractedRel.RelationType)
                    : "RELATED_TO";

                await UpsertRelationshipAsync(
                    conn, tenantId,
                    extractedRel.SourceEntity,
                    extractedRel.TargetEntity,
                    normalizedType,
                    targetVersion,
                    extractedRel.Confidence);
                relationshipCount++;
            }

            // Mark as completed
            await conn.ExecuteAsync(
                "SELECT complete_memory_recrawl(@JobId, @MemoryId, @EntityCount, @RelationshipCount, @TargetVersion)",
                new
                {
                    JobId = jobId,
                    MemoryId = memory.MemoryId,
                    EntityCount = entityCount,
                    RelationshipCount = relationshipCount,
                    TargetVersion = targetVersion
                });

            logger.LogDebug(
                "Processed memory {MemoryId}: {Entities} entities, {Relationships} relationships",
                memory.MemoryId, entityCount, relationshipCount);

            return new SingleMemoryResult { Success = true, EntityCount = entityCount, RelationshipCount = relationshipCount };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process memory {MemoryId}", memory.MemoryId);

            // Mark as failed
            await conn.ExecuteAsync(
                "SELECT fail_memory_recrawl(@JobId, @MemoryId, @ErrorMessage, @TargetVersion, @TenantId)",
                new
                {
                    JobId = jobId,
                    MemoryId = memory.MemoryId,
                    ErrorMessage = ex.Message,
                    TargetVersion = targetVersion,
                    TenantId = tenantId
                });

            return new SingleMemoryResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Upserts an entity and links it to a memory.
    /// </summary>
    private static async Task UpsertEntityAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Entity entity,
        Guid memoryId,
        int graphVersion)
    {
        // Upsert entity
        var entityId = await conn.QuerySingleAsync<Guid>(
            @"INSERT INTO entities (id, tenant_id, name, entity_type, canonical_name, first_seen_memory_id, metadata, graph_version)
              VALUES (@Id, @TenantId, @Name, @EntityType, @CanonicalName, @FirstSeenMemoryId, @Metadata::jsonb, @GraphVersion)
              ON CONFLICT (tenant_id, name, entity_type) DO UPDATE SET
                  canonical_name = COALESCE(entities.canonical_name, EXCLUDED.canonical_name),
                  graph_version = GREATEST(entities.graph_version, EXCLUDED.graph_version)
              RETURNING id",
            new
            {
                entity.Id,
                TenantId = tenantId,
                entity.Name,
                entity.EntityType,
                entity.CanonicalName,
                entity.FirstSeenMemoryId,
                Metadata = System.Text.Json.JsonSerializer.Serialize(entity.Metadata),
                GraphVersion = graphVersion
            });

        // Link to memory (with version)
        await conn.ExecuteAsync(
            @"INSERT INTO memory_entities (tenant_id, memory_id, entity_id, graph_version)
              VALUES (@TenantId, @MemoryId, @EntityId, @GraphVersion)
              ON CONFLICT (memory_id, entity_id) DO UPDATE SET
                  graph_version = GREATEST(memory_entities.graph_version, EXCLUDED.graph_version)",
            new { TenantId = tenantId, MemoryId = memoryId, EntityId = entityId, GraphVersion = graphVersion });
    }

    /// <summary>
    /// Upserts a relationship between entities.
    /// </summary>
    private static async Task UpsertRelationshipAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string sourceEntityName,
        string targetEntityName,
        string relationshipType,
        int graphVersion,
        float confidence)
    {
        // Find or create source entity
        var sourceId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            @"SELECT id FROM entities WHERE tenant_id = @TenantId AND name = @Name LIMIT 1",
            new { TenantId = tenantId, Name = sourceEntityName });

        if (sourceId == null)
        {
            sourceId = await conn.QuerySingleAsync<Guid>(
                @"INSERT INTO entities (id, tenant_id, name, entity_type, canonical_name, graph_version)
                  VALUES (@Id, @TenantId, @Name, 'CUSTOM', @CanonicalName, @GraphVersion)
                  RETURNING id",
                new
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Name = sourceEntityName,
                    CanonicalName = sourceEntityName.ToLowerInvariant(),
                    GraphVersion = graphVersion
                });
        }

        // Find or create target entity
        var targetId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            @"SELECT id FROM entities WHERE tenant_id = @TenantId AND name = @Name LIMIT 1",
            new { TenantId = tenantId, Name = targetEntityName });

        if (targetId == null)
        {
            targetId = await conn.QuerySingleAsync<Guid>(
                @"INSERT INTO entities (id, tenant_id, name, entity_type, canonical_name, graph_version)
                  VALUES (@Id, @TenantId, @Name, 'CUSTOM', @CanonicalName, @GraphVersion)
                  RETURNING id",
                new
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Name = targetEntityName,
                    CanonicalName = targetEntityName.ToLowerInvariant(),
                    GraphVersion = graphVersion
                });
        }

        // Upsert relationship
        await conn.ExecuteAsync(
            @"INSERT INTO entity_relationships (id, tenant_id, source_entity_id, target_entity_id, relationship_type, confidence, graph_version)
              VALUES (@Id, @TenantId, @SourceId, @TargetId, @RelationType, @Confidence, @GraphVersion)
              ON CONFLICT (tenant_id, source_entity_id, target_entity_id, relationship_type)
              DO UPDATE SET
                  confidence = GREATEST(entity_relationships.confidence, EXCLUDED.confidence),
                  graph_version = GREATEST(entity_relationships.graph_version, EXCLUDED.graph_version)",
            new
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                SourceId = sourceId.Value,
                TargetId = targetId.Value,
                RelationType = relationshipType,
                Confidence = (decimal)confidence,
                GraphVersion = graphVersion
            });
    }

    /// <summary>
    /// Gets statistics about recrawl progress for a tenant.
    /// </summary>
    public async Task<RecrawlStatistics> GetStatisticsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        var stats = await conn.QuerySingleAsync<RecrawlStatistics>(
            "SELECT * FROM get_recrawl_statistics(@TenantId)",
            new { TenantId = tenantId });

        return stats;
    }

    /// <summary>
    /// Retries failed memories from the dead letter table.
    /// </summary>
    public async Task<int> RetryFailedMemoriesAsync(
        Guid tenantId,
        int maxRetries = 1,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        // Get failed memories that can be retried
        var failedMemories = await conn.QueryAsync<Guid>(
            @"SELECT memory_id FROM recrawl_failures
              WHERE tenant_id = @TenantId
                AND retry_count < @MaxRetries
                AND target_version = @TargetVersion",
            new { TenantId = tenantId, MaxRetries = maxRetries + MaxRetries, TargetVersion = CurrentGraphVersion });

        var retried = 0;
        foreach (var memoryId in failedMemories)
        {
            // Clear from failures to allow retry
            await conn.ExecuteAsync(
                @"DELETE FROM recrawl_failures
                  WHERE tenant_id = @TenantId AND memory_id = @MemoryId",
                new { TenantId = tenantId, MemoryId = memoryId });

            // Reset memory's graph extraction error
            await conn.ExecuteAsync(
                @"UPDATE memories SET graph_extraction_error = NULL
                  WHERE id = @MemoryId",
                new { MemoryId = memoryId });

            retried++;
        }

        logger.LogInformation("Queued {Count} failed memories for retry", retried);
        return retried;
    }

    #region DTOs

    private sealed class RecrawlJob
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public int TargetVersion { get; set; }
        public int TotalMemories { get; set; }
        public int BatchSize { get; set; }
        public string Status { get; set; } = "";
        public int ProcessedCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
    }

    private sealed class MemoryToRecrawl
    {
        public Guid MemoryId { get; set; }
        public string Content { get; set; } = "";
        public int? CurrentVersion { get; set; }
    }

    private sealed class SingleMemoryResult
    {
        public bool Success { get; set; }
        public int EntityCount { get; set; }
        public int RelationshipCount { get; set; }
        public string? Error { get; set; }
    }

    #endregion
}

