namespace SerialMemory.EventSourcing.Events;

/// <summary>
/// Cognitive processing stages for instrumentation.
/// Each stage must be queryable and logged.
/// </summary>
public enum CognitiveStage
{
    /// <summary>Planning and intent determination</summary>
    Plan,

    /// <summary>Memory retrieval phase</summary>
    Retrieve,

    /// <summary>Reasoning over retrieved memories</summary>
    Reason,

    /// <summary>Self-reflection on reasoning</summary>
    Reflect,

    /// <summary>Summarization of findings</summary>
    Summarise,

    /// <summary>Consolidation into knowledge</summary>
    Consolidate
}

/// <summary>
/// Log entry for a cognitive stage execution.
/// </summary>
public sealed record CognitiveStageLog
{
    public Guid LogId { get; init; } = Guid.CreateVersion7();
    public Guid? SessionId { get; init; }
    public required CognitiveStage Stage { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int? DurationMs => CompletedAt.HasValue
        ? (int)(CompletedAt.Value - StartedAt).TotalMilliseconds
        : null;
    public string? InputSummary { get; init; }
    public string? OutputSummary { get; set; }
    public Guid[] MemoryIdsInvolved { get; init; } = [];
    public Dictionary<string, object> Metadata { get; init; } = [];
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }

    public void Complete(string? outputSummary = null)
    {
        CompletedAt = DateTimeOffset.UtcNow;
        OutputSummary = outputSummary;
    }

    public void Fail(string errorMessage)
    {
        CompletedAt = DateTimeOffset.UtcNow;
        Success = false;
        ErrorMessage = errorMessage;
    }
}
