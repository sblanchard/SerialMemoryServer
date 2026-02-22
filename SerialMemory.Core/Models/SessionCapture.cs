namespace SerialMemory.Core.Models;

/// <summary>
/// A staged capture entry received over HTTP.
/// Stored server-side in session_captures table, drained into memories.
/// </summary>
public class SessionCapture
{
    public Guid Id { get; set; }
    public string? SessionId { get; set; }
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public string? Tool { get; set; }
    public string? File { get; set; }
    public string? Result { get; set; }
    public Dictionary<string, object>? RawJson { get; set; }
    public bool Drained { get; set; }
    public DateTime? DrainedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
