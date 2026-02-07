namespace SerialMemory.Core.Models;

/// <summary>
/// A named state snapshot for a workspace, used for checkpointing and restoring context.
/// </summary>
public sealed record WorkspaceSnapshot
{
    public Guid Id { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SnapshotName { get; init; }
    public required WorkspaceStateData StateData { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// The serializable state data captured in a workspace snapshot.
/// </summary>
public sealed record WorkspaceStateData
{
    public string? SessionId { get; init; }
    public string? Goal { get; init; }
    public string? Constraints { get; init; }
    public string? Memory { get; init; }
    public List<Guid> RecentMemoryIds { get; init; } = [];
    public List<string> ActiveEntityNames { get; init; } = [];
    public Dictionary<string, object>? CustomMetadata { get; init; }
}
