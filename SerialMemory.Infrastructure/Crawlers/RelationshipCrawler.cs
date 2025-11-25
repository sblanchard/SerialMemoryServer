using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Crawlers;

/// <summary>
/// Crawls existing memories to extract and create entity relationships.
/// Runs as a background task to populate the knowledge graph.
/// </summary>
public sealed class RelationshipCrawler
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEntityExtractionService _extractionService;
    private readonly ILogger<RelationshipCrawler> _logger;

    public RelationshipCrawler(
        NpgsqlDataSource dataSource,
        IEntityExtractionService extractionService,
        ILogger<RelationshipCrawler> logger)
    {
        _dataSource = dataSource;
        _extractionService = extractionService;
        _logger = logger;
    }

    /// <summary>
    /// Crawl all unprocessed memories and extract relationships.
    /// </summary>
    public async Task<CrawlResult> CrawlAllAsync(
        CrawlOptions? options = null,
        IProgress<CrawlProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CrawlOptions();
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get memories that haven't been processed for relationships
        var sql = @"
            SELECT m.id, m.content, m.created_at
            FROM memories m
            WHERE NOT EXISTS (
                SELECT 1 FROM memory_entities me WHERE me.memory_id = m.id
            )
            OR @ForceReprocess = TRUE
            ORDER BY m.created_at DESC
            LIMIT @BatchSize";

        var memories = (await connection.QueryAsync<MemoryRow>(sql, new
        {
            ForceReprocess = options.ForceReprocess,
            BatchSize = options.BatchSize
        })).ToList();

        _logger.LogInformation("Found {Count} memories to process for relationship extraction", memories.Count);

        var totalEntities = 0;
        var totalRelationships = 0;
        var processedMemories = 0;
        var errors = new List<CrawlError>();

        foreach (var memory in memories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await ProcessMemoryAsync(connection, memory, cancellationToken);
                totalEntities += result.EntitiesCreated;
                totalRelationships += result.RelationshipsCreated;
                processedMemories++;

                progress?.Report(new CrawlProgress(
                    processedMemories,
                    memories.Count,
                    totalEntities,
                    totalRelationships));

                if (options.DelayBetweenMemories > TimeSpan.Zero)
                {
                    await Task.Delay(options.DelayBetweenMemories, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process memory {MemoryId}", memory.id);
                errors.Add(new CrawlError(memory.id, ex.Message));

                if (errors.Count >= options.MaxErrors)
                {
                    _logger.LogError("Max errors reached ({Count}), stopping crawl", errors.Count);
                    break;
                }
            }
        }

        stopwatch.Stop();

        _logger.LogInformation(
            "Crawl completed: {Processed} memories, {Entities} entities, {Relationships} relationships in {Duration}ms",
            processedMemories, totalEntities, totalRelationships, stopwatch.ElapsedMilliseconds);

        return new CrawlResult(
            processedMemories,
            totalEntities,
            totalRelationships,
            errors,
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Crawl a single memory and extract relationships.
    /// </summary>
    public async Task<MemoryProcessResult> ProcessMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = "SELECT id, content, created_at FROM memories WHERE id = @MemoryId";
        var memory = await connection.QuerySingleOrDefaultAsync<MemoryRow>(sql, new { MemoryId = memoryId });

        if (memory == null)
        {
            throw new InvalidOperationException($"Memory {memoryId} not found");
        }

        return await ProcessMemoryAsync(connection, memory, cancellationToken);
    }

    private async Task<MemoryProcessResult> ProcessMemoryAsync(
        NpgsqlConnection connection,
        MemoryRow memory,
        CancellationToken cancellationToken)
    {
        // Extract entities and relationships
        var (entities, relationships) = await _extractionService.ExtractAllAsync(memory.content, cancellationToken);

        var entitiesCreated = 0;
        var relationshipsCreated = 0;

        // Create or update entities
        var entityIdMap = new Dictionary<string, Guid>();

        foreach (var entity in entities)
        {
            var entityId = await CreateOrUpdateEntityAsync(
                connection,
                entity.Name,
                entity.Type,
                memory.id,
                entity.Confidence,
                cancellationToken);

            entityIdMap[entity.Name] = entityId;
            entitiesCreated++;

            // Link entity to memory
            await LinkMemoryToEntityAsync(connection, memory.id, entityId, entity.Confidence, cancellationToken);
        }

        // Create relationships between entities
        foreach (var rel in relationships)
        {
            // Ensure both entities exist
            if (!entityIdMap.TryGetValue(rel.SourceEntity, out var sourceId))
            {
                sourceId = await CreateOrUpdateEntityAsync(
                    connection,
                    rel.SourceEntity,
                    "UNKNOWN",
                    memory.id,
                    rel.Confidence,
                    cancellationToken);
                entityIdMap[rel.SourceEntity] = sourceId;
                entitiesCreated++;
            }

            if (!entityIdMap.TryGetValue(rel.TargetEntity, out var targetId))
            {
                targetId = await CreateOrUpdateEntityAsync(
                    connection,
                    rel.TargetEntity,
                    "UNKNOWN",
                    memory.id,
                    rel.Confidence,
                    cancellationToken);
                entityIdMap[rel.TargetEntity] = targetId;
                entitiesCreated++;
            }

            // Create the relationship
            await CreateRelationshipAsync(
                connection,
                sourceId,
                targetId,
                rel.RelationshipType,
                rel.Confidence,
                memory.id,
                cancellationToken);

            relationshipsCreated++;
        }

        // Also infer relationships based on co-occurrence in the same memory
        if (entities.Count >= 2)
        {
            var coOccurrenceRelationships = await InferCoOccurrenceRelationshipsAsync(
                connection,
                memory.id,
                entities,
                entityIdMap,
                cancellationToken);

            relationshipsCreated += coOccurrenceRelationships;
        }

        return new MemoryProcessResult(memory.id, entitiesCreated, relationshipsCreated);
    }

    private async Task<int> InferCoOccurrenceRelationshipsAsync(
        NpgsqlConnection connection,
        Guid memoryId,
        List<ExtractedEntity> entities,
        Dictionary<string, Guid> entityIdMap,
        CancellationToken cancellationToken)
    {
        var created = 0;

        // Create MENTIONED_WITH relationships for entities that appear together
        var personEntities = entities.Where(e => e.Type == "PERSON").ToList();
        var orgEntities = entities.Where(e => e.Type == "ORG").ToList();
        var locationEntities = entities.Where(e => e.Type == "GPE").ToList();

        // Person-Org co-occurrence suggests association
        foreach (var person in personEntities)
        {
            foreach (var org in orgEntities)
            {
                if (entityIdMap.TryGetValue(person.Name, out var personId) &&
                    entityIdMap.TryGetValue(org.Name, out var orgId))
                {
                    await CreateRelationshipAsync(
                        connection,
                        personId,
                        orgId,
                        "MENTIONED_WITH",
                        0.5f,
                        memoryId,
                        cancellationToken);
                    created++;
                }
            }
        }

        // Person-Location co-occurrence suggests association
        foreach (var person in personEntities)
        {
            foreach (var location in locationEntities)
            {
                if (entityIdMap.TryGetValue(person.Name, out var personId) &&
                    entityIdMap.TryGetValue(location.Name, out var locationId))
                {
                    await CreateRelationshipAsync(
                        connection,
                        personId,
                        locationId,
                        "MENTIONED_WITH",
                        0.4f,
                        memoryId,
                        cancellationToken);
                    created++;
                }
            }
        }

        // Person-Person co-occurrence suggests knowing each other
        for (int i = 0; i < personEntities.Count; i++)
        {
            for (int j = i + 1; j < personEntities.Count; j++)
            {
                if (entityIdMap.TryGetValue(personEntities[i].Name, out var id1) &&
                    entityIdMap.TryGetValue(personEntities[j].Name, out var id2))
                {
                    await CreateRelationshipAsync(
                        connection,
                        id1,
                        id2,
                        "MENTIONED_WITH",
                        0.3f,
                        memoryId,
                        cancellationToken);
                    created++;
                }
            }
        }

        return created;
    }

    private async Task<Guid> CreateOrUpdateEntityAsync(
        NpgsqlConnection connection,
        string name,
        string entityType,
        Guid sourceMemoryId,
        float confidence,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO entities (id, name, entity_type, canonical_name, first_seen_memory_id, metadata)
            VALUES (@Id, @Name, @EntityType, @CanonicalName, @SourceMemoryId, '{}'::jsonb)
            ON CONFLICT (name, entity_type) DO UPDATE SET
                metadata = entities.metadata
            RETURNING id";

        var id = Guid.CreateVersion7();
        var existingId = await connection.QuerySingleOrDefaultAsync<Guid?>(sql, new
        {
            Id = id,
            Name = name,
            EntityType = entityType,
            CanonicalName = name.ToLowerInvariant(),
            SourceMemoryId = sourceMemoryId
        });

        return existingId ?? id;
    }

    private async Task LinkMemoryToEntityAsync(
        NpgsqlConnection connection,
        Guid memoryId,
        Guid entityId,
        float relevance,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO memory_entities (memory_id, entity_id, relevance)
            VALUES (@MemoryId, @EntityId, @Relevance)
            ON CONFLICT (memory_id, entity_id) DO UPDATE SET
                relevance = GREATEST(memory_entities.relevance, EXCLUDED.relevance)";

        await connection.ExecuteAsync(sql, new
        {
            MemoryId = memoryId,
            EntityId = entityId,
            Relevance = relevance
        });
    }

    private async Task CreateRelationshipAsync(
        NpgsqlConnection connection,
        Guid sourceEntityId,
        Guid targetEntityId,
        string relationshipType,
        float confidence,
        Guid sourceMemoryId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO entity_relationships (
                id, source_entity_id, target_entity_id, relationship_type,
                confidence, first_seen_memory_id, metadata
            )
            VALUES (
                @Id, @SourceEntityId, @TargetEntityId, @RelationshipType,
                @Confidence, @SourceMemoryId, '{}'::jsonb
            )
            ON CONFLICT (source_entity_id, target_entity_id, relationship_type) DO UPDATE SET
                confidence = GREATEST(entity_relationships.confidence, EXCLUDED.confidence)";

        await connection.ExecuteAsync(sql, new
        {
            Id = Guid.CreateVersion7(),
            SourceEntityId = sourceEntityId,
            TargetEntityId = targetEntityId,
            RelationshipType = relationshipType,
            Confidence = confidence,
            SourceMemoryId = sourceMemoryId
        });
    }

    /// <summary>
    /// Get statistics about the current relationship graph.
    /// </summary>
    public async Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT
                (SELECT COUNT(*) FROM memories) as memory_count,
                (SELECT COUNT(*) FROM entities) as entity_count,
                (SELECT COUNT(*) FROM entity_relationships) as relationship_count,
                (SELECT COUNT(DISTINCT memory_id) FROM memory_entities) as memories_with_entities,
                (SELECT COUNT(*) FROM entity_relationships WHERE relationship_type != 'MENTIONED_WITH') as explicit_relationships,
                (SELECT COUNT(*) FROM entity_relationships WHERE relationship_type = 'MENTIONED_WITH') as inferred_relationships";

        var stats = await connection.QuerySingleAsync<dynamic>(sql);

        // Get relationship type breakdown
        var typesSql = @"
            SELECT relationship_type, COUNT(*) as count
            FROM entity_relationships
            GROUP BY relationship_type
            ORDER BY count DESC";

        var types = await connection.QueryAsync<(string relationship_type, int count)>(typesSql);

        return new GraphStatistics(
            (int)(long)stats.memory_count,
            (int)(long)stats.entity_count,
            (int)(long)stats.relationship_count,
            (int)(long)stats.memories_with_entities,
            (int)(long)stats.explicit_relationships,
            (int)(long)stats.inferred_relationships,
            types.ToDictionary(t => t.relationship_type, t => t.count));
    }

    private record MemoryRow(Guid id, string content, DateTime created_at);
}

public record CrawlOptions(
    int BatchSize = 1000,
    bool ForceReprocess = false,
    int MaxErrors = 100,
    TimeSpan DelayBetweenMemories = default);

public record CrawlProgress(
    int ProcessedMemories,
    int TotalMemories,
    int EntitiesCreated,
    int RelationshipsCreated);

public record CrawlResult(
    int MemoriesProcessed,
    int EntitiesCreated,
    int RelationshipsCreated,
    IReadOnlyList<CrawlError> Errors,
    TimeSpan Duration);

public record CrawlError(Guid MemoryId, string ErrorMessage);

public record MemoryProcessResult(
    Guid MemoryId,
    int EntitiesCreated,
    int RelationshipsCreated);

public record GraphStatistics(
    int TotalMemories,
    int TotalEntities,
    int TotalRelationships,
    int MemoriesWithEntities,
    int ExplicitRelationships,
    int InferredRelationships,
    IReadOnlyDictionary<string, int> RelationshipTypeBreakdown);
