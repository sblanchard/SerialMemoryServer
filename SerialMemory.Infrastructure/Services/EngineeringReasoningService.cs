using System.Text.RegularExpressions;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// Rule-based engineering reasoning service that infers risks, conflicts,
/// and insights from the knowledge graph.
/// </summary>
public sealed partial class EngineeringReasoningService(IKnowledgeGraphStore store) : IEngineeringReasoningService
{
    public async Task<EngineeringAnalysisResult> AnalyzeAsync(
        string? projectFilter = null,
        CancellationToken cancellationToken = default)
    {
        var entities = await store.GetAllEntitiesAsync(10000, cancellationToken);
        var relationships = await store.GetAllRelationshipsAsync(10000, cancellationToken);

        // Optionally filter by project
        if (!string.IsNullOrEmpty(projectFilter))
        {
            var projectEntity = entities.FirstOrDefault(e =>
                e.EntityType == "PROJECT" &&
                e.Name.Equals(projectFilter, StringComparison.OrdinalIgnoreCase));

            if (projectEntity != null)
            {
                // Get all entities connected to this project
                var connectedEntityIds = new HashSet<Guid> { projectEntity.Id };
                var relatedRelationships = relationships
                    .Where(r => r.SourceEntityId == projectEntity.Id || r.TargetEntityId == projectEntity.Id)
                    .ToList();

                foreach (var rel in relatedRelationships)
                {
                    connectedEntityIds.Add(rel.SourceEntityId);
                    connectedEntityIds.Add(rel.TargetEntityId);
                }

                entities = entities.Where(e => connectedEntityIds.Contains(e.Id)).ToList();
                relationships = relationships.Where(r =>
                    connectedEntityIds.Contains(r.SourceEntityId) ||
                    connectedEntityIds.Contains(r.TargetEntityId)).ToList();
            }
        }

        // Build lookup maps
        var entityMap = entities.ToDictionary(e => e.Id, e => e);

        // Run all inference rules
        var insights = new List<EngineeringInsight>();

        insights.AddRange(await DetectPowerIntegrityIssuesAsync(entities, relationships, entityMap, cancellationToken));
        insights.AddRange(await DetectSignalIntegrityIssuesAsync(entities, relationships, entityMap, cancellationToken));
        insights.AddRange(await DetectDependencyCorruptionAsync(entities, relationships, entityMap, cancellationToken));
        insights.AddRange(await DetectThermalRiskAsync(entities, relationships, entityMap, cancellationToken));

        return new EngineeringAnalysisResult
        {
            EntitiesAnalyzed = entities.Count,
            RelationshipsAnalyzed = relationships.Count,
            Insights = insights.OrderByDescending(i => i.Severity).ToList()
        };
    }

    public async Task<EngineeringAnalysisResult> AnalyzeMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        var memoryEntities = await store.GetEntitiesForMemoryAsync(memoryId, cancellationToken);
        if (memoryEntities.Count == 0)
        {
            return new EngineeringAnalysisResult
            {
                EntitiesAnalyzed = 0,
                RelationshipsAnalyzed = 0,
                Insights = []
            };
        }

        // Get all relationships for these entities
        var entityIds = memoryEntities.Select(e => e.Id).ToHashSet();
        var allRelationships = await store.GetAllRelationshipsAsync(10000, cancellationToken);
        var relationships = allRelationships
            .Where(r => entityIds.Contains(r.SourceEntityId) || entityIds.Contains(r.TargetEntityId))
            .ToList();

        // Expand to include connected entities
        var connectedEntityIds = new HashSet<Guid>(entityIds);
        foreach (var rel in relationships)
        {
            connectedEntityIds.Add(rel.SourceEntityId);
            connectedEntityIds.Add(rel.TargetEntityId);
        }

        var allEntities = await store.GetAllEntitiesAsync(10000, cancellationToken);
        var entities = allEntities.Where(e => connectedEntityIds.Contains(e.Id)).ToList();
        var entityMap = entities.ToDictionary(e => e.Id, e => e);

        // Run inference rules
        var insights = new List<EngineeringInsight>();
        insights.AddRange(await DetectPowerIntegrityIssuesAsync(entities, relationships, entityMap, cancellationToken));
        insights.AddRange(await DetectSignalIntegrityIssuesAsync(entities, relationships, entityMap, cancellationToken));
        insights.AddRange(await DetectDependencyCorruptionAsync(entities, relationships, entityMap, cancellationToken));
        insights.AddRange(await DetectThermalRiskAsync(entities, relationships, entityMap, cancellationToken));

