using SerialMemory.EventSourcing.Store;

namespace SerialMemory.EventSourcing.Projections;

/// <summary>
/// Interface for event projections that build read models.
/// Projections are rebuilt from events and can be dropped/recreated.
/// </summary>
public interface IProjection
{
    /// <summary>Projection name for checkpoint tracking</summary>
    string ProjectionName { get; }

    /// <summary>Apply a stored event to update the projection</summary>
    Task ApplyAsync(StoredEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Get last processed sequence for this projection</summary>
    Task<long> GetCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Update checkpoint after processing</summary>
    Task SaveCheckpointAsync(long globalSequence, CancellationToken cancellationToken = default);
}

/// <summary>
/// Projection host that runs projections against the event store.
/// </summary>
public interface IProjectionHost
{
    /// <summary>Start processing events for all projections</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop processing</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Rebuild a projection from scratch</summary>
    Task RebuildProjectionAsync(string projectionName, CancellationToken cancellationToken = default);
}
