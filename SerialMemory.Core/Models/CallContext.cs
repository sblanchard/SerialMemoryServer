namespace SerialMemory.Core.Models;

/// <summary>
/// Per-call context envelope for overriding workspace, session, and providing
/// ephemeral context (memory, goal, constraints) to tool calls.
/// </summary>
public sealed record CallContext
{
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? Memory { get; init; }
    public string? Goal { get; init; }
    public string? Constraints { get; init; }
}