        return new EngineeringAnalysisResult
        {
            EntitiesAnalyzed = entities.Count,
            RelationshipsAnalyzed = relationships.Count,
            Insights = insights.OrderByDescending(i => i.Severity).ToList()
        };
    }

    public async Task<List<EngineeringInsight>> DetectPowerIntegrityIssuesAsync(
        CancellationToken cancellationToken = default)
    {
        var entities = await store.GetAllEntitiesAsync(10000, cancellationToken);
        var relationships = await store.GetAllRelationshipsAsync(10000, cancellationToken);
        var entityMap = entities.ToDictionary(e => e.Id, e => e);
        return await DetectPowerIntegrityIssuesAsync(entities, relationships, entityMap, cancellationToken);
    }

    public async Task<List<EngineeringInsight>> DetectSignalIntegrityIssuesAsync(
        CancellationToken cancellationToken = default)
    {
        var entities = await store.GetAllEntitiesAsync(10000, cancellationToken);
        var relationships = await store.GetAllRelationshipsAsync(10000, cancellationToken);
        var entityMap = entities.ToDictionary(e => e.Id, e => e);
        return await DetectSignalIntegrityIssuesAsync(entities, relationships, entityMap, cancellationToken);
    }

    public async Task<List<EngineeringInsight>> DetectDependencyCorruptionAsync(
        CancellationToken cancellationToken = default)
    {
        var entities = await store.GetAllEntitiesAsync(10000, cancellationToken);
        var relationships = await store.GetAllRelationshipsAsync(10000, cancellationToken);
        var entityMap = entities.ToDictionary(e => e.Id, e => e);
        return await DetectDependencyCorruptionAsync(entities, relationships, entityMap, cancellationToken);
    }

    public async Task<List<EngineeringInsight>> DetectThermalRiskAsync(
        CancellationToken cancellationToken = default)
    {
        var entities = await store.GetAllEntitiesAsync(10000, cancellationToken);
        var relationships = await store.GetAllRelationshipsAsync(10000, cancellationToken);
        var entityMap = entities.ToDictionary(e => e.Id, e => e);
        return await DetectThermalRiskAsync(entities, relationships, entityMap, cancellationToken);
    }

    #region Power Integrity Rules

    private Task<List<EngineeringInsight>> DetectPowerIntegrityIssuesAsync(
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap,
        CancellationToken cancellationToken)
    {
        var insights = new List<EngineeringInsight>();

        // Rule 1: Voltage mismatch - component requires voltage different from power rail supply
        var powerRelationships = relationships
            .Where(r => r.RelationshipType is "POWERS" or "POWERED_BY" or "REQUIRES" or "FEEDS")
            .ToList();

        var voltageEntities = entities
            .Where(e => e.EntityType == "VOLTAGE")
            .ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);

        var powerRails = entities.Where(e => e.EntityType == "POWER_RAIL").ToList();
        var components = entities.Where(e => e.EntityType == "COMPONENT").ToList();

        foreach (var component in components)
        {
            // Find what powers this component
            var poweringRels = relationships
                .Where(r => r.TargetEntityId == component.Id &&
                           r.RelationshipType is "POWERS" or "FEEDS")
                .ToList();

            // Find what voltage this component requires
            var requiresRels = relationships
                .Where(r => r.SourceEntityId == component.Id && r.RelationshipType == "REQUIRES")
                .ToList();

            foreach (var powerRel in poweringRels)
            {
                if (!entityMap.TryGetValue(powerRel.SourceEntityId, out var powerSource))
                    continue;

                // Get supply voltage from power source
                var supplyVoltage = ExtractVoltageFromEntity(powerSource, entities, relationships, entityMap);

                // Get required voltage from component
                var requiredVoltage = ExtractRequiredVoltageFromComponent(component, entities, relationships, entityMap);

                if (supplyVoltage.HasValue && requiredVoltage.HasValue)
                {
                    // Check for voltage mismatch (>5% tolerance)
                    var diff = Math.Abs(supplyVoltage.Value - requiredVoltage.Value);
                    var tolerance = requiredVoltage.Value * 0.05f;

                    if (diff > tolerance)
                    {
                        insights.Add(new EngineeringInsight
                        {
                            Severity = InsightSeverity.Conflict,
                            Category = InsightCategory.PowerIntegrity,
                            Title = "Voltage Mismatch Detected",
                            Description = $"{component.Name} requires {requiredVoltage.Value:F1}V but {powerSource.Name} supplies {supplyVoltage.Value:F1}V",
                            InvolvedEntities =
                            [
                                new() { Id = component.Id, Name = component.Name, EntityType = component.EntityType, Role = "consumer" },
                                new() { Id = powerSource.Id, Name = powerSource.Name, EntityType = powerSource.EntityType, Role = "supplier" }
                            ],
                            RelationshipId = powerRel.Id,
                            Confidence = 0.9f,
                            Recommendations =
                            [
                                $"Add voltage regulator to convert {supplyVoltage.Value:F1}V to {requiredVoltage.Value:F1}V",
                                "Verify component absolute maximum ratings",
                                "Consider level-shifting circuit"
                            ],
                            Details = new Dictionary<string, object>
                            {
                                ["supply_voltage"] = supplyVoltage.Value,
                                ["required_voltage"] = requiredVoltage.Value,
                                ["difference"] = diff
                            }
                        });
                    }
                }
            }
        }

        // Rule 2: Overcurrent risk - current draw exceeds power rail capacity
        foreach (var powerRail in powerRails)
        {
            var currentCapacity = ExtractCurrentFromEntity(powerRail, entities, relationships, entityMap);
            if (!currentCapacity.HasValue) continue;

            var poweredComponents = relationships
                .Where(r => r.SourceEntityId == powerRail.Id && r.RelationshipType is "POWERS" or "FEEDS")
                .Select(r => entityMap.GetValueOrDefault(r.TargetEntityId))
                .Where(e => e != null)
                .ToList();

            float totalCurrentDraw = 0;
            var componentCurrents = new List<(Entity Component, float Current)>();

            foreach (var comp in poweredComponents)
            {
                var draw = ExtractCurrentDrawFromComponent(comp!, entities, relationships, entityMap);
                if (draw.HasValue)
                {
                    totalCurrentDraw += draw.Value;
                    componentCurrents.Add((comp!, draw.Value));
                }
            }

            if (totalCurrentDraw > currentCapacity.Value * 0.9f) // 90% threshold for warning
            {
                var severity = totalCurrentDraw > currentCapacity.Value
                    ? InsightSeverity.Risk
                    : InsightSeverity.Warning;

                insights.Add(new EngineeringInsight
                {
                    Severity = severity,
                    Category = InsightCategory.PowerIntegrity,
                    Title = "Overcurrent Risk",
                    Description = $"{powerRail.Name} capacity {currentCapacity.Value:F2}A may be exceeded by total load {totalCurrentDraw:F2}A",
                    InvolvedEntities = [new() { Id = powerRail.Id, Name = powerRail.Name, EntityType = powerRail.EntityType, Role = "power_rail" },
                        ..componentCurrents.Select(c => new InsightEntity
                        {
                            Id = c.Component.Id,
                            Name = c.Component.Name,
                            EntityType = c.Component.EntityType,
                            Role = "consumer"
                        })
                    ],
                    Confidence = 0.85f,
                    Recommendations =
                    [
                        "Add current-limiting circuit or fuse",
                        "Consider splitting load across multiple power rails",
                        "Verify peak vs. average current requirements"
                    ],
                    Details = new Dictionary<string, object>
                    {
                        ["rail_capacity_amps"] = currentCapacity.Value,
                        ["total_load_amps"] = totalCurrentDraw,
                        ["utilization_percent"] = (totalCurrentDraw / currentCapacity.Value) * 100
                    }
                });
            }
        }

        // Rule 3: Undervoltage chain - cascading power failure
        var undervoltageChains = DetectUndervoltageChains(entities, relationships, entityMap);
        insights.AddRange(undervoltageChains);

        return Task.FromResult(insights);
    }

    private List<EngineeringInsight> DetectUndervoltageChains(
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        var insights = new List<EngineeringInsight>();

        // Find power dependency chains
        var powerGraph = new Dictionary<Guid, List<Guid>>();
        foreach (var rel in relationships.Where(r => r.RelationshipType is "POWERS" or "FEEDS"))
        {
            if (!powerGraph.ContainsKey(rel.SourceEntityId))
                powerGraph[rel.SourceEntityId] = [];
            powerGraph[rel.SourceEntityId].Add(rel.TargetEntityId);
        }

        // DFS to find chains longer than 2 hops
        foreach (var startNode in powerGraph.Keys)
        {
            var chain = new List<Guid> { startNode };
            FindPowerChains(startNode, powerGraph, chain, entityMap, insights);
        }

        return insights;
    }

    private void FindPowerChains(
        Guid current,
        Dictionary<Guid, List<Guid>> graph,
        List<Guid> chain,
        Dictionary<Guid, Entity> entityMap,
        List<EngineeringInsight> insights)
    {
        if (!graph.TryGetValue(current, out var neighbors)) return;

        foreach (var neighbor in neighbors)
        {
            if (chain.Contains(neighbor)) continue; // Avoid cycles

            chain.Add(neighbor);

            if (chain.Count >= 3)
            {
                // Long power chain detected - potential undervoltage risk
                var chainEntities = chain
                    .Select(id => entityMap.GetValueOrDefault(id))
                    .Where(e => e != null)
                    .ToList();

                insights.Add(new EngineeringInsight
                {
                    Severity = InsightSeverity.Warning,
                    Category = InsightCategory.PowerIntegrity,
                    Title = "Long Power Chain Detected",
                    Description = $"Power chain of {chain.Count} hops may cause voltage drop: {string.Join(" → ", chainEntities.Select(e => e!.Name))}",
                    InvolvedEntities = chainEntities.Select((e, i) => new InsightEntity
                    {
                        Id = e!.Id,
                        Name = e.Name,
                        EntityType = e.EntityType,
                        Role = i == 0 ? "source" : i == chainEntities.Count - 1 ? "terminal" : "intermediate"
                    }).ToList(),
                    Confidence = 0.7f,
                    Recommendations =
                    [
                        "Measure actual voltage at terminal node",
                        "Consider direct power connection to reduce chain length",
                        "Add bulk/decoupling capacitors at intermediate nodes"
                    ],
                    Details = new Dictionary<string, object>
                    {
                        ["chain_length"] = chain.Count,
                        ["chain_nodes"] = chainEntities.Select(e => e!.Name).ToList()
                    }
                });
            }

            FindPowerChains(neighbor, graph, chain, entityMap, insights);
            chain.RemoveAt(chain.Count - 1);
        }
    }

    #endregion

    #region Signal Integrity Rules

    private Task<List<EngineeringInsight>> DetectSignalIntegrityIssuesAsync(
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap,
        CancellationToken cancellationToken)
    {
        var insights = new List<EngineeringInsight>();

        // Rule 1: Clock frequency mismatch
        var frequencyEntities = entities.Where(e => e.EntityType == "FREQUENCY").ToList();
        var signalEntities = entities.Where(e => e.EntityType == "SIGNAL").ToList();

        // Find COMMUNICATES_VIA relationships
        var commRelationships = relationships
            .Where(r => r.RelationshipType is "COMMUNICATES_VIA" or "INTERFACES_WITH")
            .ToList();

        foreach (var rel in commRelationships)
        {
            if (!entityMap.TryGetValue(rel.SourceEntityId, out var source) ||
                !entityMap.TryGetValue(rel.TargetEntityId, out var target))
                continue;

            // Check if components operate at different frequencies
            var sourceFreq = ExtractFrequencyFromEntity(source, entities, relationships, entityMap);
            var targetFreq = ExtractFrequencyFromEntity(target, entities, relationships, entityMap);

            if (sourceFreq.HasValue && targetFreq.HasValue && Math.Abs(sourceFreq.Value - targetFreq.Value) > 0.01)
            {
                insights.Add(new EngineeringInsight
                {
                    Severity = InsightSeverity.Conflict,
                    Category = InsightCategory.SignalIntegrity,
                    Title = "Clock Frequency Mismatch",
                    Description = $"{source.Name} operates at {FormatFrequency(sourceFreq.Value)} but {target.Name} expects {FormatFrequency(targetFreq.Value)}",
                    InvolvedEntities =
                    [
                        new() { Id = source.Id, Name = source.Name, EntityType = source.EntityType, Role = "source" },
                        new() { Id = target.Id, Name = target.Name, EntityType = target.EntityType, Role = "target" }
                    ],
                    RelationshipId = rel.Id,
                    Confidence = 0.85f,
                    Recommendations =
                    [
                        "Add clock divider or PLL to synchronize frequencies",
                        "Use asynchronous FIFO for clock domain crossing",
                        "Verify timing constraints at interface"
                    ],
                    Details = new Dictionary<string, object>
                    {
                        ["source_frequency_hz"] = sourceFreq.Value,
                        ["target_frequency_hz"] = targetFreq.Value
                    }
                });
            }
        }

        // Rule 2: Protocol mismatch
        var protocols = entities.Where(e => e.EntityType == "PROTOCOL").ToList();
        foreach (var rel in commRelationships)
        {
            if (!entityMap.TryGetValue(rel.SourceEntityId, out var source) ||
                !entityMap.TryGetValue(rel.TargetEntityId, out var target))
                continue;

            var sourceProtocols = GetEntityProtocols(source.Id, relationships, entityMap);
            var targetProtocols = GetEntityProtocols(target.Id, relationships, entityMap);

            if (sourceProtocols.Count > 0 && targetProtocols.Count > 0)
            {
                var commonProtocols = sourceProtocols.Intersect(targetProtocols, StringComparer.OrdinalIgnoreCase).ToList();
                if (commonProtocols.Count == 0)
                {
                    insights.Add(new EngineeringInsight
                    {
                        Severity = InsightSeverity.Conflict,
                        Category = InsightCategory.ProtocolMismatch,
                        Title = "Protocol Incompatibility",
                        Description = $"{source.Name} uses {string.Join(", ", sourceProtocols)} but {target.Name} requires {string.Join(", ", targetProtocols)}",
                        InvolvedEntities =
                        [
                            new() { Id = source.Id, Name = source.Name, EntityType = source.EntityType, Role = "source" },
                            new() { Id = target.Id, Name = target.Name, EntityType = target.EntityType, Role = "target" }
                        ],
                        RelationshipId = rel.Id,
                        Confidence = 0.9f,
                        Recommendations =
                        [
                            "Add protocol converter/bridge",
                            "Verify if components support multiple protocols",
                            "Consider different interface method"
                        ],
                        Details = new Dictionary<string, object>
                        {
                            ["source_protocols"] = sourceProtocols,
                            ["target_protocols"] = targetProtocols
                        }
                    });
                }
            }
        }

        // Rule 3: Baud rate mismatch (for serial protocols)
        foreach (var rel in commRelationships)
        {
            if (!entityMap.TryGetValue(rel.SourceEntityId, out var source) ||
                !entityMap.TryGetValue(rel.TargetEntityId, out var target))
                continue;

            var sourceBaud = ExtractBaudRateFromEntity(source, entities, relationships, entityMap);
            var targetBaud = ExtractBaudRateFromEntity(target, entities, relationships, entityMap);

            if (sourceBaud.HasValue && targetBaud.HasValue && sourceBaud.Value != targetBaud.Value)
            {
                insights.Add(new EngineeringInsight
                {
                    Severity = InsightSeverity.Conflict,
                    Category = InsightCategory.SignalIntegrity,
                    Title = "Baud Rate Mismatch",
                    Description = $"{source.Name} configured for {sourceBaud.Value} baud but {target.Name} expects {targetBaud.Value} baud",
                    InvolvedEntities =
                    [
                        new() { Id = source.Id, Name = source.Name, EntityType = source.EntityType, Role = "source" },
                        new() { Id = target.Id, Name = target.Name, EntityType = target.EntityType, Role = "target" }
                    ],
                    RelationshipId = rel.Id,
                    Confidence = 0.95f,
                    Recommendations =
                    [
                        "Configure both devices to use the same baud rate",
                        "Check if auto-baud detection is available",
                        "Verify baud rate tolerance specifications"
                    ],
                    Details = new Dictionary<string, object>
                    {
                        ["source_baud"] = sourceBaud.Value,
                        ["target_baud"] = targetBaud.Value
                    }
                });
            }
        }

        return Task.FromResult(insights);
    }

    #endregion

    #region Dependency Corruption Rules

    private Task<List<EngineeringInsight>> DetectDependencyCorruptionAsync(
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap,
        CancellationToken cancellationToken)
    {
        var insights = new List<EngineeringInsight>();

        // Find all INCIDENT entities
        var incidents = entities.Where(e => e.EntityType == "INCIDENT").ToList();
        var incidentAffectedEntities = new HashSet<Guid>();

        // Find entities affected by incidents (via CAUSED, AFFECTS, etc.)
        foreach (var incident in incidents)
        {
            var affectedRels = relationships
                .Where(r => r.SourceEntityId == incident.Id &&
                           r.RelationshipType is "CAUSED" or "AFFECTS" or "IMPACTS")
                .ToList();

            foreach (var rel in affectedRels)
            {
                incidentAffectedEntities.Add(rel.TargetEntityId);
            }

            // Also check for entities that CAUSED the incident (they're implicated)
            var causingRels = relationships
                .Where(r => r.TargetEntityId == incident.Id && r.RelationshipType == "CAUSED")
                .ToList();

            foreach (var rel in causingRels)
            {
                incidentAffectedEntities.Add(rel.SourceEntityId);
            }
        }

        // Find dependency chains that lead to incident-affected entities
        var dependencyRels = relationships
            .Where(r => r.RelationshipType is "DEPENDS_ON" or "USES" or "CALLS" or "REQUIRES")
            .ToList();

        foreach (var rel in dependencyRels)
        {
            if (!incidentAffectedEntities.Contains(rel.TargetEntityId)) continue;

            if (!entityMap.TryGetValue(rel.SourceEntityId, out var dependent) ||
                !entityMap.TryGetValue(rel.TargetEntityId, out var dependency))
                continue;

            // Find the related incident
            var relatedIncident = incidents.FirstOrDefault(inc =>
                relationships.Any(r =>
                    (r.SourceEntityId == inc.Id && r.TargetEntityId == dependency.Id) ||
                    (r.TargetEntityId == inc.Id && r.SourceEntityId == dependency.Id)));

            insights.Add(new EngineeringInsight
            {
                Severity = InsightSeverity.Risk,
                Category = InsightCategory.DependencyCorruption,
                Title = "Cascading Dependency Risk",
                Description = $"{dependent.Name} depends on {dependency.Name} which is affected by an incident",
                InvolvedEntities = BuildDependencyEntities(dependent, dependency, relatedIncident),
                RelationshipId = rel.Id,
                Confidence = 0.9f,
                Recommendations =
                [
                    "Implement fallback/redundancy for critical dependency",
                    "Add circuit breaker pattern to isolate failures",
                    "Monitor dependent service health",
                    "Consider graceful degradation strategy"
                ],
                Details = new Dictionary<string, object>
                {
                    ["dependency_type"] = rel.RelationshipType,
                    ["incident_entity"] = relatedIncident?.Name ?? "unknown"
                }
            });
        }

        // Find multi-hop dependency chains to incident-affected entities
        var cascadeInsights = FindDependencyCascades(entities, relationships, entityMap, incidentAffectedEntities);
        insights.AddRange(cascadeInsights);

        return Task.FromResult(insights);
    }

    private List<EngineeringInsight> FindDependencyCascades(
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap,
        HashSet<Guid> incidentAffectedEntities)
    {
        var insights = new List<EngineeringInsight>();

        // Build dependency graph
        var depGraph = new Dictionary<Guid, List<Guid>>();
        foreach (var rel in relationships.Where(r => r.RelationshipType is "DEPENDS_ON" or "USES" or "CALLS" or "REQUIRES"))
        {
            if (!depGraph.ContainsKey(rel.SourceEntityId))
                depGraph[rel.SourceEntityId] = [];
            depGraph[rel.SourceEntityId].Add(rel.TargetEntityId);
        }

        // For each non-incident entity, trace dependencies
        var visitedInPaths = new Dictionary<Guid, List<List<Guid>>>();

        foreach (var startId in depGraph.Keys)
        {
            if (incidentAffectedEntities.Contains(startId)) continue;

            var paths = new List<List<Guid>>();
            var currentPath = new List<Guid> { startId };
            FindPathsToIncident(startId, depGraph, incidentAffectedEntities, currentPath, paths, new HashSet<Guid>());

            if (paths.Any(p => p.Count > 2)) // Multi-hop chains
            {
                var longestPath = paths.OrderByDescending(p => p.Count).First();
                var pathEntities = longestPath
                    .Select(id => entityMap.GetValueOrDefault(id))
                    .Where(e => e != null)
                    .ToList();

                insights.Add(new EngineeringInsight
                {
                    Severity = InsightSeverity.Warning,
                    Category = InsightCategory.DependencyCorruption,
                    Title = "Transitive Dependency Risk",
                    Description = $"Dependency chain of {longestPath.Count} hops leads to incident-affected entity: {string.Join(" → ", pathEntities.Select(e => e!.Name))}",
                    InvolvedEntities = pathEntities.Select((e, i) => new InsightEntity
                    {
                        Id = e!.Id,
                        Name = e.Name,
                        EntityType = e.EntityType,
                        Role = i == 0 ? "root" : i == pathEntities.Count - 1 ? "affected" : "intermediate"
                    }).ToList(),
                    Confidence = 0.75f,
                    Recommendations =
                    [
                        "Review dependency chain for single points of failure",
                        "Consider caching or offline capability for intermediate services",
                        "Implement health checks throughout the chain"
                    ],
                    Details = new Dictionary<string, object>
                    {
                        ["chain_length"] = longestPath.Count,
                        ["path"] = pathEntities.Select(e => e!.Name).ToList()
                    }
                });
            }
        }

        return insights;
    }

    private void FindPathsToIncident(
        Guid current,
        Dictionary<Guid, List<Guid>> graph,
        HashSet<Guid> targets,
        List<Guid> currentPath,
        List<List<Guid>> paths,
        HashSet<Guid> visited)
    {
        if (visited.Contains(current)) return;
        visited.Add(current);

        if (targets.Contains(current) && currentPath.Count > 1)
        {
            paths.Add([.. currentPath]);
            return;
        }

        if (!graph.TryGetValue(current, out var neighbors)) return;

        foreach (var neighbor in neighbors)
        {
            currentPath.Add(neighbor);
            FindPathsToIncident(neighbor, graph, targets, currentPath, paths, visited);
            currentPath.RemoveAt(currentPath.Count - 1);
        }

        visited.Remove(current);
    }

    #endregion

    #region Thermal Risk Rules

    private Task<List<EngineeringInsight>> DetectThermalRiskAsync(
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap,
        CancellationToken cancellationToken)
    {
        var insights = new List<EngineeringInsight>();

        var temperatureEntities = entities.Where(e => e.EntityType == "TEMPERATURE").ToList();
        var components = entities.Where(e => e.EntityType == "COMPONENT").ToList();

        // Find components with temperature ratings/limits
        foreach (var component in components)
        {
            var maxTemp = ExtractMaxTemperatureFromComponent(component, entities, relationships, entityMap);
            var operatingTemp = ExtractOperatingTemperatureFromComponent(component, entities, relationships, entityMap);

            if (maxTemp.HasValue && operatingTemp.HasValue)
            {
                var margin = maxTemp.Value - operatingTemp.Value;
                var utilizationPercent = (operatingTemp.Value / maxTemp.Value) * 100;

                if (operatingTemp.Value > maxTemp.Value)
                {
                    insights.Add(new EngineeringInsight
                    {
                        Severity = InsightSeverity.Risk,
                        Category = InsightCategory.ThermalRisk,
                        Title = "Temperature Exceeds Maximum Rating",
                        Description = $"{component.Name} operating at {operatingTemp.Value:F1}°C exceeds max rating of {maxTemp.Value:F1}°C",
                        InvolvedEntities =
                        [
                            new() { Id = component.Id, Name = component.Name, EntityType = component.EntityType, Role = "component" }
                        ],
                        Confidence = 0.95f,
                        Recommendations =
                        [
                            "Add heatsink or thermal interface material",
                            "Improve airflow/ventilation",
                            "Reduce operating frequency/voltage to lower power dissipation",
                            "Consider component with higher temperature rating"
                        ],
                        Details = new Dictionary<string, object>
                        {
                            ["operating_temp_c"] = operatingTemp.Value,
                            ["max_temp_c"] = maxTemp.Value,
                            ["exceeded_by_c"] = operatingTemp.Value - maxTemp.Value
                        }
                    });
                }
                else if (margin < 10) // Less than 10°C margin
                {
                    insights.Add(new EngineeringInsight
                    {
                        Severity = InsightSeverity.Warning,
                        Category = InsightCategory.ThermalRisk,
                        Title = "Low Thermal Margin",
                        Description = $"{component.Name} has only {margin:F1}°C margin below max rating ({operatingTemp.Value:F1}°C / {maxTemp.Value:F1}°C)",
                        InvolvedEntities =
                        [
                            new() { Id = component.Id, Name = component.Name, EntityType = component.EntityType, Role = "component" }
                        ],
                        Confidence = 0.85f,
                        Recommendations =
                        [
                            "Consider adding thermal management (passive or active cooling)",
                            "Monitor temperature during extended operation",
                            "Derate component if possible"
                        ],
                        Details = new Dictionary<string, object>
                        {
                            ["operating_temp_c"] = operatingTemp.Value,
                            ["max_temp_c"] = maxTemp.Value,
                            ["margin_c"] = margin,
                            ["utilization_percent"] = utilizationPercent
                        }
                    });
                }
            }
        }

        // Check for thermal conflicts (components near heat sources)
        var heatSourceRelationships = relationships
            .Where(r => r.RelationshipType is "LOCATED_AT" or "MOUNTED_ON" or "ATTACHED_TO")
            .ToList();

        // Find temperature-sensitive components near high-heat components
        // This is a simplified check - in reality you'd want spatial data
        foreach (var rel in heatSourceRelationships)
        {
            if (!entityMap.TryGetValue(rel.SourceEntityId, out var source) ||
                !entityMap.TryGetValue(rel.TargetEntityId, out var target))
                continue;

            // Check if both components have temperature data
            var sourceMaxTemp = ExtractMaxTemperatureFromComponent(source, entities, relationships, entityMap);
            var targetMaxTemp = ExtractMaxTemperatureFromComponent(target, entities, relationships, entityMap);

            if (sourceMaxTemp.HasValue && targetMaxTemp.HasValue)
            {
                // Flag if low-temp-tolerance component is mounted on/near high-power component
                if (sourceMaxTemp.Value < 85 && targetMaxTemp.Value > 100)
                {
                    insights.Add(new EngineeringInsight
                    {
                        Severity = InsightSeverity.Warning,
                        Category = InsightCategory.ThermalRisk,
                        Title = "Thermal Proximity Concern",
                        Description = $"{source.Name} (max {sourceMaxTemp.Value:F0}°C) is mounted on/near {target.Name} (max {targetMaxTemp.Value:F0}°C)",
                        InvolvedEntities =
                        [
                            new() { Id = source.Id, Name = source.Name, EntityType = source.EntityType, Role = "sensitive_component" },
                            new() { Id = target.Id, Name = target.Name, EntityType = target.EntityType, Role = "heat_source" }
                        ],
                        RelationshipId = rel.Id,
                        Confidence = 0.7f,
                        Recommendations =
                        [
                            "Increase physical separation between components",
                            "Add thermal barrier or insulation",
                            "Verify thermal simulation of board layout"
                        ],
                        Details = new Dictionary<string, object>
                        {
                            ["sensitive_component_max_temp_c"] = sourceMaxTemp.Value,
                            ["heat_source_max_temp_c"] = targetMaxTemp.Value
                        }
                    });
                }
            }
        }

        return Task.FromResult(insights);
    }

    #endregion

    #region Helper Methods

    private static List<InsightEntity> BuildDependencyEntities(Entity dependent, Entity dependency, Entity? relatedIncident)
    {
        var result = new List<InsightEntity>
        {
            new() { Id = dependent.Id, Name = dependent.Name, EntityType = dependent.EntityType, Role = "dependent" },
            new() { Id = dependency.Id, Name = dependency.Name, EntityType = dependency.EntityType, Role = "dependency" }
        };

        if (relatedIncident != null)
        {
            result.Add(new InsightEntity
            {
                Id = relatedIncident.Id,
                Name = relatedIncident.Name,
                EntityType = relatedIncident.EntityType,
                Role = "incident"
            });
        }

        return result;
    }

    // Regex patterns for parsing values from entity names/metadata
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*V", RegexOptions.IgnoreCase)]
    private static partial Regex VoltageRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(m?A)", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(Hz|kHz|MHz|GHz)", RegexOptions.IgnoreCase)]
    private static partial Regex FrequencyRegex();

    [GeneratedRegex(@"(\d+)\s*baud", RegexOptions.IgnoreCase)]
    private static partial Regex BaudRegex();

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s*[°]?C", RegexOptions.IgnoreCase)]
    private static partial Regex TemperatureRegex();

    private float? ExtractVoltageFromEntity(
        Entity entity,
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        // Try to extract from entity name
        var match = VoltageRegex().Match(entity.Name);
        if (match.Success && float.TryParse(match.Groups[1].Value, out var voltage))
            return voltage;

        // Try to find related VOLTAGE entity
        var voltageRel = relationships.FirstOrDefault(r =>
            r.SourceEntityId == entity.Id &&
            entityMap.TryGetValue(r.TargetEntityId, out var target) &&
            target.EntityType == "VOLTAGE");

        if (voltageRel != null && entityMap.TryGetValue(voltageRel.TargetEntityId, out var voltageEntity))
        {
            match = VoltageRegex().Match(voltageEntity.Name);
            if (match.Success && float.TryParse(match.Groups[1].Value, out voltage))
                return voltage;
        }

        // Try metadata
        if (entity.Metadata?.TryGetValue("voltage", out var v) == true && v is double dv)
            return (float)dv;

        return null;
    }

    private float? ExtractRequiredVoltageFromComponent(
        Entity component,
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        // Look for REQUIRES relationship to VOLTAGE entity
        var requiresRel = relationships.FirstOrDefault(r =>
            r.SourceEntityId == component.Id &&
            r.RelationshipType == "REQUIRES" &&
            entityMap.TryGetValue(r.TargetEntityId, out var target) &&
            target.EntityType == "VOLTAGE");

        if (requiresRel != null && entityMap.TryGetValue(requiresRel.TargetEntityId, out var voltageEntity))
        {
            var match = VoltageRegex().Match(voltageEntity.Name);
            if (match.Success && float.TryParse(match.Groups[1].Value, out var voltage))
                return voltage;
        }

        // Try metadata
        if (component.Metadata?.TryGetValue("required_voltage", out var v) == true && v is double dv)
            return (float)dv;

        return null;
    }

    private float? ExtractCurrentFromEntity(
        Entity entity,
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        var match = CurrentRegex().Match(entity.Name);
        if (match.Success && float.TryParse(match.Groups[1].Value, out var current))
        {
            var unit = match.Groups[2].Value.ToLowerInvariant();
            return unit == "ma" ? current / 1000f : current;
        }

        // Try metadata
        if (entity.Metadata?.TryGetValue("current_capacity_amps", out var c) == true && c is double dc)
            return (float)dc;

        return null;
    }

    private float? ExtractCurrentDrawFromComponent(
        Entity component,
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        // Look for CURRENT entity related to this component
        var currentRel = relationships.FirstOrDefault(r =>
            r.SourceEntityId == component.Id &&
            entityMap.TryGetValue(r.TargetEntityId, out var target) &&
            target.EntityType == "CURRENT");

        if (currentRel != null && entityMap.TryGetValue(currentRel.TargetEntityId, out var currentEntity))
        {
            var match = CurrentRegex().Match(currentEntity.Name);
            if (match.Success && float.TryParse(match.Groups[1].Value, out var current))
            {
                var unit = match.Groups[2].Value.ToLowerInvariant();
                return unit == "ma" ? current / 1000f : current;
            }
        }

        // Try metadata
        if (component.Metadata?.TryGetValue("current_draw_amps", out var c) == true && c is double dc)
            return (float)dc;

        return null;
    }

    private float? ExtractFrequencyFromEntity(
        Entity entity,
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        // Look for OPERATES_AT relationship to FREQUENCY entity
        var freqRel = relationships.FirstOrDefault(r =>
            r.SourceEntityId == entity.Id &&
            r.RelationshipType == "OPERATES_AT" &&
            entityMap.TryGetValue(r.TargetEntityId, out var target) &&
            target.EntityType == "FREQUENCY");

        if (freqRel != null && entityMap.TryGetValue(freqRel.TargetEntityId, out var freqEntity))
        {
            return ParseFrequency(freqEntity.Name);
        }

        // Try entity name
        var freq = ParseFrequency(entity.Name);
        if (freq.HasValue) return freq;

        // Try metadata
        if (entity.Metadata?.TryGetValue("frequency_hz", out var f) == true && f is double df)
            return (float)df;

        return null;
    }

    private float? ParseFrequency(string text)
    {
        var match = FrequencyRegex().Match(text);
        if (!match.Success || !float.TryParse(match.Groups[1].Value, out var value))
            return null;

        var unit = match.Groups[2].Value.ToLowerInvariant();
        return unit switch
        {
            "khz" => value * 1000,
            "mhz" => value * 1_000_000,
            "ghz" => value * 1_000_000_000,
            _ => value
        };
    }

    private string FormatFrequency(float hz)
    {
        return hz switch
        {
            >= 1_000_000_000 => $"{hz / 1_000_000_000:F2} GHz",
            >= 1_000_000 => $"{hz / 1_000_000:F2} MHz",
            >= 1_000 => $"{hz / 1_000:F2} kHz",
            _ => $"{hz:F0} Hz"
        };
    }

    private List<string> GetEntityProtocols(
        Guid entityId,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        var protocols = new List<string>();

        var protocolRels = relationships
            .Where(r => r.SourceEntityId == entityId &&
                       r.RelationshipType is "COMMUNICATES_VIA" or "USES" or "IMPLEMENTS" &&
                       entityMap.TryGetValue(r.TargetEntityId, out var target) &&
                       target.EntityType == "PROTOCOL")
            .ToList();

        foreach (var rel in protocolRels)
        {
            if (entityMap.TryGetValue(rel.TargetEntityId, out var protocol))
            {
                protocols.Add(protocol.Name);
            }
        }

        return protocols;
    }

    private int? ExtractBaudRateFromEntity(
        Entity entity,
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        var match = BaudRegex().Match(entity.Name);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var baud))
            return baud;

        // Try metadata
        if (entity.Metadata?.TryGetValue("baud_rate", out var b) == true)
        {
            if (b is int ib) return ib;
            if (b is double db) return (int)db;
            if (b is string sb && int.TryParse(sb, out var parsed)) return parsed;
        }

        return null;
    }

    private float? ExtractMaxTemperatureFromComponent(
        Entity component,
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        // Look for RATED_FOR relationship to TEMPERATURE entity
        var tempRel = relationships.FirstOrDefault(r =>
            r.SourceEntityId == component.Id &&
            r.RelationshipType == "RATED_FOR" &&
            entityMap.TryGetValue(r.TargetEntityId, out var target) &&
            target.EntityType == "TEMPERATURE");

        if (tempRel != null && entityMap.TryGetValue(tempRel.TargetEntityId, out var tempEntity))
        {
            var match = TemperatureRegex().Match(tempEntity.Name);
            if (match.Success && float.TryParse(match.Groups[1].Value, out var temp))
                return temp;
        }

        // Try metadata
        if (component.Metadata?.TryGetValue("max_temp_c", out var t) == true && t is double dt)
            return (float)dt;

        return null;
    }

    private float? ExtractOperatingTemperatureFromComponent(
        Entity component,
        List<Entity> entities,
        List<EntityRelationship> relationships,
        Dictionary<Guid, Entity> entityMap)
    {
        // Look for OPERATES_AT relationship to TEMPERATURE entity
        var tempRel = relationships.FirstOrDefault(r =>
            r.SourceEntityId == component.Id &&
            r.RelationshipType == "OPERATES_AT" &&
            entityMap.TryGetValue(r.TargetEntityId, out var target) &&
            target.EntityType == "TEMPERATURE");

        if (tempRel != null && entityMap.TryGetValue(tempRel.TargetEntityId, out var tempEntity))
        {
            var match = TemperatureRegex().Match(tempEntity.Name);
            if (match.Success && float.TryParse(match.Groups[1].Value, out var temp))
                return temp;
        }

        // Try metadata
        if (component.Metadata?.TryGetValue("operating_temp_c", out var t) == true && t is double dt)
            return (float)dt;

        return null;
    }

    #endregion
}
