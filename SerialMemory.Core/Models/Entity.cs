namespace SerialMemory.Core.Models;

/// <summary>
/// Represents an entity extracted from memories (people, places, organizations, etc.)
/// </summary>
public class Entity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string EntityType { get; set; } // PERSON, ORG, GPE, DATE, EVENT, PRODUCT, etc.
    public string? CanonicalName { get; set; } // Normalized form
    public DateTime CreatedAt { get; set; }
    public Guid? FirstSeenMemoryId { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }

    // Navigation properties
    public List<Memory> Memories { get; set; } = [];
    public List<EntityRelationship> OutgoingRelationships { get; set; } = [];
    public List<EntityRelationship> IncomingRelationships { get; set; } = [];
}
