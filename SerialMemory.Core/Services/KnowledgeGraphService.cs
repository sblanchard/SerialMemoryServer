using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Core.Services;

/// <summary>
/// Orchestration service for knowledge graph operations.
/// Coordinates embedding generation, entity extraction, and storage.
/// </summary>
public class KnowledgeGraphService(
    IKnowledgeGraphStore store,
    IEmbeddingService embeddingService,
    IEntityExtractionService entityExtractionService)
{
    private readonly IKnowledgeGraphStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IEmbeddingService _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
    private readonly IEntityExtractionService _entityExtractionService = entityExtractionService ?? throw new ArgumentNullException(nameof(entityExtractionService));

    #region Memory Operations

    /// <summary>
    /// Ingest a new memory into the knowledge graph with automatic entity extraction
    /// </summary>
    public async Task<MemoryIngestResult> IngestMemoryAsync(
        string content,
        string? source = null,
        Guid? sessionId = null,
        Dictionary<string, object>? metadata = null,
        bool extractEntities = true,
        string dedupMode = "warn",
        float dedupThreshold = 0.85f,
        string? memoryType = null,
        string? title = null,
        List<string>? facts = null,
        List<string>? concepts = null,
        List<string>? filesRead = null,
        List<string>? filesModified = null,
        CancellationToken cancellationToken = default)
    {
        // Strip <private> tags before any processing (P2 - GAP 6)
        content = StripPrivateTags(content);
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Content is empty after removing private tags");

        // Generate embedding
        var embedding = await _embeddingService.EmbedTextAsync(content, cancellationToken);

        // Ingest-time deduplication check
        List<DuplicateInfo>? similarMemories = null;
        if (dedupMode is not "off")
        {
            var candidates = await _store.SearchMemoriesByEmbeddingAsync(
                embedding, limit: 3, threshold: dedupThreshold, cancellationToken: cancellationToken);

            if (candidates.Count > 0)
            {
                var best = candidates[0];
                similarMemories = candidates.Select(c => new DuplicateInfo
                {
                    MemoryId = c.Id,
                    Similarity = c.Similarity,
                    ContentPreview = c.Content.Length > 120 ? c.Content[..120] + "..." : c.Content
                }).ToList();

                if (dedupMode == "skip")
                {
                    return new MemoryIngestResult
                    {
                        MemoryId = best.Id,
                        DuplicateDetected = true,
                        DuplicateOf = best.Id,
                        DuplicateSimilarity = best.Similarity,
                        SimilarMemories = similarMemories
                    };
                }

                if (dedupMode == "append")
                {
                    var merged = best.Content + "\n---\n" + content;
                    // Reuse the new content's embedding instead of re-embedding the entire merged text.
                    // This avoids a second embedding API call. Any drift is corrected by reembed_memories.

                    // Preserve existing metadata/session — only override if caller provided new values
                    var mergedMetadata = best.Metadata ?? new Dictionary<string, object>();
                    if (metadata != null)
                    {
                        foreach (var kvp in metadata)
                            mergedMetadata[kvp.Key] = kvp.Value;
                    }

                    var updatedMemory = new Memory
                    {
                        Id = best.Id,
                        Content = merged,
                        Embedding = embedding,
                        Source = source ?? best.Source,
                        ConversationSessionId = sessionId ?? best.ConversationSessionId,
                        Metadata = mergedMetadata
                    };
                    await _store.UpdateMemoryAsync(updatedMemory, cancellationToken);

                    return new MemoryIngestResult
                    {
                        MemoryId = best.Id,
                        DuplicateDetected = true,
                        DuplicateOf = best.Id,
                        DuplicateSimilarity = best.Similarity,
                        SimilarMemories = similarMemories
                    };
                }

                // dedupMode == "warn": fall through to normal creation
            }
        }

        // Resolve memory type: explicit param > metadata > default
        var resolvedMemoryType = memoryType
            ?? (metadata?.TryGetValue("memory_type", out var mtVal) == true ? mtVal?.ToString() : null)
            ?? "knowledge";

        // Create memory with structured observation fields
        var memory = new Memory
        {
            Content = content,
            Embedding = embedding,
            Source = source,
            ConversationSessionId = sessionId,
            Metadata = metadata,
            MemoryType = resolvedMemoryType,
            Title = title ?? ExtractTitleFromContent(content),
            Facts = facts,
            Concepts = concepts,
            FilesRead = filesRead,
            FilesModified = filesModified
        };

        // Extract entities/relationships in parallel with entity extraction (before UoW)
        Task<(List<ExtractedEntity>, List<ExtractedRelationship>)>? extractionTask = null;
        if (extractEntities)
            extractionTask = _entityExtractionService.ExtractAllAsync(content, cancellationToken);

        // Unit-of-work: reuse one connection (with RLS set once) for all store writes
        await using var uow = await _store.BeginUnitOfWorkAsync(cancellationToken);

        var memoryId = await _store.CreateMemoryAsync(memory, cancellationToken);

        var result = new MemoryIngestResult
        {
            MemoryId = memoryId,
            EntitiesCreated = 0,
            RelationshipsCreated = 0,
            Entities = [],
            Relationships = [],
            DuplicateDetected = similarMemories is { Count: > 0 },
            DuplicateSimilarity = similarMemories?.FirstOrDefault()?.Similarity ?? 0f,
            SimilarMemories = similarMemories
        };

        if (extractionTask == null) return result;

        // Await entity extraction (may already be done if extraction was faster than CreateMemory)
        var (extractedEntities, extractedRelationships) = await extractionTask;

        // Batch-create all entities in a single roundtrip
        var entitiesToCreate = extractedEntities.Select(e => new Entity
        {
            Name = e.Text,
            EntityType = e.Label,
            CanonicalName = e.Text.ToLowerInvariant(),
            FirstSeenMemoryId = memoryId,
            Metadata = new Dictionary<string, object>
            {
                ["confidence"] = e.Confidence,
                ["start"] = e.Start,
                ["end"] = e.End
            }
        }).ToList();

        var entityIdMap = await _store.CreateEntitiesBatchAsync(entitiesToCreate, cancellationToken);

        // Batch-link all entities to memory in a single roundtrip
        var entityRelevances = new Dictionary<Guid, float>();
        foreach (var extracted in extractedEntities)
        {
            if (entityIdMap.TryGetValue(extracted.Text, out var entityId))
            {
                entityRelevances[entityId] = extracted.Confidence;
                result.Entities.Add(new EntityInfo
                {
                    Id = entityId,
                    Name = extracted.Text,
                    Type = extracted.Label,
                    Confidence = extracted.Confidence
                });
                result.EntitiesCreated++;
            }
        }
        await _store.LinkMemoryToEntitiesBatchAsync(memoryId, entityRelevances, cancellationToken);

        // Batch-create all relationships in a single roundtrip
        var relationshipsToCreate = new List<EntityRelationship>();
        foreach (var extracted in extractedRelationships)
        {
            if (!entityIdMap.TryGetValue(extracted.SourceEntity, out var sourceId) ||
                !entityIdMap.TryGetValue(extracted.TargetEntity, out var targetId))
            {
                continue;
            }

            relationshipsToCreate.Add(new EntityRelationship
            {
                SourceEntityId = sourceId,
                TargetEntityId = targetId,
                RelationshipType = extracted.RelationType,
                Confidence = extracted.Confidence,
                FirstSeenMemoryId = memoryId
            });

            result.Relationships.Add(new RelationshipInfo
            {
                SourceId = sourceId,
                TargetId = targetId,
                Source = extracted.SourceEntity,
                Target = extracted.TargetEntity,
                Type = extracted.RelationType,
                Confidence = extracted.Confidence
            });
            result.RelationshipsCreated++;
        }
        await _store.CreateRelationshipsBatchAsync(relationshipsToCreate, cancellationToken);

        return result;
    }

    /// <summary>
    /// Get recent memories ordered by creation date
    /// </summary>
    public async Task<List<MemorySearchResult>> GetRecentMemoriesAsync(
        int limit = 10,
        bool includeEntities = true,
        CancellationToken cancellationToken = default)
    {
        var memories = await _store.GetRecentMemoriesAsync(limit, cancellationToken);

        var results = memories.Select(memory => new MemorySearchResult
        {
            Id = memory.Id,
            Content = memory.Content,
            CreatedAt = memory.CreatedAt,
            Source = memory.Source,
            Entities = []
        }).ToList();

        if (includeEntities && results.Count > 0)
        {
            var memoryIds = results.Select(r => r.Id).ToList();
            var entitiesByMemory = await _store.GetEntitiesForMemoriesAsync(memoryIds, cancellationToken);

            foreach (var result in results)
            {
                if (entitiesByMemory.TryGetValue(result.Id, out var entities))
                {
                    result.Entities = entities.Select(e => new EntityInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Type = e.EntityType
                    }).ToList();
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Search memories using semantic, text, or hybrid search
    /// </summary>
    public async Task<List<MemorySearchResult>> SearchMemoriesAsync(
        string query,
        SearchMode mode = SearchMode.Hybrid,
        int limit = 10,
        float threshold = 0.7f,
        bool includeEntities = true,
        string? memoryType = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<MemorySearchResult>();
        var seenIds = new HashSet<Guid>();

        if (mode == SearchMode.Semantic || mode == SearchMode.Hybrid)
        {
            var queryEmbedding = await _embeddingService.EmbedTextAsync(query, cancellationToken);
            var semanticResults = await _store.SearchMemoriesByEmbeddingAsync(queryEmbedding, limit, threshold, memoryType, cancellationToken);

            foreach (var memory in semanticResults)
            {
                seenIds.Add(memory.Id);
                results.Add(new MemorySearchResult
                {
                    Id = memory.Id,
                    Content = memory.Content,
                    CreatedAt = memory.CreatedAt,
                    Source = memory.Source,
                    Similarity = memory.Similarity,
                    Entities = []
                });
            }
        }

        // In hybrid mode, skip text search if semantic already found enough high-quality results
        var skipTextSearch = mode == SearchMode.Hybrid
            && results.Count >= limit
            && results.All(r => r.Similarity > 0.6f);

        if ((mode == SearchMode.Text || mode == SearchMode.Hybrid) && !skipTextSearch)
        {
            var textResults = await _store.SearchMemoriesByTextAsync(query, limit, memoryType, cancellationToken);

            foreach (var memory in textResults)
            {
                if (!seenIds.Add(memory.Id)) continue;

                results.Add(new MemorySearchResult
                {
                    Id = memory.Id,
                    Content = memory.Content,
                    CreatedAt = memory.CreatedAt,
                    Source = memory.Source,
                    Rank = memory.Rank,
                    Entities = []
                });
            }
        }

        var trimmed = results.Take(limit).ToList();

        if (includeEntities && trimmed.Count > 0)
        {
            var memoryIds = trimmed.Select(r => r.Id).ToList();
            var entitiesByMemory = await _store.GetEntitiesForMemoriesAsync(memoryIds, cancellationToken);

            foreach (var result in trimmed)
            {
                if (entitiesByMemory.TryGetValue(result.Id, out var entities))
                {
                    result.Entities = entities.Select(e => new EntityInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Type = e.EntityType
                    }).ToList();
                }
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Multi-hop search through the knowledge graph
    /// </summary>
    public async Task<MultiHopSearchResult> MultiHopSearchAsync(
        string startQuery,
        int hops = 2,
        int maxResultsPerHop = 5,
        CancellationToken cancellationToken = default)
    {
        var result = new MultiHopSearchResult
        {
            Hops = hops,
            Memories = [],
            Entities = [],
            Relationships = []
        };

        var visitedMemoryIds = new HashSet<Guid>();
        var visitedEntityIds = new HashSet<Guid>();
        var processedEntityIds = new HashSet<Guid>(); // Track entities whose relationships have been fetched
        var visitedRelationships = new HashSet<(Guid, Guid, string)>(); // Track (source, target, type) to avoid duplicates

        // Initial search
        var initialResults = await SearchMemoriesAsync(
            startQuery,
            SearchMode.Hybrid,
            maxResultsPerHop,
            0.5f,
            true,
            cancellationToken: cancellationToken);

        foreach (var memory in initialResults)
        {
            if (visitedMemoryIds.Add(memory.Id))
            {
                result.Memories.Add(memory);

                foreach (var entity in memory.Entities)
                {
                    if (visitedEntityIds.Add(entity.Id))
                    {
                        result.Entities.Add(entity);
                    }
                }
            }
        }

        // Traverse hops - batch-load relationships for frontier entities
        for (int hop = 0; hop < hops; hop++)
        {
            var frontierEntities = result.Entities
                .Where(e => !processedEntityIds.Contains(e.Id))
                .ToList();

            if (frontierEntities.Count == 0)
                break;

            var frontierIds = frontierEntities.Select(e => e.Id).ToList();
            foreach (var id in frontierIds)
                processedEntityIds.Add(id);

            // Batch-load all relationships for the frontier in one query
            var relationshipsByEntity = await _store.GetRelationshipsForEntitiesAsync(frontierIds, cancellationToken);

            var newEntityIds = new List<Guid>();

            foreach (var entity in frontierEntities)
            {
                if (!relationshipsByEntity.TryGetValue(entity.Id, out var relationships))
                    continue;

                foreach (var rel in relationships.Take(maxResultsPerHop))
                {
                    var relationshipKey = (rel.SourceEntityId, rel.TargetEntityId, rel.RelationshipType);
                    if (!visitedRelationships.Add(relationshipKey))
                        continue;

                    result.Relationships.Add(new RelationshipInfo
                    {
                        SourceId = rel.SourceEntityId,
                        TargetId = rel.TargetEntityId,
                        Source = rel.SourceEntity?.Name ?? entity.Name,
                        Target = rel.TargetEntity?.Name ?? "Unknown",
                        Type = rel.RelationshipType,
                        Confidence = rel.Confidence
                    });

                    var connectedEntityId = rel.SourceEntityId == entity.Id
                        ? rel.TargetEntityId
                        : rel.SourceEntityId;

                    if (visitedEntityIds.Add(connectedEntityId))
                    {
                        // Try to get entity info from the relationship's navigation properties
                        var connectedEntityInfo = rel.SourceEntityId == entity.Id
                            ? rel.TargetEntity
                            : rel.SourceEntity;

                        if (connectedEntityInfo != null)
                        {
                            result.Entities.Add(new EntityInfo
                            {
                                Id = connectedEntityInfo.Id,
                                Name = connectedEntityInfo.Name,
                                Type = connectedEntityInfo.EntityType
                            });
                        }
                        else
                        {
                            newEntityIds.Add(connectedEntityId);
                        }
                    }
                }
            }

            // Batch-load missing entities in one query instead of N+1 individual lookups
            if (newEntityIds.Count > 0)
            {
                var fetchedEntities = await _store.GetEntitiesByIdsAsync(newEntityIds, cancellationToken);

                foreach (var connectedEntity in fetchedEntities)
                {
                    result.Entities.Add(new EntityInfo
                    {
                        Id = connectedEntity.Id,
                        Name = connectedEntity.Name,
                        Type = connectedEntity.EntityType
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Get context from the previous day to instantiate a new session with continuity.
    /// Filters by project/subject using server-side pgvector similarity if provided.
    /// </summary>
    public async Task<PreviousDayContext> GetPreviousDayContextAsync(
        string? projectOrSubject = null,
        int daysBack = 1,
        int limit = 50,
        bool includeEntities = true,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var toUtc = now;
        var fromUtc = now.Date.AddDays(-daysBack);

        List<Memory> memories;

        if (!string.IsNullOrWhiteSpace(projectOrSubject))
        {
            var queryEmbedding = await _embeddingService.EmbedTextAsync(projectOrSubject, cancellationToken);

            // Use server-side pgvector similarity instead of fetching 1000 memories client-side
            var recentMemories = await _store.SearchMemoriesByEmbeddingInDateRangeAsync(
                queryEmbedding, fromUtc, toUtc, threshold: 0.3f, limit, cancellationToken);

            if (recentMemories.Count == 0)
            {
                var olderMemories = await _store.SearchMemoriesByEmbeddingAsync(
                    queryEmbedding, limit, threshold: 0.5f, cancellationToken: cancellationToken);
                memories = olderMemories.Where(m => m.CreatedAt < fromUtc).ToList();
            }
            else
            {
                // Also fetch older context memories (up to 30% of limit)
                var contextLimit = Math.Max(3, limit / 3);
                var olderMemories = await _store.SearchMemoriesByEmbeddingAsync(
                    queryEmbedding, contextLimit * 2, threshold: 0.5f, cancellationToken: cancellationToken);
                var olderContextMemories = olderMemories
                    .Where(m => m.CreatedAt < fromUtc)
                    .Take(contextLimit)
                    .ToList();

                memories = [..recentMemories, ..olderContextMemories];
            }
        }
        else
        {
            memories = await _store.GetMemoriesByDateRangeAsync(fromUtc, toUtc, limit, cancellationToken);
        }

        var recentCount = memories.Count(m => m.CreatedAt >= fromUtc && m.CreatedAt <= toUtc);
        var contextCount = memories.Count - recentCount;

        var result = new PreviousDayContext
        {
            FromDate = fromUtc,
            ToDate = toUtc,
            ProjectOrSubject = projectOrSubject,
            MemoryCount = memories.Count,
            RecentMemoryCount = recentCount,
            ContextMemoryCount = contextCount,
            Memories = [],
            TopEntities = [],
            TopRelationships = [],
            SessionSummary = ""
        };

        if (memories.Count == 0)
        {
            result.SessionSummary = string.IsNullOrWhiteSpace(projectOrSubject)
                ? "No memories found from the previous session."
                : $"No memories found for '{projectOrSubject}' from the previous session.";
            return result;
        }

        // Build memory results
        result.Memories = memories.Select(memory => new MemorySearchResult
        {
            Id = memory.Id,
            Content = memory.Content,
            CreatedAt = memory.CreatedAt,
            Source = memory.Source,
            Similarity = memory.Similarity,
            Entities = []
        }).ToList();

        // Batch-load entities and relationships instead of N+1
        if (includeEntities)
        {
            var memoryIds = memories.Select(m => m.Id).ToList();
            var entitiesByMemory = await _store.GetEntitiesForMemoriesAsync(memoryIds, cancellationToken);

            var entityCounts = new Dictionary<string, (EntityInfo Entity, int Count)>();
            var allEntityIds = new HashSet<Guid>();

            foreach (var memoryResult in result.Memories)
            {
                if (entitiesByMemory.TryGetValue(memoryResult.Id, out var entities))
                {
                    memoryResult.Entities = entities.Select(e => new EntityInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Type = e.EntityType
                    }).ToList();

                    foreach (var entity in memoryResult.Entities)
                    {
                        allEntityIds.Add(entity.Id);
                        var key = $"{entity.Type}:{entity.Name}";
                        if (entityCounts.TryGetValue(key, out var existing))
                        {
                            entityCounts[key] = (existing.Entity, existing.Count + 1);
                        }
                        else
                        {
                            entityCounts[key] = (entity, 1);
                        }
                    }
                }
            }

            // Batch-load all relationships for all entities in one query
            var relationshipCounts = new Dictionary<string, (RelationshipInfo Relationship, int Count)>();
            if (allEntityIds.Count > 0)
            {
                var relationshipsByEntity = await _store.GetRelationshipsForEntitiesAsync(
                    allEntityIds.ToList(), cancellationToken);

                var seenRelationships = new HashSet<string>();
                foreach (var (_, rels) in relationshipsByEntity)
                {
                    foreach (var rel in rels)
                    {
                        var relInfo = new RelationshipInfo
                        {
                            SourceId = rel.SourceEntityId,
                            TargetId = rel.TargetEntityId,
                            Source = rel.SourceEntity?.Name ?? "Unknown",
                            Target = rel.TargetEntity?.Name ?? "Unknown",
                            Type = rel.RelationshipType,
                            Confidence = rel.Confidence
                        };

                        var key = $"{relInfo.Source}->{relInfo.Type}->{relInfo.Target}";
                        if (!seenRelationships.Add(key)) continue;

                        if (relationshipCounts.TryGetValue(key, out var existingRel))
                        {
                            relationshipCounts[key] = (existingRel.Relationship, existingRel.Count + 1);
                        }
                        else
                        {
                            relationshipCounts[key] = (relInfo, 1);
                        }
                    }
                }
            }

            result.TopEntities = entityCounts.Values
                .OrderByDescending(x => x.Count)
                .Take(10)
                .Select(x => x.Entity)
                .ToList();

            result.TopRelationships = relationshipCounts.Values
                .OrderByDescending(x => x.Count)
                .Take(10)
                .Select(x => x.Relationship)
                .ToList();
        }

        // Generate summary
        var entityTypes = result.TopEntities
            .GroupBy(e => e.Type)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();

        var topics = result.TopEntities
            .Where(e => e.Type is "PROJECT" or "CONCEPT" or "TECHNOLOGY" or "PRODUCT")
            .Select(e => e.Name)
            .Take(5)
            .ToList();

        var people = result.TopEntities
            .Where(e => e.Type == "PERSON")
            .Select(e => e.Name)
            .Take(3)
            .ToList();

        var subjectClause = string.IsNullOrWhiteSpace(projectOrSubject)
            ? ""
            : $" related to '{projectOrSubject}'";

        if (contextCount > 0)
        {
            result.SessionSummary = $"Found {recentCount} recent memories{subjectClause} from {fromUtc:yyyy-MM-dd} to {toUtc:yyyy-MM-dd}, plus {contextCount} older contextual memories for background.";
        }
        else
        {
            result.SessionSummary = $"Found {memories.Count} memories{subjectClause} from {fromUtc:yyyy-MM-dd} to {toUtc:yyyy-MM-dd}.";
        }

        if (topics.Count > 0)
            result.SessionSummary += $" Main topics: {string.Join(", ", topics)}.";

        if (people.Count > 0)
            result.SessionSummary += $" People mentioned: {string.Join(", ", people)}.";

        if (entityTypes.Count > 0)
            result.SessionSummary += $" Entity breakdown: {string.Join(", ", entityTypes)}.";

        return result;
    }

    #endregion

    #region Progressive Disclosure (P0 - GAP 2)

    /// <summary>
    /// Search memories returning only compact index results (ID, timestamp, type, title, similarity, entity count).
    /// ~50-80 tokens per result vs ~500+ for full content. Use memory_fetch to get full details.
    /// </summary>
    public async Task<CompactSearchResult> SearchMemoriesCompactAsync(
        string query,
        SearchMode mode = SearchMode.Hybrid,
        int limit = 20,
        float threshold = 0.5f,
        string? memoryType = null,
        CancellationToken cancellationToken = default)
    {
        var results = await SearchMemoriesAsync(query, mode, limit, threshold, true, memoryType, cancellationToken);

        var compactResults = results.Select(r => new CompactMemoryEntry
        {
            Id = r.Id,
            CreatedAt = r.CreatedAt,
            MemoryType = r.Source ?? "knowledge",
            Title = ExtractTitleFromContent(r.Content),
            Similarity = r.Similarity > 0 ? r.Similarity : r.Rank,
            EntityCount = r.Entities.Count,
            ContentLength = r.Content.Length,
            EstimatedTokens = EstimateTokens(r.Content)
        }).ToList();

        var totalFullTokens = compactResults.Sum(r => r.EstimatedTokens);
        var indexTokens = compactResults.Sum(r => EstimateTokens($"{r.Id} {r.CreatedAt:O} {r.Title} {r.Similarity:F3}"));

        return new CompactSearchResult
        {
            Results = compactResults,
            TotalResults = compactResults.Count,
            Meta = new SearchMeta
            {
                IndexTokens = indexTokens,
                FullFetchTokens = totalFullTokens,
                TokenSavingsPercent = totalFullTokens > 0 ? (int)((1.0 - (float)indexTokens / totalFullTokens) * 100) : 0
            }
        };
    }

    /// <summary>
    /// Batch-fetch full memory details by IDs. Step 3 of progressive disclosure.
    /// </summary>
    public async Task<List<MemorySearchResult>> FetchMemoriesByIdsAsync(
        List<Guid> ids,
        bool includeEntities = true,
        CancellationToken cancellationToken = default)
    {
        var memories = await _store.GetMemoriesByIdsAsync(ids, cancellationToken);

        var results = memories.Select(m => new MemorySearchResult
        {
            Id = m.Id,
            Content = m.Content,
            CreatedAt = m.CreatedAt,
            Source = m.Source,
            MemoryType = m.MemoryType,
            Title = m.Title,
            Facts = m.Facts,
            Concepts = m.Concepts,
            FilesRead = m.FilesRead,
            FilesModified = m.FilesModified,
            EstimatedTokens = EstimateTokens(m.Content),
            Entities = []
        }).ToList();

        if (includeEntities && results.Count > 0)
        {
            var memoryIds = results.Select(r => r.Id).ToList();
            var entitiesByMemory = await _store.GetEntitiesForMemoriesAsync(memoryIds, cancellationToken);
            foreach (var result in results)
            {
                if (entitiesByMemory.TryGetValue(result.Id, out var entities))
                {
                    result.Entities = entities.Select(e => new EntityInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Type = e.EntityType
                    }).ToList();
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Get chronological timeline around an anchor point.
    /// </summary>
    public async Task<TimelineResult> GetTimelineAsync(
        Guid? anchorId = null,
        DateTime? anchorTime = null,
        int before = 5,
        int after = 5,
        string? memoryType = null,
        CancellationToken cancellationToken = default)
    {
        List<Memory> memories;

        if (anchorId.HasValue)
        {
            memories = await _store.GetMemoriesAroundAnchorAsync(anchorId.Value, before, after, memoryType, cancellationToken);
        }
        else if (anchorTime.HasValue)
        {
            memories = await _store.GetMemoriesAroundTimestampAsync(anchorTime.Value, before, after, memoryType, cancellationToken);
        }
        else
        {
            memories = await _store.GetRecentMemoriesAsync(before + after, cancellationToken);
        }

        return new TimelineResult
        {
            AnchorId = anchorId,
            AnchorTime = anchorTime ?? memories.FirstOrDefault()?.CreatedAt,
            Entries = memories.Select(m => new CompactMemoryEntry
            {
                Id = m.Id,
                CreatedAt = m.CreatedAt,
                MemoryType = m.MemoryType,
                Title = m.Title ?? ExtractTitleFromContent(m.Content),
                Similarity = 0,
                EntityCount = 0,
                ContentLength = m.Content.Length,
                EstimatedTokens = EstimateTokens(m.Content)
            }).ToList(),
            TotalEntries = memories.Count
        };
    }

    #endregion

    #region Privacy Tag Support (P2 - GAP 6)

    /// <summary>
    /// Strip &lt;private&gt;...&lt;/private&gt; tags from content before storage.
    /// </summary>
    public static string StripPrivateTags(string content)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            content,
            @"<private>.*?</private>",
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Trim();
    }

    #endregion

    #region Helpers

    private static string ExtractTitleFromContent(string content)
    {
        var newlinePos = content.IndexOf('\n');
        if (newlinePos > 0 && newlinePos <= 120)
            return content[..newlinePos].Trim();
        return content.Length > 100 ? content[..100].Trim() + "..." : content.Trim();
    }

    /// <summary>
    /// Estimate token count from character count (rough: chars / 4).
    /// </summary>
    public static int EstimateTokens(string text)
    {
        return (text.Length + 3) / 4;
    }

    #endregion

    #region User Persona Operations

    /// <summary>
    /// Get user persona information
    /// </summary>
    public async Task<Dictionary<string, Dictionary<string, object>>> GetUserPersonaAsync(
        string userId = "default_user",
        CancellationToken cancellationToken = default)
    {
        return await _store.GetUserPersonaAsync(userId, cancellationToken);
    }

    /// <summary>
    /// Set a user persona attribute
    /// </summary>
    public async Task SetUserPersonaAttributeAsync(
        string attributeType,
        string attributeKey,
        string attributeValue,
        float confidence = 1.0f,
        string userId = "default_user",
        Guid? sourceMemoryId = null,
        CancellationToken cancellationToken = default)
    {
        var persona = new UserPersona
        {
            UserId = userId,
            AttributeType = attributeType,
            AttributeKey = attributeKey,
            AttributeValue = attributeValue,
            Confidence = confidence,
            SourceMemoryId = sourceMemoryId
        };

        await _store.SetUserPersonaAttributeAsync(persona, cancellationToken);
    }

    /// <summary>
    /// Get active goals (stored as user_personas with attribute_type='goal' and confidence > 0)
    /// </summary>
    public async Task<List<UserPersona>> GetActiveGoalsAsync(
        string userId = "default_user",
        CancellationToken cancellationToken = default)
    {
        return await _store.GetActiveGoalsAsync(userId, cancellationToken);
    }

    public async Task<List<Memory>> GetMemoriesByTypeAsync(
        string memoryType,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await _store.GetMemoriesByTypeAsync(memoryType, limit, cancellationToken);
    }

    public async Task<List<Memory>> GetMemoriesBySessionAsync(
        Guid sessionId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return await _store.GetMemoriesBySessionAsync(sessionId, limit, cancellationToken);
    }

    public async Task<List<Memory>> GetMemoriesByDateRangeAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return await _store.GetMemoriesByDateRangeAsync(fromUtc, toUtc, limit, cancellationToken);
    }

    /// <summary>
    /// Set a goal (stored as user persona with attribute_type='goal', confidence=priority)
    /// </summary>
    public async Task SetGoalAsync(
        string key,
        string description,
        float priority = 1.0f,
        string userId = "default_user",
        CancellationToken cancellationToken = default)
    {
        await SetUserPersonaAttributeAsync("goal", key, description, priority, userId, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Complete a goal (sets confidence=0 and marks value as completed, preserving history)
    /// </summary>
    public async Task CompleteGoalAsync(
        string key,
        string userId = "default_user",
        CancellationToken cancellationToken = default)
    {
        await SetUserPersonaAttributeAsync("goal", key, "[COMPLETED]", 0f, userId, cancellationToken: cancellationToken);
    }

    #endregion

    #region Session Operations

    /// <summary>
    /// Create a new conversation session
    /// </summary>
    public async Task<Guid> CreateConversationSessionAsync(
        string? sessionName = null,
        string? clientType = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var session = new ConversationSession
        {
            SessionName = sessionName,
            ClientType = clientType,
            Metadata = metadata
        };

        return await _store.CreateConversationSessionAsync(session, cancellationToken);
    }

    /// <summary>
    /// End a conversation session
    /// </summary>
    public async Task EndConversationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _store.EndConversationSessionAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// Get recent conversation sessions
    /// </summary>
    public async Task<List<ConversationSession>> GetRecentSessionsAsync(
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return await _store.GetRecentSessionsAsync(limit, cancellationToken);
    }

    #endregion

    #region Import Operations

    /// <summary>
    /// Import memories from CORE MCP export format
    /// </summary>
    public async Task<CoreImportResult> ImportFromCoreAsync(
        CoreExportData coreData,
        string? source = "core-import",
        CancellationToken cancellationToken = default)
    {
        var result = new CoreImportResult
        {
            EntitiesImported = 0,
            RelationsImported = 0,
            ObservationsImported = 0,
            Errors = []
        };

        // 1. Batch-create all entities in one roundtrip
        var entitiesToCreate = coreData.Entities.Select(ce => new Entity
        {
            Name = ce.Name,
            EntityType = ce.EntityType ?? "CONCEPT",
            CanonicalName = ce.Name.ToLowerInvariant(),
            Metadata = new Dictionary<string, object>
            {
                ["imported_from"] = "core",
                ["original_observations"] = ce.Observations ?? []
            }
        }).ToList();

        Dictionary<string, Guid> entityNameToId;
        try
        {
            entityNameToId = await _store.CreateEntitiesBatchAsync(entitiesToCreate, cancellationToken);
            result.EntitiesImported = entityNameToId.Count;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to batch-create entities: {ex.Message}");
            entityNameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        }

        // 2. Batch-embed and create observation memories
        // Collect all observations with their entity context
        var observationTexts = new List<(string Text, string EntityName, Guid EntityId)>();
        foreach (var coreEntity in coreData.Entities)
        {
            if (coreEntity.Observations == null || !entityNameToId.TryGetValue(coreEntity.Name, out var entityId))
                continue;

            foreach (var observation in coreEntity.Observations)
            {
                observationTexts.Add(($"{coreEntity.Name}: {observation}", coreEntity.Name, entityId));
            }
        }

        // Batch-embed in chunks of 50
        const int embedBatchSize = 50;
        await using var uow = await _store.BeginUnitOfWorkAsync(cancellationToken);

        for (var i = 0; i < observationTexts.Count; i += embedBatchSize)
        {
            var batch = observationTexts.Skip(i).Take(embedBatchSize).ToList();
            var texts = batch.Select(o => o.Text).ToList();

            try
            {
                var embeddings = await _embeddingService.EmbedBatchAsync(texts, cancellationToken);

                for (var j = 0; j < batch.Count; j++)
                {
                    try
                    {
                        var memory = new Memory
                        {
                            Content = batch[j].Text,
                            Embedding = embeddings[j],
                            Source = source,
                            Metadata = new Dictionary<string, object>
                            {
                                ["type"] = "observation",
                                ["entity"] = batch[j].EntityName,
                                ["imported_from"] = "core"
                            },
                            MemoryType = "knowledge"
                        };

                        var memoryId = await _store.CreateMemoryAsync(memory, cancellationToken);
                        await _store.LinkMemoryToEntityAsync(memoryId, batch[j].EntityId, 1.0f, cancellationToken);
                        result.ObservationsImported++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Failed to import observation for '{batch[j].EntityName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to batch-embed observations (batch {i / embedBatchSize}): {ex.Message}");
            }
        }

        // 3. Batch-create all relationships in one roundtrip
        var relationshipsToCreate = new List<EntityRelationship>();
        foreach (var coreRelation in coreData.Relations)
        {
            if (!entityNameToId.TryGetValue(coreRelation.From, out var sourceId))
            {
                result.Errors.Add($"Source entity '{coreRelation.From}' not found for relation");
                continue;
            }

            if (!entityNameToId.TryGetValue(coreRelation.To, out var targetId))
            {
                result.Errors.Add($"Target entity '{coreRelation.To}' not found for relation");
                continue;
            }

            relationshipsToCreate.Add(new EntityRelationship
            {
                SourceEntityId = sourceId,
                TargetEntityId = targetId,
                RelationshipType = coreRelation.RelationType.ToUpperInvariant().Replace(" ", "_"),
                Confidence = 1.0f,
                Metadata = new Dictionary<string, object>
                {
                    ["imported_from"] = "core"
                }
            });
        }

        try
        {
            await _store.CreateRelationshipsBatchAsync(relationshipsToCreate, cancellationToken);
            result.RelationsImported = relationshipsToCreate.Count;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to batch-create relationships: {ex.Message}");
        }

        return result;
    }

    #endregion
}

#region Result Types

public enum SearchMode
{
    Semantic,
    Text,
    Hybrid
}

public class MemoryIngestResult
{
    public Guid MemoryId { get; set; }
    public int EntitiesCreated { get; set; }
    public int RelationshipsCreated { get; set; }
    public List<EntityInfo> Entities { get; set; } = [];
    public List<RelationshipInfo> Relationships { get; set; } = [];
    public bool DuplicateDetected { get; set; }
    public Guid? DuplicateOf { get; set; }
    public float DuplicateSimilarity { get; set; }
    public List<DuplicateInfo>? SimilarMemories { get; set; }
}

public class DuplicateInfo
{
    public Guid MemoryId { get; set; }
    public float Similarity { get; set; }
    public string ContentPreview { get; set; } = "";
}

public class MemorySearchResult
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Source { get; set; }
    public float Similarity { get; set; }
    public float Rank { get; set; }
    public List<EntityInfo> Entities { get; set; } = [];

    // Structured observation fields (P1 - GAP 5)
    public string? MemoryType { get; set; }
    public string? Title { get; set; }
    public List<string>? Facts { get; set; }
    public List<string>? Concepts { get; set; }
    public List<string>? FilesRead { get; set; }
    public List<string>? FilesModified { get; set; }

    // Token estimation (P1 - GAP 4)
    public int EstimatedTokens { get; set; }
}

public class MultiHopSearchResult
{
    public int Hops { get; set; }
    public List<MemorySearchResult> Memories { get; set; } = [];
    public List<EntityInfo> Entities { get; set; } = [];
    public List<RelationshipInfo> Relationships { get; set; } = [];
}

public class EntityInfo
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public float Confidence { get; set; } = 1.0f;
}

public class RelationshipInfo
{
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public required string Source { get; set; }
    public required string Target { get; set; }
    public required string Type { get; set; }
    public float Confidence { get; set; } = 1.0f;
}

#endregion

#region CORE Import Types

/// <summary>
/// CORE MCP export data format
/// </summary>
public class CoreExportData
{
    public List<CoreEntity> Entities { get; set; } = [];
    public List<CoreRelation> Relations { get; set; } = [];
}

public class CoreEntity
{
    public required string Name { get; set; }
    public string? EntityType { get; set; }
    public List<string>? Observations { get; set; }
}

public class CoreRelation
{
    public required string From { get; set; }
    public required string To { get; set; }
    public required string RelationType { get; set; }
}

public class CoreImportResult
{
    public int EntitiesImported { get; set; }
    public int RelationsImported { get; set; }
    public int ObservationsImported { get; set; }
    public List<string> Errors { get; set; } = [];
}

public class PreviousDayContext
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string? ProjectOrSubject { get; set; }
    public int MemoryCount { get; set; }
    public int RecentMemoryCount { get; set; }
    public int ContextMemoryCount { get; set; }
    public required string SessionSummary { get; set; }
    public List<MemorySearchResult> Memories { get; set; } = [];
    public List<EntityInfo> TopEntities { get; set; } = [];
    public List<RelationshipInfo> TopRelationships { get; set; } = [];
}

#endregion

#region Progressive Disclosure Result Types (P0 - GAP 2)

/// <summary>
/// Compact memory entry for token-efficient search results (~50-80 tokens each).
/// </summary>
public class CompactMemoryEntry
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string MemoryType { get; set; } = "knowledge";
    public string Title { get; set; } = "";
    public float Similarity { get; set; }
    public int EntityCount { get; set; }
    public int ContentLength { get; set; }
    public int EstimatedTokens { get; set; }
}

/// <summary>
/// Result from compact search index. Includes token savings metadata.
/// </summary>
public class CompactSearchResult
{
    public List<CompactMemoryEntry> Results { get; set; } = [];
    public int TotalResults { get; set; }
    public SearchMeta Meta { get; set; } = new();
}

/// <summary>
/// Token usage metadata for search results.
/// </summary>
public class SearchMeta
{
    public int IndexTokens { get; set; }
    public int FullFetchTokens { get; set; }
    public int TokenSavingsPercent { get; set; }
}

/// <summary>
/// Timeline navigation result showing memories around an anchor point.
/// </summary>
public class TimelineResult
{
    public Guid? AnchorId { get; set; }
    public DateTime? AnchorTime { get; set; }
    public List<CompactMemoryEntry> Entries { get; set; } = [];
    public int TotalEntries { get; set; }
}

#endregion
