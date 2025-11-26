namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Advanced rate limiting service with sliding windows and concurrency caps.
/// Supports per-tenant, per-endpoint, and global rate limiting.
/// </summary>
public interface IRateLimitingService
{
    /// <summary>
    /// Checks if a request can proceed under rate limits.
    /// </summary>
    Task<RateLimitResult> CheckRateLimitAsync(
        RateLimitRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Acquires a concurrency slot for a tenant/operation.
    /// Returns null if max concurrency reached.
    /// </summary>
    Task<ConcurrencySlot?> TryAcquireConcurrencySlotAsync(
        string tenantId,
        string operation,
        CancellationToken ct = default);

    /// <summary>
    /// Releases a concurrency slot.
    /// </summary>
    Task ReleaseConcurrencySlotAsync(
        ConcurrencySlot slot,
        CancellationToken ct = default);

    /// <summary>
    /// Gets current rate limit status for a tenant.
    /// </summary>
    Task<RateLimitStatus> GetStatusAsync(
        string tenantId,
        CancellationToken ct = default);
}

/// <summary>
/// Rate limit check request.
/// </summary>
public sealed record RateLimitRequest
{
    public required string TenantId { get; init; }
    public required string Operation { get; init; }
    public string? UserId { get; init; }
    public string? IpAddress { get; init; }
    public int Cost { get; init; } = 1;
}

/// <summary>
/// Result of a rate limit check.
/// </summary>
public sealed record RateLimitResult
{
    public required bool IsAllowed { get; init; }
    public required int Remaining { get; init; }
    public required int Limit { get; init; }
    public required TimeSpan WindowSize { get; init; }
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Which limit was exceeded (if any).
    /// </summary>
    public RateLimitType? ExceededLimit { get; init; }

    /// <summary>
    /// Headers to include in response.
    /// </summary>
    public Dictionary<string, string> GetHeaders() => new()
    {
        ["X-RateLimit-Limit"] = Limit.ToString(),
        ["X-RateLimit-Remaining"] = Math.Max(0, Remaining).ToString(),
        ["X-RateLimit-Reset"] = DateTimeOffset.UtcNow.Add(WindowSize).ToUnixTimeSeconds().ToString()
    };
}

/// <summary>
/// Types of rate limits.
/// </summary>
public enum RateLimitType
{
    /// <summary>
    /// Per-second sliding window.
    /// </summary>
    PerSecond,

    /// <summary>
    /// Per-minute sliding window.
    /// </summary>
    PerMinute,

    /// <summary>
    /// Per-hour sliding window.
    /// </summary>
    PerHour,

    /// <summary>
    /// Concurrent operation limit.
    /// </summary>
    Concurrency,

    /// <summary>
    /// Daily quota.
    /// </summary>
    DailyQuota,

    /// <summary>
    /// Burst limit (short spike protection).
    /// </summary>
    Burst
}

/// <summary>
/// A held concurrency slot.
/// </summary>
public sealed record ConcurrencySlot
{
    public required Guid SlotId { get; init; }
    public required string TenantId { get; init; }
    public required string Operation { get; init; }
    public required DateTimeOffset AcquiredAt { get; init; }
    public required TimeSpan MaxHoldTime { get; init; }

    /// <summary>
    /// When this slot will auto-expire if not released.
    /// </summary>
    public DateTimeOffset ExpiresAt => AcquiredAt.Add(MaxHoldTime);
}

/// <summary>
/// Current rate limit status for a tenant.
/// </summary>
public sealed record RateLimitStatus
{
    public required string TenantId { get; init; }
    public required int RequestsPerSecond { get; init; }
    public required int RequestsPerMinute { get; init; }
    public required int LimitPerSecond { get; init; }
    public required int LimitPerMinute { get; init; }
    public required int CurrentConcurrency { get; init; }
    public required int MaxConcurrency { get; init; }
    public required decimal DailyQuotaUsed { get; init; }
    public required decimal DailyQuotaLimit { get; init; }
}

/// <summary>
/// Rate limit configuration per plan tier.
/// </summary>
public sealed record RateLimitConfig
{
    public int RequestsPerSecond { get; init; } = 10;
    public int RequestsPerMinute { get; init; } = 60;
    public int BurstLimit { get; init; } = 20;
    public int MaxConcurrency { get; init; } = 5;
    public TimeSpan ConcurrencySlotTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public static RateLimitConfig Free => new()
    {
        RequestsPerSecond = 2,
        RequestsPerMinute = 30,
        BurstLimit = 5,
        MaxConcurrency = 2,
        ConcurrencySlotTimeout = TimeSpan.FromMinutes(2)
    };

    public static RateLimitConfig Pro => new()
    {
        RequestsPerSecond = 10,
        RequestsPerMinute = 120,
        BurstLimit = 30,
        MaxConcurrency = 10,
        ConcurrencySlotTimeout = TimeSpan.FromMinutes(5)
    };

    public static RateLimitConfig Enterprise => new()
    {
        RequestsPerSecond = 50,
        RequestsPerMinute = 600,
        BurstLimit = 100,
        MaxConcurrency = 50,
        ConcurrencySlotTimeout = TimeSpan.FromMinutes(10)
    };

    public static RateLimitConfig SelfHosted => new()
    {
        RequestsPerSecond = 1000,
        RequestsPerMinute = 10000,
        BurstLimit = 2000,
        MaxConcurrency = 100,
        ConcurrencySlotTimeout = TimeSpan.FromMinutes(30)
    };
}
