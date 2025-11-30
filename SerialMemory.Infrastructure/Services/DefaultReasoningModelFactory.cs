using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// Default factory for creating reasoning models.
/// Creates rule-based models for different reasoning roles.
/// </summary>
public sealed class DefaultReasoningModelFactory(
    IKnowledgeGraphStore store,
    ILoggerFactory loggerFactory)
    : IReasoningModelFactory
{
    public IEnumerable<IReasoningModel> CreateModels()
    {
        yield return new StructuralReasoningModel(store, loggerFactory.CreateLogger<StructuralReasoningModel>());
        yield return new RiskReasoningModel(store, loggerFactory.CreateLogger<RiskReasoningModel>());
        yield return new OptimizationReasoningModel(store, loggerFactory.CreateLogger<OptimizationReasoningModel>());
        yield return new ContradictionReasoningModel(store, loggerFactory.CreateLogger<ContradictionReasoningModel>());
    }

    public IEnumerable<IReasoningModel> CreateModelsForRole(ReasoningRole role)
    {
        return CreateModels().Where(m => m.Role == role);
    }
}

/// <summary>
/// Validates graph structural consistency (orphaned nodes, cycles, missing references).
/// </summary>
public sealed class StructuralReasoningModel(IKnowledgeGraphStore store, ILogger<StructuralReasoningModel> logger)
    : IReasoningModel
{
    private readonly IKnowledgeGraphStore _store = store;

    public string Name => "StructuralValidator";
    public string Version => "1.0.0";
    public ReasoningRole Role => ReasoningRole.Structural;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ModelResult> ReasonAsync(ReasoningInput input, CancellationToken cancellationToken = default)
    {
        var insights = new List<ModelInsight>();

        try
        {
            var entities = input.Entities ?? [];
            var relationships = input.Relationships ?? [];
            var entityIds = entities.Select(e => e.Id).ToHashSet();

            // Find orphaned entities (no relationships)
            var connectedIds = relationships
                .SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId })
                .ToHashSet();

            var orphanedEntities = entities.Where(e => !connectedIds.Contains(e.Id)).ToList();
            if (orphanedEntities.Count > 0)
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Info,
                    Message = $"Found {orphanedEntities.Count} orphaned entities with no relationships",
                    Confidence = 0.9f,
                    AffectedEntities = orphanedEntities.Take(10).Select(e => e.Id).ToList(),
                    Details = new Dictionary<string, object>
                    {
                        ["orphanedCount"] = orphanedEntities.Count,
                        ["orphanedNames"] = orphanedEntities.Take(10).Select(e => e.Name).ToList()
                    }
                });
            }

            // Find dangling references (relationships pointing to non-existent entities)
            var danglingRels = relationships
                .Where(r => !entityIds.Contains(r.SourceEntityId) || !entityIds.Contains(r.TargetEntityId))
                .ToList();

            if (danglingRels.Count > 0)
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Risk,
                    Message = $"Found {danglingRels.Count} relationships with dangling references",
                    Confidence = 1.0f,
                    Details = new Dictionary<string, object>
                    {
                        ["danglingCount"] = danglingRels.Count,
                        ["danglingTypes"] = danglingRels.GroupBy(r => r.RelationshipType)
                            .Select(g => new { Type = g.Key, Count = g.Count() })
                            .ToList()
                    }
                });
            }

            // Find duplicate relationships
            var duplicateRels = relationships
                .GroupBy(r => (r.SourceEntityId, r.TargetEntityId, r.RelationshipType))
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicateRels.Count > 0)
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Conflict,
                    Message = $"Found {duplicateRels.Count} duplicate relationship patterns",
                    Confidence = 0.95f,
                    Details = new Dictionary<string, object>
                    {
                        ["duplicatePatterns"] = duplicateRels.Select(g => new
                        {
                            Type = g.Key.RelationshipType,
                            Count = g.Count()
                        }).ToList()
                    }
                });
            }

            // Check for high-degree nodes (potential bottlenecks)
            var nodeDegrees = relationships
                .SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId })
                .GroupBy(id => id)
                .Select(g => (Id: g.Key, Degree: g.Count()))
                .OrderByDescending(x => x.Degree)
                .ToList();

            var highDegreeNodes = nodeDegrees.Where(n => n.Degree > 20).ToList();
            if (highDegreeNodes.Count <= 0)
                return new ModelResult
                {
                    ModelName = Name,
                    Version = Version,
                    Role = Role,
                    Success = true,
                    DurationMs = 0, // Will be set by orchestrator
                    Insights = insights
                };
            {
                var highDegreeEntities = entities
                    .Where(e => highDegreeNodes.Select(h => h.Id).Contains(e.Id))
                    .ToList();

                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Info,
                    Message = $"Found {highDegreeNodes.Count} high-degree nodes (>20 connections) that may be bottlenecks",
                    Confidence = 0.7f,
                    AffectedEntities = highDegreeNodes.Take(5).Select(h => h.Id).ToList(),
                    Details = new Dictionary<string, object>
                    {
                        ["highDegreeNodes"] = highDegreeNodes.Take(10).Select(h => new
                        {
                            Id = h.Id,
                            Degree = h.Degree,
                            Name = entities.FirstOrDefault(e => e.Id == h.Id)?.Name ?? "Unknown"
                        }).ToList()
                    }
                });
            }

            return new ModelResult
            {
                ModelName = Name,
                Version = Version,
                Role = Role,
                Success = true,
                DurationMs = 0, // Will be set by orchestrator
                Insights = insights
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Structural reasoning failed");
            return new ModelResult
            {
                ModelName = Name,
                Version = Version,
                Role = Role,
                Success = false,
                Error = ex.Message,
                Insights = insights
            };
        }
    }
}

