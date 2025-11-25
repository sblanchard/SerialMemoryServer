namespace SerialMemory.Contracts.Events;

/// <summary>
/// Event published when a context value is deleted.
/// </summary>
public record ContextDeleted
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
    /// Context key that was deleted.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Source system that triggered the deletion (for audit trail).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Reason for deletion (optional).
    /// </summary>
    public string? Reason { get; init; }
}
