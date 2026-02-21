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
    public string MemoryType { get; set; } = "knowledge"; // error, decision, pattern, learning, knowledge, session_summary, auto_capture

    // Structured observation fields (P1 - GAP 5: claude-mem parity)
    public string? Title { get; set; } // Short title/summary (auto-generated from first line if null)
    public List<string>? Facts { get; set; } // Structured factual points
    public List<string>? Concepts { get; set; } // Categorization tags (distinct from entities)
    public List<string>? FilesRead { get; set; } // Files read during this observation
    public List<string>? FilesModified { get; set; } // Files modified during this observation

    // Search result scores (populated by search queries, not stored in DB)
    public float Similarity { get; set; } // Cosine similarity from semantic search (0.0 to 1.0)
    public float Rank { get; set; } // Full-text search rank score

    // Navigation properties
    public List<Entity> Entities { get; set; } = [];
    public ConversationSession? ConversationSession { get; set; }
}
