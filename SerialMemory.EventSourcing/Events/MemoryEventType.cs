namespace SerialMemory.EventSourcing.Events;

/// <summary>
/// All allowed memory mutation event types.
/// Events are append-only and immutable.
/// </summary>
public enum MemoryEventType
{
    MemoryCreated,
    MemoryUpdated,
    MemoryMerged,
    MemoryInvalidated,
    MemoryDecayed,
    MemoryReinforced,
    MemoryLayerTransitioned,
    MemoryArchived,
    MemoryRecalled,
    MemoryIgnored,
    MemoryContradicted,
    MemoryExpired,
    MemorySplit
}
