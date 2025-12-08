using SerialMemory.Core.Interfaces;

namespace SerialMemory.EventSourcing.CQRS;

/// <summary>
/// Base interface for all commands.
/// Commands represent intent to mutate state.
/// </summary>
public interface ICommand
{
    string? ActorId { get; }
    Guid? CorrelationId { get; }
}

/// <summary>
/// Result of command execution.
/// </summary>
public sealed record CommandResult
{
    public bool Success { get; init; }
    public Guid? AggregateId { get; init; }
    public long? Version { get; init; }
    public string? Error { get; init; }
    public long[]? GlobalSequences { get; init; }

    public static CommandResult Ok(Guid aggregateId, long version, long[] sequences) =>
        new() { Success = true, AggregateId = aggregateId, Version = version, GlobalSequences = sequences };

    public static CommandResult Fail(string error) =>
        new() { Success = false, Error = error };
}

// ============================================================================
// WRITE COMMANDS
// ============================================================================

/// <summary>
/// Command to create a new memory.
/// </summary>
public sealed record CreateMemoryCommand : ICommand
{
    public required string Content { get; init; }
    public MemoryLayer Layer { get; init; } = MemoryLayer.L0_RAW;
    public float ConfidenceScore { get; init; } = 1.0f;
    public int HalfLifeDays { get; init; } = 30;
    public Guid[] CausalParents { get; init; } = [];
    public string? Source { get; init; }
    public Guid? SessionId { get; init; }
    public string UserId { get; init; } = "default_user";
    public string[] Tags { get; init; } = [];
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to update memory content.
/// </summary>
public sealed record UpdateMemoryCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public required string NewContent { get; init; }
    public string? Reason { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to reinforce a memory.
/// </summary>
public sealed record ReinforceMemoryCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public float NewConfidence { get; init; } = 1.0f;
    public required string Source { get; init; }
    public Guid[] ValidatedByIds { get; init; } = [];
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to invalidate a memory.
/// </summary>
public sealed record InvalidateMemoryCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public required string Reason { get; init; }
    public Guid? SupersededById { get; init; }
    public Guid[] ContradictedByIds { get; init; } = [];
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to merge memories.
/// </summary>
public sealed record MergeMemoriesCommand : ICommand
{
    public required Guid[] SourceMemoryIds { get; init; }
    public required string MergedContent { get; init; }
    public string? MergeStrategy { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to transition memory layer.
/// </summary>
public sealed record TransitionLayerCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public required MemoryLayer NewLayer { get; init; }
    public string? Reason { get; init; }
    public Guid? TriggeredByMemoryId { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to apply decay to a single memory.
/// </summary>
public sealed record ApplyDecayCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to archive a memory (cold storage).
/// </summary>
public sealed record ArchiveMemoryCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public required string Reason { get; init; }
    public int AccessCount { get; init; }
    public DateTimeOffset LastAccessedAt { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to record a memory recall event.
/// </summary>
public sealed record RecallMemoryCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public string? Query { get; init; }
    public float SimilarityScore { get; init; }
    public string? Context { get; init; }
    public Guid? SessionId { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to record that a memory was ignored.
/// </summary>
public sealed record IgnoreMemoryCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public string? Query { get; init; }
    public required string Reason { get; init; }
    public Guid? SessionId { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to mark a contradiction between memories.
/// </summary>
public sealed record MarkContradictionCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public required Guid[] ContradictingMemoryIds { get; init; }
    public required string ContradictionType { get; init; }
    public string? DetectionMethod { get; init; }
    public float ContradictionConfidence { get; init; } = 1.0f;
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to expire a memory.
/// </summary>
public sealed record ExpireMemoryCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public required string ExpirationPolicy { get; init; }
    public int OriginalTtlDays { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}

/// <summary>
/// Command to split a memory into children.
/// </summary>
public sealed record SplitMemoryCommand : ICommand
{
    public required Guid MemoryId { get; init; }
    public required Guid[] ChildMemoryIds { get; init; }
    public string? SplitStrategy { get; init; }
    public string? Reason { get; init; }
    public string? ActorId { get; init; }
    public Guid? CorrelationId { get; init; }
}
