using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Core.Services;

/// <summary>
/// Service for discovering and inferring relationships between entities.
/// Uses multiple strategies: pattern matching, co-occurrence analysis, and semantic similarity.
/// </summary>
public class RelationshipDiscoveryService
{
    private readonly IKnowledgeGraphStore _store;
    private readonly IEmbeddingService _embeddingService;

    // Relationship type inference rules based on entity types
    private static readonly Dictionary<(string, string), string[]> EntityTypeRelationships = new()
    {
        [("PERSON", "ORG")] = ["works_at", "founded", "member_of", "associated_with"],
        [("ORG", "PERSON")] = ["employs", "founded_by", "led_by", "associated_with"],
        [("PERSON", "GPE")] = ["lives_in", "from", "visited", "associated_with"],
        [("GPE", "PERSON")] = ["has_resident", "birthplace_of", "associated_with"],
        [("PERSON", "PERSON")] = ["knows", "works_with", "related_to", "associated_with"],
        [("ORG", "ORG")] = ["partner_of", "competitor_of", "subsidiary_of", "associated_with"],
        [("ORG", "GPE")] = ["located_in", "headquartered_in", "operates_in", "associated_with"],
        [("GPE", "ORG")] = ["hosts", "associated_with"],
        [("PERSON", "TITLE")] = ["holds_title", "has_role"],
        [("TITLE", "PERSON")] = ["held_by", "associated_with"],
        [("PERSON", "EMAIL")] = ["has_email", "contactable_via"],
        [("EMAIL", "PERSON")] = ["belongs_to", "associated_with"],
        [("PERSON", "URL")] = ["has_website", "linked_to"],
        [("ORG", "URL")] = ["has_website", "linked_to"],
        [("PERSON", "DATE")] = ["born_on", "active_in", "associated_with"],
        [("ORG", "DATE")] = ["founded_on", "active_in", "associated_with"],
    };

    public RelationshipDiscoveryService(
        IKnowledgeGraphStore store,
        IEmbeddingService embeddingService)
    {
        _store = store;
        _embeddingService = embeddingService;
    }

    /// <summary>
    /// Discover relationships between entities that co-occur in memories.
    /// </summary>
    public async Task<RelationshipDiscoveryResult> DiscoverCoOccurrenceRelationshipsAsync(
        int memoriesLimit = 100,
        float minConfidence = 0.5f,
        CancellationToken cancellationToken = default)
    {
        var result = new RelationshipDiscoveryResult();
        var coOccurrences = new Dictionary<(Guid, Guid), CoOccurrence>();

        // Get recent memories and batch-load all entities in one query
        var memories = await _store.GetRecentMemoriesAsync(memoriesLimit, cancellationToken);
        var memoryIds = memories.Select(m => m.Id).ToList();
        var entitiesByMemory = await _store.GetEntitiesForMemoriesAsync(memoryIds, cancellationToken);

        foreach (var memory in memories)
        {
            if (!entitiesByMemory.TryGetValue(memory.Id, out var entities))
                continue;

            // Track co-occurrences between entities in the same memory
            for (int i = 0; i < entities.Count; i++)
            {
                for (int j = i + 1; j < entities.Count; j++)
                {
                    var key = (entities[i].Id, entities[j].Id);
                    var reverseKey = (entities[j].Id, entities[i].Id);

                    if (!coOccurrences.ContainsKey(key) && !coOccurrences.ContainsKey(reverseKey))
                    {
                        coOccurrences[key] = new CoOccurrence
                        {
                            EntityA = entities[i],
                            EntityB = entities[j],
                            Count = 0,
                            MemoryIds = []
                        };
                    }

                    var actualKey = coOccurrences.ContainsKey(key) ? key : reverseKey;
                    coOccurrences[actualKey].Count++;
                    coOccurrences[actualKey].MemoryIds.Add(memory.Id);
                }
            }
        }

        // Create relationships from significant co-occurrences
        foreach (var (key, coOccur) in coOccurrences)
        {
            // Calculate confidence based on co-occurrence count
            var confidence = Math.Min(1.0f, coOccur.Count * 0.2f); // 5+ co-occurrences = 100%

            if (confidence < minConfidence)
                continue;

            // Infer relationship type based on entity types
            var relationType = InferRelationshipType(
                coOccur.EntityA.EntityType,
                coOccur.EntityB.EntityType);

            var relationship = new EntityRelationship
            {
                SourceEntityId = coOccur.EntityA.Id,
                TargetEntityId = coOccur.EntityB.Id,
                RelationshipType = relationType,
                Confidence = confidence,
                FirstSeenMemoryId = coOccur.MemoryIds.First(),
                Metadata = new Dictionary<string, object>
                {
                    ["discovery_method"] = "co_occurrence",
                    ["co_occurrence_count"] = coOccur.Count,
                    ["memory_ids"] = coOccur.MemoryIds.Distinct().ToList()
                }
            };

            try
            {
                var id = await _store.CreateRelationshipAsync(relationship, cancellationToken);
                result.RelationshipsCreated++;
                result.Relationships.Add(new DiscoveredRelationship
                {
                    Id = id,
                    SourceName = coOccur.EntityA.Name,
                    TargetName = coOccur.EntityB.Name,
                    RelationType = relationType,
                    Confidence = confidence,
                    Method = "co_occurrence"
                });
            }
            catch
            {
                // Relationship may already exist
                result.RelationshipsSkipped++;
            }
        }

        return result;
    }

