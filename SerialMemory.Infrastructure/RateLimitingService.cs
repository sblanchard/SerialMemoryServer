using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Advanced rate limiting service with sliding windows and concurrency caps.
/// Uses in-memory counters with PostgreSQL for persistence and distributed coordination.
/// </summary>
public sealed class RateLimitingService : IRateLimitingService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<RateLimitingService> _logger;
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _secondCounters = new();
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _minuteCounters = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, ConcurrencySlot>> _concurrencySlots = new();
    private readonly ConcurrentDictionary<string, RateLimitConfig> _tenantConfigs = new();

    public RateLimitingService(string connectionString, ILogger<RateLimitingService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public RateLimitingService(NpgsqlDataSource dataSource, ILogger<RateLimitingService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<RateLimitResult> CheckRateLimitAsync(
        RateLimitRequest request,
        CancellationToken ct = default)
    {
        var config = await GetTenantConfigAsync(request.TenantId, ct);

        // Check per-second limit (burst protection)
        var secondKey = $"{request.TenantId}:{request.Operation}:sec";
        var secondCounter = _secondCounters.GetOrAdd(secondKey, _ => new SlidingWindowCounter(TimeSpan.FromSeconds(1)));
        var secondCount = secondCounter.GetCount();

        if (secondCount + request.Cost > config.BurstLimit)
        {
            _logger.LogWarning(
                "Burst limit exceeded for tenant {TenantId}: {Count}/{Limit} per second",
                request.TenantId, secondCount, config.BurstLimit);

            return new RateLimitResult
            {
                IsAllowed = false,
                Remaining = 0,
                Limit = config.BurstLimit,
                WindowSize = TimeSpan.FromSeconds(1),
                RetryAfter = TimeSpan.FromSeconds(1),
                ExceededLimit = RateLimitType.Burst
            };
        }

        // Check per-minute limit
        var minuteKey = $"{request.TenantId}:{request.Operation}:min";
        var minuteCounter = _minuteCounters.GetOrAdd(minuteKey, _ => new SlidingWindowCounter(TimeSpan.FromMinutes(1)));
        var minuteCount = minuteCounter.GetCount();

        if (minuteCount + request.Cost > config.RequestsPerMinute)
        {
            var retryAfter = minuteCounter.GetTimeUntilSlotAvailable();

            _logger.LogWarning(
                "Rate limit exceeded for tenant {TenantId}: {Count}/{Limit} per minute",
                request.TenantId, minuteCount, config.RequestsPerMinute);

            // Record rate limit hit in database for monitoring
            await RecordRateLimitHitAsync(request.TenantId, request.Operation, ct);

            return new RateLimitResult
            {
                IsAllowed = false,
                Remaining = 0,
                Limit = config.RequestsPerMinute,
                WindowSize = TimeSpan.FromMinutes(1),
                RetryAfter = retryAfter,
                ExceededLimit = RateLimitType.PerMinute
            };
        }

        // Increment counters
        secondCounter.Increment(request.Cost);
        minuteCounter.Increment(request.Cost);

        return new RateLimitResult
        {
            IsAllowed = true,
            Remaining = config.RequestsPerMinute - minuteCount - request.Cost,
            Limit = config.RequestsPerMinute,
            WindowSize = TimeSpan.FromMinutes(1)
        };
    }

    public async Task<ConcurrencySlot?> TryAcquireConcurrencySlotAsync(
        string tenantId,
        string operation,
        CancellationToken ct = default)
    {
        var config = await GetTenantConfigAsync(tenantId, ct);
        var key = $"{tenantId}:{operation}";

        var slots = _concurrencySlots.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, ConcurrencySlot>());

        // Clean up expired slots
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in slots)
        {
            if (kvp.Value.ExpiresAt < now)
            {
                slots.TryRemove(kvp.Key, out _);
            }
        }

        // Check if we can acquire
        if (slots.Count >= config.MaxConcurrency)
        {
            _logger.LogWarning(
                "Concurrency limit reached for tenant {TenantId} operation {Operation}: {Count}/{Max}",
                tenantId, operation, slots.Count, config.MaxConcurrency);
            return null;
        }

        var slot = new ConcurrencySlot
        {
            SlotId = Guid.CreateVersion7(),
            TenantId = tenantId,
            Operation = operation,
            AcquiredAt = now,
            MaxHoldTime = config.ConcurrencySlotTimeout
        };

        if (slots.TryAdd(slot.SlotId, slot))
        {
            _logger.LogDebug(
                "Acquired concurrency slot {SlotId} for tenant {TenantId} ({Count}/{Max})",
                slot.SlotId, tenantId, slots.Count, config.MaxConcurrency);
            return slot;
        }

        return null;
    }

    public Task ReleaseConcurrencySlotAsync(ConcurrencySlot slot, CancellationToken ct = default)
    {
        var key = $"{slot.TenantId}:{slot.Operation}";

        if (_concurrencySlots.TryGetValue(key, out var slots))
        {
            if (slots.TryRemove(slot.SlotId, out _))
            {
                _logger.LogDebug(
                    "Released concurrency slot {SlotId} for tenant {TenantId}",
                    slot.SlotId, slot.TenantId);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<RateLimitStatus> GetStatusAsync(string tenantId, CancellationToken ct = default)
    {
        var config = await GetTenantConfigAsync(tenantId, ct);

        var secondKey = $"{tenantId}:*:sec";
        var minuteKey = $"{tenantId}:*:min";
        var concurrencyKey = $"{tenantId}:*";

        var secondCount = 0;
        var minuteCount = 0;
        var concurrencyCount = 0;

        foreach (var kvp in _secondCounters)
        {
            if (kvp.Key.StartsWith($"{tenantId}:"))
                secondCount += kvp.Value.GetCount();
        }

        foreach (var kvp in _minuteCounters)
        {
            if (kvp.Key.StartsWith($"{tenantId}:"))
                minuteCount += kvp.Value.GetCount();
        }

        foreach (var kvp in _concurrencySlots)
        {
            if (kvp.Key.StartsWith($"{tenantId}:"))
                concurrencyCount += kvp.Value.Count;
        }

        // Get daily quota from database
        var dailyUsage = await GetDailyQuotaUsageAsync(tenantId, ct);

        return new RateLimitStatus
        {
            TenantId = tenantId,
            RequestsPerSecond = secondCount,
            RequestsPerMinute = minuteCount,
            LimitPerSecond = config.BurstLimit,
            LimitPerMinute = config.RequestsPerMinute,
            CurrentConcurrency = concurrencyCount,
            MaxConcurrency = config.MaxConcurrency,
            DailyQuotaUsed = dailyUsage.used,
            DailyQuotaLimit = dailyUsage.limit
        };
    }

    private async Task<RateLimitConfig> GetTenantConfigAsync(string tenantId, CancellationToken ct)
    {
        if (_tenantConfigs.TryGetValue(tenantId, out var cached))
            return cached;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var planName = await conn.QueryFirstOrDefaultAsync<string>(
            """
            SELECT ts.plan FROM tenant_settings ts
                          JOIN tenants t ON ts.tenant_id = t.id
                          WHERE t.id = @TenantId::uuid OR t.slug = @TenantId
            """,
            new { TenantId = tenantId });

        var config = planName?.ToLowerInvariant() switch
        {
            "free" => RateLimitConfig.Free,
            "pro" => RateLimitConfig.Pro,
            "enterprise" => RateLimitConfig.Enterprise,
            "self" => RateLimitConfig.SelfHosted,
            _ => RateLimitConfig.Free
        };

        _tenantConfigs[tenantId] = config;
        return config;
    }

    private async Task RecordRateLimitHitAsync(string tenantId, string operation, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(
                """
                INSERT INTO rate_limit_hits (tenant_id, operation, hit_at)
                                  VALUES (@TenantId, @Operation, NOW())
                """,
                new { TenantId = tenantId, Operation = operation });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record rate limit hit for tenant {TenantId}", tenantId);
        }
    }

    private async Task<(decimal used, decimal limit)> GetDailyQuotaUsageAsync(string tenantId, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            var result = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT
                                    COALESCE(SUM(credits_consumed), 0) AS used,
                                    COALESCE(tp.credits_per_cycle, 1000) AS daily_limit
                                  FROM usage_events ue
                                  JOIN billing_cycles bc ON ue.billing_cycle_id = bc.id
                                  JOIN tenant_plans tp ON bc.plan_id = tp.id
                                  WHERE ue.tenant_id = @TenantId
                                    AND ue.event_timestamp >= CURRENT_DATE
                                    AND ue.event_timestamp < CURRENT_DATE + INTERVAL '1 day'
                                  GROUP BY tp.credits_per_cycle
                """,
                new { TenantId = tenantId });

            return result != null ? ((decimal)result.used, (decimal)result.daily_limit) : (0, 1000);
        }
        catch
        {
            return (0, 1000);
        }
    }

    /// <summary>
    /// Sliding window counter using circular buffer.
    /// </summary>
    private sealed class SlidingWindowCounter
    {
        private readonly TimeSpan _windowSize;
        private readonly ConcurrentQueue<(DateTimeOffset time, int count)> _entries = new();
        private int _totalCount;

        public SlidingWindowCounter(TimeSpan windowSize)
        {
            _windowSize = windowSize;
        }

        public int GetCount()
        {
            CleanOldEntries();
            return _totalCount;
        }

        public void Increment(int amount = 1)
        {
            CleanOldEntries();
            _entries.Enqueue((DateTimeOffset.UtcNow, amount));
            Interlocked.Add(ref _totalCount, amount);
        }

        public TimeSpan GetTimeUntilSlotAvailable()
        {
            CleanOldEntries();

            if (_entries.TryPeek(out var oldest))
            {
                var availableAt = oldest.time.Add(_windowSize);
                var remaining = availableAt - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            return TimeSpan.Zero;
        }

        private void CleanOldEntries()
        {
            var cutoff = DateTimeOffset.UtcNow - _windowSize;

            while (_entries.TryPeek(out var oldest) && oldest.time < cutoff)
            {
                if (_entries.TryDequeue(out var removed))
                {
                    Interlocked.Add(ref _totalCount, -removed.count);
                }
            }
        }
    }
}