/// <summary>
/// Detects risk patterns (single points of failure, critical dependencies).
/// </summary>
public sealed class RiskReasoningModel(IKnowledgeGraphStore store, ILogger<RiskReasoningModel> logger)
    : IReasoningModel
{
    private readonly IKnowledgeGraphStore _store = store;

    public string Name => "RiskDetector";
    public string Version => "1.0.0";
    public ReasoningRole Role => ReasoningRole.Risk;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ModelResult> ReasonAsync(ReasoningInput input, CancellationToken cancellationToken = default)
    {
        var insights = new List<ModelInsight>();

        try
        {
            var entities = input.Entities ?? [];
            var relationships = input.Relationships ?? [];

            // Find single points of failure (entities that many others depend on)
            var dependencyCount = relationships
                .Where(r => r.RelationshipType.Contains("DEPENDS", StringComparison.OrdinalIgnoreCase) ||
                           r.RelationshipType.Contains("USES", StringComparison.OrdinalIgnoreCase) ||
                           r.RelationshipType.Contains("REQUIRES", StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r.TargetEntityId)
                .Select(g => (Id: g.Key, Count: g.Count()))
                .Where(x => x.Count >= 3)
                .OrderByDescending(x => x.Count)
                .ToList();

            foreach (var spof in dependencyCount.Take(5))
            {
                var entity = entities.FirstOrDefault(e => e.Id == spof.Id);
                if (entity != null)
                {
                    insights.Add(new ModelInsight
                    {
                        Type = MultiModelInsightType.Risk,
                        Message = $"Single point of failure: '{entity.Name}' ({entity.EntityType}) has {spof.Count} dependents",
                        Confidence = Math.Min(0.5f + (spof.Count * 0.1f), 0.95f),
                        AffectedEntities = [spof.Id],
                        Details = new Dictionary<string, object>
                        {
                            ["dependentCount"] = spof.Count,
                            ["entityType"] = entity.EntityType,
                            ["entityName"] = entity.Name
                        }
                    });
                }
            }

            // Find critical path entities (bridges in the graph)
            var criticalEntities = FindBridgeEntities(entities, relationships);
            foreach (var critical in criticalEntities.Take(3))
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Risk,
                    Message = $"Critical path entity: '{critical.Name}' - removing it would disconnect parts of the graph",
                    Confidence = 0.85f,
                    AffectedEntities = [critical.Id],
                    Details = new Dictionary<string, object>
                    {
                        ["entityType"] = critical.EntityType,
                        ["entityName"] = critical.Name
                    }
                });
            }

            // Find entities with "INCIDENT" or "ERROR" or "FAILURE" in their name or type
            var problematicEntities = entities
                .Where(e =>
                    e.Name.Contains("incident", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.Contains("outage", StringComparison.OrdinalIgnoreCase) ||
                    e.EntityType.Equals("INCIDENT", StringComparison.OrdinalIgnoreCase) ||
                    e.EntityType.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (problematicEntities.Count > 0)
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Risk,
                    Message = $"Found {problematicEntities.Count} entities with incident/error/failure mentions",
                    Confidence = 0.8f,
                    AffectedEntities = problematicEntities.Select(e => e.Id).ToList(),
                    Details = new Dictionary<string, object>
                    {
                        ["problematicEntities"] = problematicEntities.Select(e => e.Name).ToList()
                    }
                });
            }

            return new ModelResult
            {
                ModelName = Name,
                Version = Version,
                Role = Role,
                Success = true,
                Insights = insights
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Risk reasoning failed");
            return new ModelResult
            {
                ModelName = Name,
                Version = Version,
                Role = Role,
                Success = false,
                Error = ex.Message,
                Insights = insights
            };
        }
    }

    private List<Entity> FindBridgeEntities(List<Entity> entities, List<EntityRelationship> relationships)
    {
        // Simplified bridge detection - find entities whose removal increases connected components
        var bridges = new List<Entity>();
        var entityIds = entities.Select(e => e.Id).ToHashSet();

        foreach (var entity in entities.Take(100)) // Limit for performance
        {
            var remainingRels = relationships
                .Where(r => r.SourceEntityId != entity.Id && r.TargetEntityId != entity.Id)
                .ToList();

            var componentsBefore = CountConnectedComponents(entityIds, relationships);
            var componentsAfter = CountConnectedComponents(
                entityIds.Where(id => id != entity.Id).ToHashSet(),
                remainingRels);

            if (componentsAfter > componentsBefore)
            {
                bridges.Add(entity);
            }
        }

        return bridges;
    }

    private int CountConnectedComponents(HashSet<Guid> entityIds, List<EntityRelationship> relationships)
    {
        var visited = new HashSet<Guid>();
        var components = 0;

        foreach (var id in entityIds)
        {
            if (!visited.Contains(id))
            {
                components++;
                var queue = new Queue<Guid>();
                queue.Enqueue(id);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (visited.Contains(current)) continue;
                    visited.Add(current);

                    var neighbors = relationships
                        .Where(r => r.SourceEntityId == current || r.TargetEntityId == current)
                        .SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId })
                        .Where(n => entityIds.Contains(n) && !visited.Contains(n));

                    foreach (var neighbor in neighbors)
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        return components;
    }
}

