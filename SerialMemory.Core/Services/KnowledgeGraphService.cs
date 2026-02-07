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
        CancellationToken cancellationToken = default)
    {
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
                    var mergedEmbedding = await _embeddingService.EmbedTextAsync(merged, cancellationToken);

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
                        Embedding = mergedEmbedding,
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

        // Create memory
        var memory = new Memory
        {
            Content = content,
            Embedding = embedding,
            Source = source,
            ConversationSessionId = sessionId,
            Metadata = metadata,
            MemoryType = resolvedMemoryType
        };

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

        if (!extractEntities) return result;

        // Extract entities and relationships
        var (extractedEntities, extractedRelationships) = await _entityExtractionService.ExtractAllAsync(content, cancellationToken);

        // Store entities and link to memory
        var entityIdMap = new Dictionary<string, Guid>(); // Map entity text to ID

        foreach (var extracted in extractedEntities)
        {
            var entity = new Entity
            {
                Name = extracted.Text,
                EntityType = extracted.Label,
                CanonicalName = extracted.Text.ToLowerInvariant(),
                FirstSeenMemoryId = memoryId,
                Metadata = new Dictionary<string, object>
                {
                    ["confidence"] = extracted.Confidence,
                    ["start"] = extracted.Start,
                    ["end"] = extracted.End
                }
            };

            var entityId = await _store.CreateEntityAsync(entity, cancellationToken);
            entityIdMap[extracted.Text] = entityId;

            await _store.LinkMemoryToEntityAsync(memoryId, entityId, extracted.Confidence, cancellationToken);

            result.Entities.Add(new EntityInfo
            {
                Id = entityId,
                Name = extracted.Text,
                Type = extracted.Label,
                Confidence = extracted.Confidence
            });
            result.EntitiesCreated++;
        }

        // Store relationships
        foreach (var extracted in extractedRelationships)
        {
            if (!entityIdMap.TryGetValue(extracted.SourceEntity, out var sourceId) ||
                !entityIdMap.TryGetValue(extracted.TargetEntity, out var targetId))
            {
                continue; // Skip if entities not found
            }

            var relationship = new EntityRelationship
            {
                SourceEntityId = sourceId,
                TargetEntityId = targetId,
                RelationshipType = extracted.RelationType,
                Confidence = extracted.Confidence,
                FirstSeenMemoryId = memoryId
            };

            await _store.CreateRelationshipAsync(relationship, cancellationToken);

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
        var results = new List<MemorySearchResult>();

        foreach (var memory in memories)
        {
            var result = new MemorySearchResult
            {
                Id = memory.Id,
                Content = memory.Content,
                CreatedAt = memory.CreatedAt,
                Source = memory.Source,
                Entities = []
            };

            if (includeEntities)
            {
                var entities = await _store.GetEntitiesForMemoryAsync(memory.Id, cancellationToken);
                result.Entities = entities.Select(e => new EntityInfo
                {
                    Id = e.Id,
                    Name = e.Name,
                    Type = e.EntityType
                }).ToList();
            }

            results.Add(result);
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

        if (mode == SearchMode.Semantic || mode == SearchMode.Hybrid)
        {
            var queryEmbedding = await _embeddingService.EmbedTextAsync(query, cancellationToken);
            var semanticResults = await _store.SearchMemoriesByEmbeddingAsync(queryEmbedding, limit, threshold, memoryType, cancellationToken);

            foreach (var memory in semanticResults)
            {
                var result = new MemorySearchResult
                {
                    Id = memory.Id,
                    Content = memory.Content,
                    CreatedAt = memory.CreatedAt,
                    Source = memory.Source,
                    Similarity = memory.Similarity, // Use similarity score from search results
                    Entities = []
                };

                if (includeEntities)
                {
                    var entities = await _store.GetEntitiesForMemoryAsync(memory.Id, cancellationToken);
                    result.Entities = entities.Select(e => new EntityInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Type = e.EntityType
                    }).ToList();
                }

                results.Add(result);
            }
        }

        if (mode == SearchMode.Text || mode == SearchMode.Hybrid)
        {
            var textResults = await _store.SearchMemoriesByTextAsync(query, limit, memoryType, cancellationToken);

            foreach (var memory in textResults)
            {
                // Avoid duplicates in hybrid mode
                if (results.Any(r => r.Id == memory.Id)) continue;

                var result = new MemorySearchResult
                {
                    Id = memory.Id,
                    Content = memory.Content,
                    CreatedAt = memory.CreatedAt,
                    Source = memory.Source,
                    Rank = memory.Rank, // Use rank score from text search results
                    Entities = []
                };

                if (includeEntities)
                {
                    var entities = await _store.GetEntitiesForMemoryAsync(memory.Id, cancellationToken);
                    result.Entities = entities.Select(e => new EntityInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Type = e.EntityType
                    }).ToList();
                }

                results.Add(result);
            }
        }

        return results.Take(limit).ToList();
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

        // Traverse hops - only process frontier entities (those not yet processed for relationships)
        for (int hop = 0; hop < hops; hop++)
        {
            // Get only entities that haven't had their relationships fetched yet
            var frontierEntities = result.Entities
                .Where(e => !processedEntityIds.Contains(e.Id))
                .ToList();

            if (frontierEntities.Count == 0)
                break; // No new entities to explore

            foreach (var entity in frontierEntities)
            {
                // Mark this entity as processed before fetching relationships
                processedEntityIds.Add(entity.Id);

                // Get relationships for this entity
                var relationships = await _store.GetRelationshipsForEntityAsync(entity.Id, cancellationToken);

                foreach (var rel in relationships.Take(maxResultsPerHop))
                {
                    // Check for duplicate relationships (using source, target, type as key)
                    var relationshipKey = (rel.SourceEntityId, rel.TargetEntityId, rel.RelationshipType);
                    if (!visitedRelationships.Add(relationshipKey))
                        continue; // Skip duplicate relationship

                    result.Relationships.Add(new RelationshipInfo
                    {
                        SourceId = rel.SourceEntityId,
                        TargetId = rel.TargetEntityId,
                        Source = rel.SourceEntity?.Name ?? entity.Name,
                        Target = rel.TargetEntity?.Name ?? "Unknown",
                        Type = rel.RelationshipType,
                        Confidence = rel.Confidence
                    });

                    // Get connected entity
                    var connectedEntityId = rel.SourceEntityId == entity.Id
                        ? rel.TargetEntityId
                        : rel.SourceEntityId;

                    if (visitedEntityIds.Add(connectedEntityId))
                    {
                        var connectedEntity = await _store.GetEntityByIdAsync(connectedEntityId, cancellationToken);
                        if (connectedEntity != null)
                        {
                            result.Entities.Add(new EntityInfo
                            {
                                Id = connectedEntity.Id,
                                Name = connectedEntity.Name,
                                Type = connectedEntity.EntityType
                            });

                            // Get memories for this entity
                            // Note: Would need additional method in store to get memories by entity
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Get context from the previous day to instantiate a new session with continuity.
    /// Filters by project/subject using semantic search if provided.
    /// </summary>
    public async Task<PreviousDayContext> GetPreviousDayContextAsync(
        string? projectOrSubject = null,
        int daysBack = 1,
        int limit = 50,
        bool includeEntities = true,
        CancellationToken cancellationToken = default)
    {
        // Calculate the date range for the previous day(s)
        var now = DateTime.UtcNow;
        var toUtc = now; // Include up to current time (not just start of today)
        var fromUtc = now.Date.AddDays(-daysBack); // Start of N days ago

        List<Memory> memories;

        if (!string.IsNullOrWhiteSpace(projectOrSubject))
        {
            // Get ALL memories from date range first, then filter by semantic relevance
            // This ensures we don't miss recent memories just because older ones rank higher semantically
            var allMemoriesInRange = await _store.GetMemoriesByDateRangeAsync(fromUtc, toUtc, limit: 1000, cancellationToken);

            var queryEmbedding = await _embeddingService.EmbedTextAsync(projectOrSubject, cancellationToken);

            if (allMemoriesInRange.Count == 0)
            {
                // No recent memories, but search older ones for context
                var olderMemories = await _store.SearchMemoriesByEmbeddingAsync(queryEmbedding, limit, threshold: 0.5f, cancellationToken: cancellationToken);
                memories = olderMemories.Where(m => m.CreatedAt < fromUtc).ToList();
            }
            else
            {
                // Filter recent memories by semantic similarity to project/subject
                var recentScoredMemories = new List<(Memory memory, float similarity, bool isRecent)>();

                foreach (var memory in allMemoriesInRange)
                {
                    if (memory.Embedding != null && memory.Embedding.Length > 0)
                    {
                        var similarity = CosineSimilarity(queryEmbedding, memory.Embedding);
                        if (similarity >= 0.3f) // Lower threshold since we're already filtered by date
                        {
                            recentScoredMemories.Add((memory, similarity, true));
                        }
                    }
                }

                // Take top recent memories
                var topRecentMemories = recentScoredMemories
                    .OrderByDescending(x => x.similarity)
                    .Take(limit)
                    .ToList();

                // Also fetch similar OLDER memories for context (up to 30% of limit)
                var contextLimit = Math.Max(3, limit / 3);
                var olderMemories = await _store.SearchMemoriesByEmbeddingAsync(queryEmbedding, contextLimit * 2, threshold: 0.5f, cancellationToken: cancellationToken);
                var olderContextMemories = olderMemories
                    .Where(m => m.CreatedAt < fromUtc) // Only older than date range
                    .Take(contextLimit)
                    .Select(m => (memory: m, similarity: 0f, isRecent: false))
                    .ToList();

                // Combine: recent memories first (by similarity), then older context memories (by date descending)
                var combined = new List<(Memory memory, float similarity, bool isRecent)>();
                combined.AddRange(topRecentMemories);
                combined.AddRange(olderContextMemories);

                memories = combined.Select(x => x.memory).ToList();
            }
        }
        else
        {
            // No filter - get all memories from the date range
            memories = await _store.GetMemoriesByDateRangeAsync(fromUtc, toUtc, limit, cancellationToken);
        }

        // Count recent vs contextual memories
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

        // Collect all entities and relationships for aggregation
        var entityCounts = new Dictionary<string, (EntityInfo Entity, int Count)>();
        var relationshipCounts = new Dictionary<string, (RelationshipInfo Relationship, int Count)>();

        foreach (var memory in memories)
        {
            var memoryResult = new MemorySearchResult
            {
                Id = memory.Id,
                Content = memory.Content,
                CreatedAt = memory.CreatedAt,
                Source = memory.Source,
                Similarity = memory.Similarity,
                Entities = []
            };

            if (includeEntities)
            {
                var entities = await _store.GetEntitiesForMemoryAsync(memory.Id, cancellationToken);
                memoryResult.Entities = entities.Select(e => new EntityInfo
                {
                    Id = e.Id,
                    Name = e.Name,
                    Type = e.EntityType
                }).ToList();

                // Aggregate entity counts
                foreach (var entity in memoryResult.Entities)
                {
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

                // Get relationships for entities in this memory
                foreach (var entity in entities.Take(5)) // Limit to avoid too many queries
                {
                    var relationships = await _store.GetRelationshipsForEntityAsync(entity.Id, cancellationToken);
                    foreach (var rel in relationships.Take(3))
                    {
                        var relInfo = new RelationshipInfo
                        {
                            SourceId = rel.SourceEntityId,
                            TargetId = rel.TargetEntityId,
                            Source = rel.SourceEntity?.Name ?? entity.Name,
                            Target = rel.TargetEntity?.Name ?? "Unknown",
                            Type = rel.RelationshipType,
                            Confidence = rel.Confidence
                        };

                        var key = $"{relInfo.Source}->{relInfo.Type}->{relInfo.Target}";
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

            result.Memories.Add(memoryResult);
        }

        // Get top entities and relationships by frequency
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

        // Generate a summary
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
        {
            result.SessionSummary += $" Main topics: {string.Join(", ", topics)}.";
        }

        if (people.Count > 0)
        {
            result.SessionSummary += $" People mentioned: {string.Join(", ", people)}.";
        }

        if (entityTypes.Count > 0)
        {
            result.SessionSummary += $" Entity breakdown: {string.Join(", ", entityTypes)}.";
        }

        return result;
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

        var entityNameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // Import entities
        foreach (var coreEntity in coreData.Entities)
        {
            try
            {
                var entity = new Entity
                {
                    Name = coreEntity.Name,
                    EntityType = coreEntity.EntityType ?? "CONCEPT",
                    CanonicalName = coreEntity.Name.ToLowerInvariant(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["imported_from"] = "core",
                        ["original_observations"] = coreEntity.Observations ?? []
                    }
                };

                var entityId = await _store.CreateEntityAsync(entity, cancellationToken);
                entityNameToId[coreEntity.Name] = entityId;
                result.EntitiesImported++;

                // Import observations as memories linked to this entity
                if (coreEntity.Observations != null)
                {
                    foreach (var observation in coreEntity.Observations)
                    {
                        var ingestResult = await IngestMemoryAsync(
                            $"{coreEntity.Name}: {observation}",
                            source,
                            null,
                            new Dictionary<string, object>
                            {
                                ["type"] = "observation",
                                ["entity"] = coreEntity.Name,
                                ["imported_from"] = "core"
                            },
                            false, // Don't re-extract entities
                            dedupMode: "off",
                            cancellationToken: cancellationToken);

                        await _store.LinkMemoryToEntityAsync(ingestResult.MemoryId, entityId, 1.0f, cancellationToken);
                        result.ObservationsImported++;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to import entity '{coreEntity.Name}': {ex.Message}");
            }
        }

        // Import relations
        foreach (var coreRelation in coreData.Relations)
        {
            try
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

                var relationship = new EntityRelationship
                {
                    SourceEntityId = sourceId,
                    TargetEntityId = targetId,
                    RelationshipType = coreRelation.RelationType.ToUpperInvariant().Replace(" ", "_"),
                    Confidence = 1.0f,
                    Metadata = new Dictionary<string, object>
                    {
                        ["imported_from"] = "core"
                    }
                };

                await _store.CreateRelationshipAsync(relationship, cancellationToken);
                result.RelationsImported++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to import relation '{coreRelation.From}' -> '{coreRelation.To}': {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Calculate cosine similarity between two embedding vectors.
    /// Returns a value between -1 and 1, where 1 means identical, 0 means orthogonal, -1 means opposite.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length");

        float dotProduct = 0;
        float magnitudeA = 0;
        float magnitudeB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = MathF.Sqrt(magnitudeA);
        magnitudeB = MathF.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
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
