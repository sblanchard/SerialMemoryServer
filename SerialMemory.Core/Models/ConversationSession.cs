namespace SerialMemory.Core.Models;

/// <summary>
/// Tracks conversation sessions for context continuity
/// </summary>
public class ConversationSession
{
    public Guid Id { get; set; }
    public string? SessionName { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? ClientType { get; set; } // claude-desktop, cursor, windsurf, etc.
    public Dictionary<string, object>? Metadata { get; set; }

    // Navigation properties
    public List<Memory> Memories { get; set; } = [];
}