    /// <summary>
    /// Discover relationships from memory content using pattern matching.
    /// Re-analyzes existing memories for relationships that may have been missed.
    /// </summary>
    public async Task<RelationshipDiscoveryResult> DiscoverPatternRelationshipsAsync(
        int memoriesLimit = 100,
        CancellationToken cancellationToken = default)
    {
        var result = new RelationshipDiscoveryResult();

        var memories = await _store.GetRecentMemoriesAsync(memoriesLimit, cancellationToken);
        var memoryIds = memories.Select(m => m.Id).ToList();
        var entitiesByMemory = await _store.GetEntitiesForMemoriesAsync(memoryIds, cancellationToken);

        foreach (var memory in memories)
        {
            if (!entitiesByMemory.TryGetValue(memory.Id, out var entities))
                continue;
            var entityNameMap = entities.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);

            foreach (var (pattern, relationType) in RelationshipPatterns)
            {
                var matches = pattern.Matches(memory.Content);

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var sourceText = match.Groups[1].Value.Trim();
                    var targetText = match.Groups[2].Value.Trim();

                    // Find matching entities
                    if (!entityNameMap.TryGetValue(sourceText, out var sourceEntity) ||
                        !entityNameMap.TryGetValue(targetText, out var targetEntity))
                    {
                        continue;
                    }

                    var relationship = new EntityRelationship
                    {
                        SourceEntityId = sourceEntity.Id,
                        TargetEntityId = targetEntity.Id,
                        RelationshipType = relationType,
                        Confidence = 0.9f,
                        FirstSeenMemoryId = memory.Id,
                        Metadata = new Dictionary<string, object>
                        {
                            ["discovery_method"] = "pattern_matching",
                            ["matched_text"] = match.Value
                        }
                    };

                    try
                    {
                        var id = await _store.CreateRelationshipAsync(relationship, cancellationToken);
                        result.RelationshipsCreated++;
                        result.Relationships.Add(new DiscoveredRelationship
                        {
                            Id = id,
                            SourceName = sourceEntity.Name,
                            TargetName = targetEntity.Name,
                            RelationType = relationType,
                            Confidence = 0.9f,
                            Method = "pattern_matching"
                        });
                    }
                    catch
                    {
                        result.RelationshipsSkipped++;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Get all relationships from the database for visualization.
    /// Uses the store's GetAllRelationshipsAsync directly instead of iterating memory-by-memory.
    /// </summary>
    public async Task<List<RelationshipWithEntities>> GetAllRelationshipsAsync(
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        // Fetch all relationships directly - the store query JOINs entity names
        var relationships = await _store.GetAllRelationshipsAsync(limit, cancellationToken);

        var relationshipSet = new HashSet<(Guid, Guid, string)>();
        var results = new List<RelationshipWithEntities>();

        foreach (var rel in relationships)
        {
            var key = (rel.SourceEntityId, rel.TargetEntityId, rel.RelationshipType);
            if (relationshipSet.Add(key))
            {
                results.Add(new RelationshipWithEntities
                {
                    Id = rel.Id,
                    SourceEntityId = rel.SourceEntityId,
                    TargetEntityId = rel.TargetEntityId,
                    SourceEntityName = rel.SourceEntity?.Name ?? "Unknown",
                    TargetEntityName = rel.TargetEntity?.Name ?? "Unknown",
                    RelationshipType = rel.RelationshipType,
                    Confidence = rel.Confidence
                });
            }
        }

        return results;
    }

    private static string InferRelationshipType(string sourceType, string targetType)
    {
        var key = (sourceType.ToUpperInvariant(), targetType.ToUpperInvariant());

        if (EntityTypeRelationships.TryGetValue(key, out var types))
        {
            return types[0]; // Return the most common relationship type
        }

        return "associated_with";
    }

    private static readonly List<(System.Text.RegularExpressions.Regex Pattern, string RelationType)> RelationshipPatterns =
    [
        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:works? at|employed by|is with)\s+([A-Z][a-zA-Z\s]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "works_at"),

        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:founded|created|started)\s+([A-Z][a-zA-Z\s]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "founded"),

        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:lives? in|is from|resides in)\s+([A-Z][a-zA-Z\s,]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "lives_in"),

        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:knows?|met|friend of)\s+([A-Z][a-z]+ [A-Z][a-z]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "knows"),

        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:is (?:the )?(?:CEO|CTO|CFO|COO|President|Director|Manager|VP|Engineer) (?:of|at))\s+([A-Z][a-zA-Z\s]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "leads"),

        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-zA-Z\s]+)\s+(?:acquired|bought|purchased)\s+([A-Z][a-zA-Z\s]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "acquired"),

        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-zA-Z\s]+)\s+(?:partnered with|partners with|collaborated with)\s+([A-Z][a-zA-Z\s]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "partner_of"),

        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-zA-Z\s]+)\s+(?:is located in|headquartered in|based in)\s+([A-Z][a-zA-Z\s,]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "located_in"),

        (new System.Text.RegularExpressions.Regex(
            @"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:and|&)\s+([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:work|worked|collaborate)",
            System.Text.RegularExpressions.RegexOptions.Compiled), "works_with"),
    ];

    private class CoOccurrence
    {
        public required Entity EntityA { get; set; }
        public required Entity EntityB { get; set; }
        public int Count { get; set; }
        public List<Guid> MemoryIds { get; set; } = [];
    }
}

/// <summary>
/// Result of relationship discovery operation.
/// </summary>
public class RelationshipDiscoveryResult
{
    public int RelationshipsCreated { get; set; }
    public int RelationshipsSkipped { get; set; }
    public List<DiscoveredRelationship> Relationships { get; set; } = [];
}

/// <summary>
/// A discovered relationship.
/// </summary>
public class DiscoveredRelationship
{
    public Guid Id { get; set; }
    public required string SourceName { get; set; }
    public required string TargetName { get; set; }
    public required string RelationType { get; set; }
    public float Confidence { get; set; }
    public required string Method { get; set; }
}

/// <summary>
/// Relationship with entity names for display.
/// </summary>
public class RelationshipWithEntities
{
    public Guid Id { get; set; }
    public Guid SourceEntityId { get; set; }
    public Guid TargetEntityId { get; set; }
    public required string SourceEntityName { get; set; }
    public required string TargetEntityName { get; set; }
    public required string RelationshipType { get; set; }
    public float Confidence { get; set; }
}
