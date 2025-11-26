using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SerialMemory.EventSourcing.Events;

/// <summary>
/// Event: Contradiction detected between memories.
/// </summary>
public sealed record ContradictionDetectedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.ContradictionDetected;

    public required Guid MemoryAId { get; init; }
    public required Guid MemoryBId { get; init; }
    public required string DetectionMethod { get; init; }
    public required float ContradictionScore { get; init; }
    public string? Details { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { MemoryAId, MemoryBId, DetectionMethod, ContradictionScore });
}

/// <summary>
/// Event: Hallucination flagged in memory content.
/// </summary>
public sealed record HallucinationFlaggedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.HallucinationFlagged;

    public required string DetectionMethod { get; init; }
    public required float ConfidenceScore { get; init; }
    public string? FlaggedContent { get; init; }
    public string? Reason { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { DetectionMethod, ConfidenceScore, FlaggedContent, Reason });
}

/// <summary>
/// Event: Integrity check failed for memory content.
/// </summary>
public sealed record IntegrityCheckFailedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.IntegrityCheckFailed;

    public required string CheckType { get; init; }
    public required string ExpectedHash { get; init; }
    public required string ActualHash { get; init; }
    public string? Details { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { CheckType, ExpectedHash, ActualHash });
}

/// <summary>
/// Event: Export completed successfully.
/// </summary>
public sealed record ExportCompletedEvent : MemoryEventBase
{
    public override MemoryEventType EventType => MemoryEventType.ExportCompleted;

    public required string ExportType { get; init; }
    public required int MemoriesExported { get; init; }
    public int EntitiesExported { get; init; }
    public int RelationshipsExported { get; init; }
    public string? OutputPath { get; init; }
    public bool Encrypted { get; init; }
    public bool Compressed { get; init; }

    protected override string GetHashableContent() =>
        JsonSerializer.Serialize(new { ExportType, MemoriesExported, EntitiesExported, OutputPath });
}