/// <summary>
/// Finds optimization opportunities (redundant relationships, missing connections).
/// </summary>
public sealed class OptimizationReasoningModel(IKnowledgeGraphStore store, ILogger<OptimizationReasoningModel> logger)
    : IReasoningModel
{
    private readonly IKnowledgeGraphStore _store = store;

    public string Name => "OptimizationFinder";
    public string Version => "1.0.0";
    public ReasoningRole Role => ReasoningRole.Optimization;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ModelResult> ReasonAsync(ReasoningInput input, CancellationToken cancellationToken = default)
    {
        var insights = new List<ModelInsight>();

        try
        {
            var entities = input.Entities ?? [];
            var relationships = input.Relationships ?? [];

            // Find redundant transitive relationships
            // If A->B->C and A->C, the A->C might be redundant
            var directRelMap = relationships
                .GroupBy(r => r.SourceEntityId)
                .ToDictionary(g => g.Key, g => g.Select(r => r.TargetEntityId).ToHashSet());

            var redundantCount = 0;
            foreach (var rel in relationships.Take(500)) // Limit for performance
            {
                if (directRelMap.TryGetValue(rel.SourceEntityId, out var targets))
                {
                    // Check if there's a path through an intermediate node
                    foreach (var intermediate in targets.Where(t => t != rel.TargetEntityId))
                    {
                        if (directRelMap.TryGetValue(intermediate, out var secondHop) &&
                            secondHop.Contains(rel.TargetEntityId))
                        {
                            redundantCount++;
                            break;
                        }
                    }
                }
            }

            if (redundantCount > 0)
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Optimization,
                    Message = $"Found ~{redundantCount} potentially redundant transitive relationships",
                    Confidence = 0.6f,
                    Details = new Dictionary<string, object>
                    {
                        ["redundantCount"] = redundantCount
                    }
                });
            }

            // Find entities with similar names that might be duplicates
            var potentialDuplicates = entities
                .GroupBy(e => NormalizeName(e.Name))
                .Where(g => g.Count() > 1)
                .ToList();

            if (potentialDuplicates.Count > 0)
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Optimization,
                    Message = $"Found {potentialDuplicates.Count} potential duplicate entity groups",
                    Confidence = 0.7f,
                    AffectedEntities = potentialDuplicates.SelectMany(g => g.Select(e => e.Id)).ToList(),
                    Details = new Dictionary<string, object>
                    {
                        ["duplicateGroups"] = potentialDuplicates.Select(g => new
                        {
                            NormalizedName = g.Key,
                            Entities = g.Select(e => e.Name).ToList()
                        }).ToList()
                    }
                });
            }

            // Find underconnected entity types
            var typeConnectionStats = entities
                .GroupBy(e => e.EntityType)
                .Select(g =>
                {
                    var ids = g.Select(e => e.Id).ToHashSet();
                    var avgConnections = relationships
                        .Count(r => ids.Contains(r.SourceEntityId) || ids.Contains(r.TargetEntityId)) / (float)g.Count();
                    return new { Type = g.Key, Count = g.Count(), AvgConnections = avgConnections };
                })
                .Where(x => x.Count >= 3 && x.AvgConnections < 1)
                .ToList();

            foreach (var underconnected in typeConnectionStats)
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Optimization,
                    Message = $"Entity type '{underconnected.Type}' has low connectivity (avg {underconnected.AvgConnections:F1} connections per entity)",
                    Confidence = 0.5f,
                    Details = new Dictionary<string, object>
                    {
                        ["entityType"] = underconnected.Type,
                        ["entityCount"] = underconnected.Count,
                        ["avgConnections"] = underconnected.AvgConnections
                    }
                });
            }

            return new ModelResult
            {
                ModelName = Name,
                Version = Version,
                Role = Role,
                Success = true,
                Insights = insights
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Optimization reasoning failed");
            return new ModelResult
            {
                ModelName = Name,
                Version = Version,
                Role = Role,
                Success = false,
                Error = ex.Message,
                Insights = insights
            };
        }
    }

    private static string NormalizeName(string name)
    {
        return string.Join("", name.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c)));
    }
}

