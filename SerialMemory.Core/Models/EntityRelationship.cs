namespace SerialMemory.Core.Models;

/// <summary>
/// Represents a relationship between two entities in the knowledge graph
/// </summary>
public class EntityRelationship
{
    public Guid Id { get; set; }
    public Guid SourceEntityId { get; set; }
    public Guid TargetEntityId { get; set; }
    public required string RelationshipType { get; set; } // WORKED_WITH, LIVES_IN, CREATED, etc.
    public float Confidence { get; set; } = 1.0f; // 0.0 to 1.0
    public DateTime CreatedAt { get; set; }
    public Guid? FirstSeenMemoryId { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }

    // Navigation properties
    public Entity? SourceEntity { get; set; }
    public Entity? TargetEntity { get; set; }
}
