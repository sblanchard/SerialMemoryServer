namespace SerialMemory.Core.Models;

/// <summary>
/// Integrity event types for memory monitoring
/// </summary>
public enum IntegrityEventType
{
    // Hash validation events
    HashValidationPass,
    HashValidationFail,
    HashMismatch,

    // Contradiction detection events
    ContradictionDetected,
    ContradictionResolved,
    ContradictionIgnored,

    // Loop detection events
    CausalLoopDetected,
    CausalLoopBroken,

    // Integrity events
    IntegrityCheckStarted,
    IntegrityCheckCompleted,
    IntegrityViolation,

    // Anomaly events
    AnomalyDetected,
    HallucinationSuspected,

    // Memory lifecycle events
    MemoryReadValidated,
    MemoryWriteValidated,
    MemoryCorruptionDetected
}

/// <summary>
/// Integrity event severity levels
/// </summary>
public enum IntegritySeverity
{
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// An integrity event logged during memory operations
/// </summary>
public class IntegrityEvent
{
    public Guid EventId { get; set; } = Guid.CreateVersion7();
    public IntegrityEventType EventType { get; set; }
    public IntegritySeverity Severity { get; set; } = IntegritySeverity.Info;

    // What was being checked
    public Guid? MemoryId { get; set; }
    public Guid? EntityId { get; set; }

    // Event details
    public required string Message { get; set; }
    public Dictionary<string, object>? Details { get; set; }

    // Hash validation specific
    public string? ExpectedHash { get; set; }
    public string? ActualHash { get; set; }

    // Contradiction specific
    public List<Guid>? ContradictingMemoryIds { get; set; }
    public float? ContradictionScore { get; set; }

    // Loop detection specific
    public List<Guid>? LoopPath { get; set; }
    public int? LoopDepth { get; set; }

    // Timestamps and audit
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string DetectedBy { get; set; } = "system";
    public Guid? SessionId { get; set; }
    public Guid? ScanBatchId { get; set; }
}

/// <summary>
/// A security scan run tracking integrity checks
/// </summary>
public class SecurityScan
{
    public Guid ScanId { get; set; } = Guid.CreateVersion7();
    public required string ScanType { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    // Scope
    public int MemoriesScanned { get; set; }
    public int EntitiesScanned { get; set; }
    public int RelationshipsScanned { get; set; }

    // Results
    public int EventsGenerated { get; set; }
    public int IssuesFound { get; set; }
    public int CriticalIssues { get; set; }

    // Status
    public string Status { get; set; } = "running";
    public string? ErrorMessage { get; set; }

    // Metadata
    public string TriggeredBy { get; set; } = "scheduled";
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Aggregated security metrics
/// </summary>
public class SecurityMetrics
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    // Overall counts
    public long TotalEvents { get; set; }
    public long TotalScans { get; set; }

    // Event counts by type
    public int HashValidationPasses { get; set; }
    public int HashValidationFailures { get; set; }
    public int ContradictionsDetected { get; set; }
    public int LoopsDetected { get; set; }
    public int IntegrityViolations { get; set; }
    public int AnomaliesDetected { get; set; }

    // Severity breakdown
    public int InfoEvents { get; set; }
    public int WarningEvents { get; set; }
    public int ErrorEvents { get; set; }
    public int CriticalEvents { get; set; }

    // Time-based metrics
    public int EventsLast24Hours { get; set; }
    public int EventsLastHour { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }

    // Pass rates
    public double HashValidationPassRate { get; set; }
    public double OverallPassRate { get; set; }

    // Memory statistics
    public long MemoriesScanned { get; set; }
    public long MemoriesWithIssues { get; set; }
}

/// <summary>
/// Result of a hash validation check
/// </summary>
public class HashValidationResult
{
    public Guid MemoryId { get; set; }
    public bool IsValid { get; set; }
    public string? ExpectedHash { get; set; }
    public string? ActualHash { get; set; }
    public string? Content { get; set; }
}

/// <summary>
/// Result of contradiction detection
/// </summary>
public class ContradictionResult
{
    public Guid MemoryAId { get; set; }
    public Guid MemoryBId { get; set; }
    public float Score { get; set; }
    public string DetectionMethod { get; set; } = "semantic";
    public string? ReasonSummary { get; set; }
}

/// <summary>
/// Result of causal loop detection
/// </summary>
public class LoopDetectionResult
{
    public bool HasLoop { get; set; }
    public List<Guid> LoopPath { get; set; } = [];
    public int Depth { get; set; }
    public Guid? StartingMemoryId { get; set; }
}
