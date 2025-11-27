namespace SerialMemory.Core.Models;

/// <summary>
/// Shadow memory branch type - defines isolation level and purpose.
/// </summary>
public enum ShadowBranchType
{
    /// <summary>Speculative reasoning - may or may not be correct.</summary>
    Speculative,

    /// <summary>Sandbox for testing without risk to main memory.</summary>
    Sandbox,

    /// <summary>Branching path for alternative reasoning.</summary>
    Branch,

    /// <summary>Draft memory awaiting validation.</summary>
    Draft
}

/// <summary>
/// Shadow memory branch status.
/// </summary>
public enum ShadowBranchStatus
{
    /// <summary>Branch is active and accepting new memories.</summary>
    Active,

    /// <summary>Branch is frozen - no new memories allowed.</summary>
    Frozen,

    /// <summary>Branch memories promoted to main memory.</summary>
    Promoted,

    /// <summary>Branch discarded - memories deleted.</summary>
    Discarded,

    /// <summary>Branch merged with another branch.</summary>
    Merged
}

/// <summary>
/// A shadow memory branch - isolated container for speculative/sandbox memories.
/// Never pollutes main memory until explicitly promoted.
/// </summary>
public sealed class ShadowBranch
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string TenantId { get; init; } = "self";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ShadowBranchType BranchType { get; init; }
    public ShadowBranchStatus Status { get; set; } = ShadowBranchStatus.Active;

    /// <summary>Parent branch ID for nested branches.</summary>
    public Guid? ParentBranchId { get; init; }

    /// <summary>Source memory ID that spawned this branch.</summary>
    public Guid? SourceMemoryId { get; init; }

    /// <summary>Confidence in this branch (0.0 to 1.0).</summary>
    public float Confidence { get; set; } = 0.5f;

    /// <summary>Hypothesis being tested in this branch.</summary>
    public string? Hypothesis { get; init; }

    /// <summary>Reasoning context for speculative branches.</summary>
    public string? ReasoningContext { get; init; }

    /// <summary>Time-to-live in hours. Null = no expiration.</summary>
    public int? TtlHours { get; init; }

    /// <summary>Auto-discard if confidence drops below this.</summary>
    public float? AutoDiscardThreshold { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FrozenAt { get; set; }
    public DateTimeOffset? PromotedAt { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; init; }

    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// A memory within a shadow branch - isolated from main memory.
/// </summary>
public sealed class ShadowMemory
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid BranchId { get; init; }
    public string TenantId { get; init; } = "self";
    public required string Content { get; set; }
    public float[]? Embedding { get; set; }

    /// <summary>Confidence in this specific memory.</summary>
    public float Confidence { get; set; } = 0.5f;

    /// <summary>Source in main memory this shadows (if any).</summary>
    public Guid? ShadowsMemoryId { get; init; }

    /// <summary>Reasoning step that produced this memory.</summary>
    public int? ReasoningStep { get; init; }

    /// <summary>Supporting evidence IDs.</summary>
    public List<Guid>? SupportingMemoryIds { get; init; }

    /// <summary>Contradicting evidence IDs.</summary>
    public List<Guid>? ContradictingMemoryIds { get; init; }

    public string? Source { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object>? Metadata { get; init; }

    // Navigation
    public ShadowBranch? Branch { get; set; }
}

/// <summary>
/// Entity within a shadow branch.
/// </summary>
public sealed class ShadowEntity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid BranchId { get; init; }
    public string TenantId { get; init; } = "self";
    public required string Name { get; init; }
    public required string EntityType { get; init; }
    public string? CanonicalName { get; init; }

    /// <summary>Entity in main memory this shadows (if any).</summary>
    public Guid? ShadowsEntityId { get; init; }

    public float Confidence { get; set; } = 0.5f;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Relationship within a shadow branch.
/// </summary>
public sealed class ShadowRelationship
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid BranchId { get; init; }
    public string TenantId { get; init; } = "self";
    public Guid SourceEntityId { get; init; }
    public Guid TargetEntityId { get; init; }
    public required string RelationshipType { get; init; }
    public float Confidence { get; set; } = 0.5f;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result of promoting a shadow branch to main memory.
/// </summary>
public sealed class PromotionResult
{
    public Guid BranchId { get; init; }
    public bool Success { get; init; }
    public int MemoriesPromoted { get; init; }
    public int EntitiesPromoted { get; init; }
    public int RelationshipsPromoted { get; init; }
    public List<Guid> PromotedMemoryIds { get; init; } = [];
    public List<Guid> PromotedEntityIds { get; init; } = [];
    public List<Guid> PromotedRelationshipIds { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public DateTimeOffset PromotedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result of discarding a shadow branch.
/// </summary>
public sealed class DiscardResult
{
    public Guid BranchId { get; init; }
    public bool Success { get; init; }
    public int MemoriesDiscarded { get; init; }
    public int EntitiesDiscarded { get; init; }
    public int RelationshipsDiscarded { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset DiscardedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Shadow branch statistics.
/// </summary>
public sealed class ShadowBranchStats
{
    public Guid BranchId { get; init; }
    public int MemoryCount { get; init; }
    public int EntityCount { get; init; }
    public int RelationshipCount { get; init; }
    public float AverageConfidence { get; init; }
    public int ChildBranchCount { get; init; }
    public DateTimeOffset? OldestMemory { get; init; }
    public DateTimeOffset? NewestMemory { get; init; }
}