/// <summary>
/// Detects logical contradictions in observations and relationships.
/// </summary>
public sealed class ContradictionReasoningModel(IKnowledgeGraphStore store, ILogger<ContradictionReasoningModel> logger)
    : IReasoningModel
{
    private readonly IKnowledgeGraphStore _store = store;

    private static readonly (string Positive, string Negative)[] ContradictoryPairs =
    [
        ("active", "inactive"),
        ("enabled", "disabled"),
        ("running", "stopped"),
        ("online", "offline"),
        ("success", "failure"),
        ("up", "down"),
        ("healthy", "unhealthy"),
        ("connected", "disconnected"),
        ("available", "unavailable"),
        ("working", "broken")
    ];

    public string Name => "ContradictionDetector";
    public string Version => "1.0.0";
    public ReasoningRole Role => ReasoningRole.Contradiction;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public async Task<ModelResult> ReasonAsync(ReasoningInput input, CancellationToken cancellationToken = default)
    {
        var insights = new List<ModelInsight>();

        try
        {
            var entities = input.Entities ?? [];
            var relationships = input.Relationships ?? [];

            // Check for contradictory entity names/types within the graph
            var nameLower = entities.Select(e => (Entity: e, NameLower: e.Name.ToLowerInvariant())).ToList();

            foreach (var (positive, negative) in ContradictoryPairs)
            {
                var positiveEntities = nameLower.Where(x => x.NameLower.Contains(positive)).Select(x => x.Entity).ToList();
                var negativeEntities = nameLower.Where(x => x.NameLower.Contains(negative)).Select(x => x.Entity).ToList();

                // Check if same base entity has contradictory states
                foreach (var posEntity in positiveEntities)
                {
                    var baseName = posEntity.Name.Replace(positive, "", StringComparison.OrdinalIgnoreCase).Trim();
                    var matchingNeg = negativeEntities
                        .FirstOrDefault(n => n.Name.Replace(negative, "", StringComparison.OrdinalIgnoreCase).Trim()
                            .Equals(baseName, StringComparison.OrdinalIgnoreCase));

                    if (matchingNeg != null)
                    {
                        insights.Add(new ModelInsight
                        {
                            Type = MultiModelInsightType.Conflict,
                            Message = $"Contradictory state detected: '{posEntity.Name}' vs '{matchingNeg.Name}'",
                            Confidence = 0.85f,
                            AffectedEntities = [posEntity.Id, matchingNeg.Id],
                            Details = new Dictionary<string, object>
                            {
                                ["contradictionType"] = $"{positive}/{negative}",
                                ["entities"] = new[] { posEntity.Name, matchingNeg.Name }
                            }
                        });
                    }
                }
            }

            // Check for bidirectional conflicting relationships
            var relPairs = relationships
                .GroupBy(r => (Math.Min(r.SourceEntityId.GetHashCode(), r.TargetEntityId.GetHashCode()),
                              Math.Max(r.SourceEntityId.GetHashCode(), r.TargetEntityId.GetHashCode())))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var pair in relPairs)
            {
                var types = pair.Select(r => r.RelationshipType).Distinct().ToList();
                if (types.Count > 1)
                {
                    // Check for conflicting relationship types
                    var hasConflict = types.Any(t1 => types.Any(t2 =>
                        IsConflictingRelationship(t1, t2)));

                    if (hasConflict)
                    {
                        var entityIds = pair.SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId }).Distinct().ToList();
                        insights.Add(new ModelInsight
                        {
                            Type = MultiModelInsightType.Conflict,
                            Message = $"Conflicting relationships found between entities: {string.Join(", ", types)}",
                            Confidence = 0.8f,
                            AffectedEntities = entityIds,
                            Details = new Dictionary<string, object>
                            {
                                ["relationshipTypes"] = types
                            }
                        });
                    }
                }
            }

            // Check for version conflicts (entities with version numbers in name)
            var versionPattern = new System.Text.RegularExpressions.Regex(@"[vV]?\d+\.?\d*\.?\d*");
            var versionEntities = entities
                .Where(e => versionPattern.IsMatch(e.Name))
                .ToList();

            var versionGroups = versionEntities
                .GroupBy(e =>
                {
                    // Remove version numbers to find base name
                    var baseName = versionPattern.Replace(e.Name, "").Trim();
                    return NormalizeName(baseName);
                })
                .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key))
                .ToList();

            foreach (var group in versionGroups)
            {
                insights.Add(new ModelInsight
                {
                    Type = MultiModelInsightType.Conflict,
                    Message = $"Multiple versions detected: {string.Join(", ", group.Select(e => e.Name))}",
                    Confidence = 0.75f,
                    AffectedEntities = group.Select(e => e.Id).ToList(),
                    Details = new Dictionary<string, object>
                    {
                        ["baseName"] = group.Key,
                        ["versions"] = group.Select(e => e.Name).ToList()
                    }
                });
            }

            return new ModelResult
            {
                ModelName = Name,
                Version = Version,
                Role = Role,
                Success = true,
                Insights = insights
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Contradiction reasoning failed");
            return new ModelResult
            {
                ModelName = Name,
                Version = Version,
                Role = Role,
                Success = false,
                Error = ex.Message,
                Insights = insights
            };
        }
    }

    private static bool IsConflictingRelationship(string type1, string type2)
    {
        var conflictPairs = new[]
        {
            ("DEPENDS_ON", "PROVIDES"),
            ("BLOCKS", "ENABLES"),
            ("REPLACES", "REPLACED_BY"),
            ("PRECEDES", "FOLLOWS")
        };

        var t1 = type1.ToUpperInvariant();
        var t2 = type2.ToUpperInvariant();

        return conflictPairs.Any(p =>
            (t1.Contains(p.Item1) && t2.Contains(p.Item2)) ||
            (t1.Contains(p.Item2) && t2.Contains(p.Item1)));
    }

    private static string NormalizeName(string name)
    {
        return string.Join("", name.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c)));
    }
}
