namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Entity extracted from text
/// </summary>
public record ExtractedEntity(string Text, string Label, int Start, int End, float Confidence = 1.0f);

/// <summary>
/// Relationship between entities
/// </summary>
public record ExtractedRelationship(string SourceEntity, string TargetEntity, string RelationType, float Confidence = 0.8f);

/// <summary>
/// Service for extracting entities and relationships from text
/// </summary>
public interface IEntityExtractionService
{
    /// <summary>
    /// Extract entities from text
    /// </summary>
    Task<List<ExtractedEntity>> ExtractEntitiesAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extract relationships between entities
    /// </summary>
    Task<List<ExtractedRelationship>> ExtractRelationshipsAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extract both entities and relationships in one pass
    /// </summary>
    Task<(List<ExtractedEntity> Entities, List<ExtractedRelationship> Relationships)> ExtractAllAsync(string text, CancellationToken cancellationToken = default);
}
