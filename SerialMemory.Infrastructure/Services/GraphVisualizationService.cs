using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// Service for generating graph visualization data with integrated reasoning overlays.
/// </summary>
public sealed class GraphVisualizationService : IGraphVisualizationService
{
    private readonly IKnowledgeGraphStore _store;
    private readonly IEngineeringReasoningService _reasoningService;

    // Software entity types
    private static readonly HashSet<string> SoftwareEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "REPO", "PROJECT", "SERVICE", "MODULE", "API", "DATABASE", "TABLE",
        "MESSAGE", "CONFIG", "ENV", "VERSION", "TECH", "DEPLOYMENT", "INCIDENT", "BUG", "TEST"
    };

    // Engineering/hardware entity types
    private static readonly HashSet<string> EngineeringEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMPONENT", "ASSEMBLY", "MATERIAL", "SIGNAL", "POWER_RAIL", "VOLTAGE", "CURRENT",
        "FREQUENCY", "TEMPERATURE", "PRESSURE", "FORCE", "TORQUE", "DIMENSION", "TOLERANCE",
        "STANDARD", "TOOL", "LOCATION", "PCB", "SCHEMATIC", "BOM", "PART_NUMBER", "DATASHEET",
        "FIRMWARE", "REGISTER", "PIN", "CONNECTOR", "PROTOCOL", "MANUFACTURER", "SENSOR",
        "ACTUATOR", "ENCLOSURE", "CABLE", "ANTENNA"
    };

    public GraphVisualizationService(
        IKnowledgeGraphStore store,
        IEngineeringReasoningService reasoningService)
    {
        _store = store;
        _reasoningService = reasoningService;
    }

    public async Task<GraphVisualizationResult> GenerateVisualizationAsync(
        VisualizationMode mode = VisualizationMode.Mixed,
        string? projectFilter = null,
        bool includeOverlays = true,
        CancellationToken cancellationToken = default)
    {
        var entities = await _store.GetAllEntitiesAsync(10000, cancellationToken);
        var relationships = await _store.GetAllRelationshipsAsync(10000, cancellationToken);

        // Filter by project if specified
        if (!string.IsNullOrEmpty(projectFilter))
        {
            var projectEntity = entities.FirstOrDefault(e =>
                e.EntityType == "PROJECT" &&
                e.Name.Equals(projectFilter, StringComparison.OrdinalIgnoreCase));

            if (projectEntity != null)
            {
                var connectedEntityIds = GetConnectedEntityIds(projectEntity.Id, relationships);
                entities = entities.Where(e => connectedEntityIds.Contains(e.Id)).ToList();
                relationships = relationships.Where(r =>
                    connectedEntityIds.Contains(r.SourceEntityId) ||
                    connectedEntityIds.Contains(r.TargetEntityId)).ToList();
            }
        }

        // Filter by mode
        entities = FilterEntitiesByMode(entities, mode);

        // Build entity lookup
        var entityIds = entities.Select(e => e.Id).ToHashSet();

        // Filter relationships to only include those between visible entities
        relationships = relationships.Where(r =>
            entityIds.Contains(r.SourceEntityId) &&
            entityIds.Contains(r.TargetEntityId)).ToList();

        // Generate nodes
        var nodes = entities.Select(e => new VisualizationNode
        {
            Id = e.Id,
            Label = e.Name,
            Type = e.EntityType,
            Category = GetEntityCategory(e.EntityType),
            Metadata = e.Metadata
        }).ToList();

        // Generate links
        var links = relationships.Select(r => new VisualizationLink
        {
            Source = r.SourceEntityId,
            Target = r.TargetEntityId,
            Type = r.RelationshipType,
            Category = GetRelationshipCategory(r.RelationshipType),
            Weight = r.Confidence
        }).ToList();

        // Generate overlays from reasoning
        var overlays = new List<VisualizationOverlay>();
        if (includeOverlays)
        {
            overlays = await GenerateOverlaysAsync(projectFilter, cancellationToken);
        }

        return new GraphVisualizationResult
        {
            Nodes = nodes,
            Links = links,
            Overlays = overlays,
            Mode = mode
        };
    }

    public async Task<GraphVisualizationResult> GenerateMemoryVisualizationAsync(
        Guid memoryId,
        VisualizationMode mode = VisualizationMode.Mixed,
        bool includeOverlays = true,
        CancellationToken cancellationToken = default)
    {
        var memoryEntities = await _store.GetEntitiesForMemoryAsync(memoryId, cancellationToken);
        if (memoryEntities.Count == 0)
        {
            return new GraphVisualizationResult { Mode = mode };
        }

        // Get all relationships for these entities
        var entityIds = memoryEntities.Select(e => e.Id).ToHashSet();
        var allRelationships = await _store.GetAllRelationshipsAsync(10000, cancellationToken);

        // Include entities connected via relationships
        var connectedEntityIds = new HashSet<Guid>(entityIds);
        foreach (var rel in allRelationships.Where(r =>
            entityIds.Contains(r.SourceEntityId) || entityIds.Contains(r.TargetEntityId)))
        {
            connectedEntityIds.Add(rel.SourceEntityId);
            connectedEntityIds.Add(rel.TargetEntityId);
        }

        // Get all connected entities
        var allEntities = await _store.GetAllEntitiesAsync(10000, cancellationToken);
        var entities = allEntities.Where(e => connectedEntityIds.Contains(e.Id)).ToList();

        // Filter by mode
        entities = FilterEntitiesByMode(entities, mode);
        var visibleIds = entities.Select(e => e.Id).ToHashSet();

        // Filter relationships
        var relationships = allRelationships.Where(r =>
            visibleIds.Contains(r.SourceEntityId) &&
            visibleIds.Contains(r.TargetEntityId)).ToList();

        // Generate nodes
        var nodes = entities.Select(e => new VisualizationNode
        {
            Id = e.Id,
            Label = e.Name,
            Type = e.EntityType,
            Category = GetEntityCategory(e.EntityType),
            Metadata = e.Metadata
        }).ToList();

        // Generate links
        var links = relationships.Select(r => new VisualizationLink
        {
            Source = r.SourceEntityId,
            Target = r.TargetEntityId,
            Type = r.RelationshipType,
            Category = GetRelationshipCategory(r.RelationshipType),
            Weight = r.Confidence
        }).ToList();

        // Generate overlays from reasoning
        var overlays = new List<VisualizationOverlay>();
        if (includeOverlays)
        {
            var analysisResult = await _reasoningService.AnalyzeMemoryAsync(memoryId, cancellationToken);
            overlays = ConvertInsightsToOverlays(analysisResult.Insights);
        }

        return new GraphVisualizationResult
        {
            Nodes = nodes,
            Links = links,
            Overlays = overlays,
            Mode = mode
        };
    }

    private HashSet<Guid> GetConnectedEntityIds(Guid rootId, List<EntityRelationship> relationships)
    {
        var connected = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();

            var neighbors = relationships
                .Where(r => r.SourceEntityId == currentId || r.TargetEntityId == currentId)
                .SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId })
                .Where(id => !connected.Contains(id));

            foreach (var neighbor in neighbors)
            {
                connected.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return connected;
    }

    private List<Entity> FilterEntitiesByMode(List<Entity> entities, VisualizationMode mode)
    {
        return mode switch
        {
            VisualizationMode.Software => entities
                .Where(e => SoftwareEntityTypes.Contains(e.EntityType) ||
                           !EngineeringEntityTypes.Contains(e.EntityType))
                .ToList(),
            VisualizationMode.Hardware => entities
                .Where(e => EngineeringEntityTypes.Contains(e.EntityType))
                .ToList(),
            _ => entities // Mixed - return all
        };
    }

    private static string GetEntityCategory(string entityType)
    {
        if (EngineeringEntityTypes.Contains(entityType))
            return "engineering";
        if (SoftwareEntityTypes.Contains(entityType))
            return "software";
        return "generic";
    }

    private static string GetRelationshipCategory(string relationshipType)
    {
        var physicalTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CONNECTS_TO", "MOUNTED_ON", "FEEDS", "CONVERTS", "MEASURES", "CONTROLS",
            "CARRIES", "REQUIRES", "LIMITED_BY", "COMPLIES_WITH", "POWERS", "GROUNDS",
            "DRIVES", "CONTAINED_IN", "ATTACHED_TO", "COMMUNICATES_VIA", "OPERATES_AT",
            "RATED_FOR", "ROUTES_TO", "LOCATED_AT"
        };

        return physicalTypes.Contains(relationshipType) ? "physical" : "logical";
    }

    private async Task<List<VisualizationOverlay>> GenerateOverlaysAsync(
        string? projectFilter,
        CancellationToken cancellationToken)
    {
        var analysisResult = await _reasoningService.AnalyzeAsync(projectFilter, cancellationToken);
        return ConvertInsightsToOverlays(analysisResult.Insights);
    }

    private static List<VisualizationOverlay> ConvertInsightsToOverlays(List<EngineeringInsight> insights)
    {
        var overlays = new List<VisualizationOverlay>();

        foreach (var insight in insights)
        {
            foreach (var entity in insight.InvolvedEntities)
            {
                overlays.Add(new VisualizationOverlay
                {
                    EntityId = entity.Id,
                    Type = insight.Severity switch
                    {
                        InsightSeverity.Risk => "risk",
                        InsightSeverity.Conflict => "conflict",
                        InsightSeverity.Warning => "warning",
                        _ => "info"
                    },
                    Message = $"{insight.Title}: {insight.Description}",
                    Confidence = insight.Confidence
                });
            }
        }

        return overlays;
    }
}
