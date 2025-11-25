namespace SerialMemory.Contracts.Events;

/// <summary>
/// Event published when a context value is updated.
/// Follows MassTransit message contract conventions with correlation ID and timestamp.
/// </summary>
public record ContextUpdated
{
    /// <summary>
    /// Unique identifier for this message (used for deduplication and tracking).
    /// </summary>
    public Guid MessageId { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Correlation ID for distributed tracing across services.
    /// </summary>
    public Guid CorrelationId { get; init; }

    /// <summary>
    /// When the event occurred.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Context key that was updated.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// New value (nullable for optional inclusion in event).
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Source system that triggered the update (for audit trail).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Additional metadata about the update operation.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}
