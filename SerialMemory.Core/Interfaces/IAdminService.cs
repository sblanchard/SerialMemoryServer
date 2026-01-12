namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for admin/operator observability endpoints.
/// </summary>
public interface IAdminService
{
    /// <summary>
    /// Lists tenants with optional status filter.
    /// </summary>
    Task<TenantListResult> ListTenantsAsync(
        TenantStatusFilter? status = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed health information for a specific tenant.
    /// </summary>
    Task<TenantHealthResult?> GetTenantHealthAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets system-wide health information.
    /// </summary>
    Task<SystemHealthResult> GetSystemHealthAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for tenants by name, slug, or email.
    /// </summary>
    Task<IReadOnlyList<TenantSearchResult>> SearchTenantsAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of tenant search.
/// </summary>
public sealed record TenantSearchResult
{
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? Email { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Tenant status filter for listing.
/// </summary>
public enum TenantStatusFilter
{
    Active,
    PendingDeletion,
    Blocked,
    All
}

/// <summary>
/// Result of listing tenants.
/// </summary>
public sealed record TenantListResult
{
    public required IReadOnlyList<TenantSummary> Tenants { get; init; }
    public required int TotalCount { get; init; }
    public required int Limit { get; init; }
    public required int Offset { get; init; }
}

/// <summary>
/// Summary of a tenant for admin listing.
/// </summary>
public sealed record TenantSummary
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required string Plan { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
    public required int UserCount { get; init; }
    public required int MemoryCount { get; init; }
    public decimal CreditsUsedThisCycle { get; init; }
}

/// <summary>
/// Detailed health information for a tenant.
/// </summary>
public sealed record TenantHealthResult
{
    public required Guid TenantId { get; init; }
    public required string TenantName { get; init; }
    public required string Status { get; init; }

    // Isolation check
    public required IsolationCheckResult IsolationCheck { get; init; }

    // Recent error rate
    public required ErrorRateResult RecentErrorRate { get; init; }

    // Quota usage
    public required QuotaUsageResult QuotaUsage { get; init; }

    // Abuse flags
    public required IReadOnlyList<AbuseFlag> AbuseFlags { get; init; }

    // Last self-audit
    public DateTimeOffset? LastSelfAuditAt { get; init; }
}

public sealed record IsolationCheckResult
{
    public required string Status { get; init; } // "healthy", "warning", "error"
    public string? Message { get; init; }
    public DateTimeOffset LastCheckedAt { get; init; }
}

public sealed record ErrorRateResult
{
    public required int TotalRequests { get; init; }
    public required int ErrorCount { get; init; }
    public required decimal ErrorRate { get; init; }
    public required string Period { get; init; } // e.g., "last_24h"
}

public sealed record QuotaUsageResult
{
    public required decimal CreditsUsed { get; init; }
    public required decimal CreditsLimit { get; init; }
    public required decimal PercentUsed { get; init; }
    public required int MemoriesCount { get; init; }
    public int? MemoriesLimit { get; init; }
    public required int EntitiesCount { get; init; }
    public int? EntitiesLimit { get; init; }
}

public sealed record AbuseFlag
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset FlaggedAt { get; init; }
    public required string Severity { get; init; } // "info", "warning", "critical"
}

/// <summary>
/// System-wide health information.
/// </summary>
public sealed record SystemHealthResult
{
    public required string Status { get; init; } // "healthy", "degraded", "unhealthy"
    public required DateTimeOffset Timestamp { get; init; }

    // Database health
    public required ComponentHealth Database { get; init; }

    // Queue health (if applicable)
    public ComponentHealth? Queue { get; init; }

    // Worker health
    public ComponentHealth? Worker { get; init; }

    // Rate limiter pressure
    public required RateLimiterHealth RateLimiter { get; init; }

    // Summary stats
    public required SystemStats Stats { get; init; }
}

public sealed record ComponentHealth
{
    public required string Status { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}

public sealed record RateLimiterHealth
{
    public required int RecentRejections { get; init; }
    public required string Period { get; init; }
    public required decimal PressurePercent { get; init; }
}

public sealed record SystemStats
{
    public required int TotalTenants { get; init; }
    public required int ActiveTenants { get; init; }
    public required long TotalMemories { get; init; }
    public required long TotalEntities { get; init; }
    public required decimal TotalCreditsUsedToday { get; init; }
}
