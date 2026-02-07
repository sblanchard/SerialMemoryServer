namespace SerialMemory.Core.Models;

/// <summary>
/// Represents a workspace within a tenant for scoping memories and sessions.
/// </summary>
public sealed record Workspace
{
    public Guid Id { get; init; }
    public required string WorkspaceId { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
