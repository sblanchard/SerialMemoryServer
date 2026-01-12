namespace SerialMemory.Core.Models;

/// <summary>
/// Status of a reasoning execution
/// </summary>
public enum ReasoningExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Input type for reasoning execution
/// </summary>
public enum ReasoningInputType
{
    Memory,
    Query,
    Project,
    Graph
}

/// <summary>
/// Reasoning execution entity - persisted to database
/// </summary>
public sealed class ReasoningExecution
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid TenantId { get; init; }
    public required string UserId { get; init; }

    // Input specification
    public required ReasoningInputType InputType { get; init; }
    public string? InputReference { get; init; }
    public string? InputHash { get; set; }

    // Execution state
    public ReasoningExecutionStatus Status { get; set; } = ReasoningExecutionStatus.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Results summary
    public int ModelsUsed { get; set; }
    public int SuccessfulModels { get; set; }
    public int TotalDurationMs { get; set; }
    public float OverallConfidence { get; set; }
    public int InsightCount { get; set; }
    public int DisagreementCount { get; set; }

    // Error handling
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object>? ErrorDetails { get; set; }

    // Audit
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation (populated when fetching full details)
    public List<ReasoningTrace> Traces { get; init; } = [];
    public List<ReasoningInsight> Insights { get; init; } = [];
    public List<ReasoningDisagreement> Disagreements { get; init; } = [];
}

/// <summary>
/// Individual model execution step (DAG node) - persisted to database
/// </summary>
public sealed class ReasoningTrace
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid ExecutionId { get; init; }
    public required Guid TenantId { get; init; }

    // DAG structure
    public required string StepId { get; init; }
    public List<string> ParentStepIds { get; init; } = [];
    public int StepOrder { get; init; }

    // Model information
    public required ReasoningRole Role { get; init; }
    public required string ModelName { get; init; }
    public string? ModelVersion { get; init; }

    // Execution details
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int DurationMs { get; set; }
    public bool Success { get; set; }

    // Input/Output
    public Dictionary<string, object>? InputContext { get; set; }
    public string? OutputRaw { get; set; }
    public Dictionary<string, object>? OutputParsed { get; set; }

    // Error handling
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }

    // Audit
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Merged insight from multiple models - persisted to database
/// </summary>
public sealed class ReasoningInsight
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid ExecutionId { get; init; }
    public required Guid TenantId { get; init; }

    // Insight content
    public required string InsightType { get; init; }
    public required string Content { get; init; }
    public float Confidence { get; init; }

    // Attribution
    public List<string> SourceModels { get; init; } = [];
    public int AgreementCount { get; init; } = 1;

    // Related entities
    public List<Guid> RelatedEntityIds { get; init; } = [];
    public List<Guid> RelatedMemoryIds { get; init; } = [];

    // Metadata
    public Dictionary<string, object>? Metadata { get; init; }

    // Audit
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Model disagreement - persisted to database
/// </summary>
public sealed class ReasoningDisagreement
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid ExecutionId { get; init; }
    public required Guid TenantId { get; init; }

    // Disagreement details
    public required string Topic { get; init; }
    public required string ModelA { get; init; }
    public required string PositionA { get; init; }
    public required string ModelB { get; init; }
    public required string PositionB { get; init; }
    public string? Severity { get; init; }

    // Resolution
    public bool Resolved { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    // Audit
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Request to start a reasoning execution
/// </summary>
public sealed record ReasoningRunRequest
{
    public required Guid TenantId { get; init; }
    public required string UserId { get; init; }
    public required ReasoningInputType InputType { get; init; }
    public string? InputReference { get; init; }
    public int MaxDurationMs { get; init; } = 30000;
}

/// <summary>
/// Lightweight summary for listing executions
/// </summary>
public sealed record ReasoningExecutionSummary
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string UserId { get; init; }
    public required ReasoningInputType InputType { get; init; }
    public string? InputReference { get; init; }
    public required ReasoningExecutionStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int ModelsUsed { get; init; }
    public int SuccessfulModels { get; init; }
    public int TotalDurationMs { get; init; }
    public float OverallConfidence { get; init; }
    public int InsightCount { get; init; }
    public int DisagreementCount { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
