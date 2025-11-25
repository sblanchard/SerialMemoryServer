namespace SerialMemory.Core.Models;

/// <summary>
/// Represents a memory/episode in the knowledge graph with semantic embedding
/// </summary>
public class Memory
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public float[]? Embedding { get; set; } // 384-dim vector for all-MiniLM-L6-v2
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Source { get; set; } // Where memory came from (e.g., "claude-desktop")
    public Guid? ConversationSessionId { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }

    // Navigation properties
    public List<Entity> Entities { get; set; } = [];
    public ConversationSession? ConversationSession { get; set; }
}
