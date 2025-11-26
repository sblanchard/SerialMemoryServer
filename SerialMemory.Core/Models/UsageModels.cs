namespace SerialMemory.Core.Models;

/// <summary>
/// Usage event types for metering API calls.
/// </summary>
public enum UsageEventType
{
    MemoryIngest,
    MemorySearch,
    MemoryMultiHopSearch,
    MemoryUpdate,
    MemoryDelete,
    MemoryMerge,
    MemorySplit,
    MemoryDecay,
    MemoryReinforce,
    MemoryExpire,
    CrawlRelationships,
    ExportWorkspace,
    ExportMemories,
    ExportGraph,
    ReembedMemories
}

/// <summary>
/// Credit costs for each operation type.
/// </summary>
public static class UsageCreditCosts
{
    private static readonly Dictionary<UsageEventType, decimal> Costs = new()
    {
        [UsageEventType.MemoryIngest] = 1.0m,
        [UsageEventType.MemorySearch] = 0.25m,
        [UsageEventType.MemoryMultiHopSearch] = 2.0m,
        [UsageEventType.MemoryUpdate] = 0.5m,
        [UsageEventType.MemoryDelete] = 0.2m,
        [UsageEventType.MemoryMerge] = 1.5m,
        [UsageEventType.MemorySplit] = 1.5m,
        [UsageEventType.MemoryDecay] = 0.1m,
        [UsageEventType.MemoryReinforce] = 0.2m,
        [UsageEventType.MemoryExpire] = 0.2m,
        [UsageEventType.CrawlRelationships] = 1.0m,
        [UsageEventType.ExportWorkspace] = 10.0m,
        [UsageEventType.ExportMemories] = 5.0m,
        [UsageEventType.ExportGraph] = 5.0m,
        [UsageEventType.ReembedMemories] = 5.0m
    };

    public static decimal GetCost(UsageEventType eventType) =>
        Costs.TryGetValue(eventType, out var cost) ? cost : 1.0m;

    public static string ToSnakeCase(UsageEventType eventType) => eventType switch
    {
        UsageEventType.MemoryIngest => "memory_ingest",
        UsageEventType.MemorySearch => "memory_search",
        UsageEventType.MemoryMultiHopSearch => "memory_multi_hop_search",
        UsageEventType.MemoryUpdate => "memory_update",
        UsageEventType.MemoryDelete => "memory_delete",
        UsageEventType.MemoryMerge => "memory_merge",
        UsageEventType.MemorySplit => "memory_split",
        UsageEventType.MemoryDecay => "memory_decay",
        UsageEventType.MemoryReinforce => "memory_reinforce",
        UsageEventType.MemoryExpire => "memory_expire",
        UsageEventType.CrawlRelationships => "crawl_relationships",
        UsageEventType.ExportWorkspace => "export_workspace",
        UsageEventType.ExportMemories => "export_memories",
        UsageEventType.ExportGraph => "export_graph",
        UsageEventType.ReembedMemories => "reembed_memories",
        _ => eventType.ToString().ToLowerInvariant()
    };
}

/// <summary>
/// Individual usage event record.
/// </summary>
public sealed class UsageEvent
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "self";
    public string WorkspaceId { get; set; } = "default";
    public Guid? BillingCycleId { get; set; }
    public UsageEventType EventType { get; set; }
    public decimal CreditsConsumed { get; set; }
    public DateTimeOffset EventTimestamp { get; set; } = DateTimeOffset.UtcNow;
    public Guid? MemoryId { get; set; }
    public string? UserId { get; set; }
    public Guid? SessionId { get; set; }
    public int? LatencyMs { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Daily rollup of usage statistics.
/// </summary>
public sealed class UsageDailyRollup
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "self";
    public string WorkspaceId { get; set; } = "default";
    public DateOnly RollupDate { get; set; }
    public UsageEventType EventType { get; set; }
    public int EventCount { get; set; }
    public decimal TotalCredits { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public decimal? AvgLatencyMs { get; set; }
    public int? P95LatencyMs { get; set; }
    public int? P99LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Billing cycle for tracking credit usage periods.
/// </summary>
public sealed class BillingCycle
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "self";
    public string WorkspaceId { get; set; } = "default";
    public Guid? PlanId { get; set; }
    public DateTimeOffset CycleStart { get; set; }
    public DateTimeOffset CycleEnd { get; set; }
    public decimal CreditsAllocated { get; set; }
    public decimal CreditsUsed { get; set; }
    public decimal CreditsRemaining => CreditsAllocated - CreditsUsed;
    public bool IsCurrent { get; set; } = true;
    public DateTimeOffset? ClosedAt { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Tenant plan defining credit limits and quotas.
/// </summary>
public sealed class TenantPlan
{
    public Guid Id { get; set; }
    public string PlanName { get; set; } = "self";
    public string DisplayName { get; set; } = "Self-Hosted";
    public decimal CreditsPerCycle { get; set; } = 999999999m;
    public int CycleDays { get; set; } = 30;
    public int? MaxMemories { get; set; }
    public int? MaxEntities { get; set; }
    public bool IsActive { get; set; } = true;
    public Dictionary<string, object>? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
