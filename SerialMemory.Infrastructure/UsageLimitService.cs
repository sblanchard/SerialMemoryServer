using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for enforcing usage limits and rate limits.
/// </summary>
public sealed class UsageLimitService : IUsageLimitService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<UsageLimitService> _logger;

    public UsageLimitService(
        string connectionString,
        ILogger<UsageLimitService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public UsageLimitService(
        NpgsqlDataSource dataSource,
        ILogger<UsageLimitService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<UsageLimitCheckResult> CheckLimitsAsync(
        string tenantId,
        string workspaceId,
        UsageEventType eventType,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get current billing cycle and plan info
        var cycleInfo = await conn.QueryFirstOrDefaultAsync<BillingCycleInfo>(
            """
            SELECT bc.id AS BillingCycleId,
                                 bc.credits_allocated AS CreditsAllocated,
                                 bc.credits_used AS CreditsUsed,
                                 bc.cycle_end AS CycleEnd,
                                 tp.plan_name AS PlanName,
                                 tp.rate_limit_per_minute AS RateLimitPerMinute,
                                 tp.rate_limit_window_seconds AS RateLimitWindowSeconds,
                                 tp.max_memories AS MaxMemories,
                                 tp.max_entities AS MaxEntities,
                                 tp.is_active AS PlanIsActive
                          FROM billing_cycles bc
                          JOIN tenant_plans tp ON bc.plan_id = tp.id
                          WHERE bc.tenant_id = @TenantId
                            AND bc.workspace_id = @WorkspaceId
                            AND bc.is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        if (cycleInfo == null)
        {
            // No billing cycle - create one
            await conn.ExecuteScalarAsync<Guid>(
                "SELECT get_or_create_billing_cycle_v2(@TenantId, @WorkspaceId)",
                new { TenantId = tenantId, WorkspaceId = workspaceId });

            // Retry fetch
            cycleInfo = await conn.QueryFirstOrDefaultAsync<BillingCycleInfo>(
                """
                SELECT bc.id AS BillingCycleId,
                                         bc.credits_allocated AS CreditsAllocated,
                                         bc.credits_used AS CreditsUsed,
                                         bc.cycle_end AS CycleEnd,
                                         tp.plan_name AS PlanName,
                                         tp.rate_limit_per_minute AS RateLimitPerMinute,
                                         tp.rate_limit_window_seconds AS RateLimitWindowSeconds,
                                         tp.max_memories AS MaxMemories,
                                         tp.max_entities AS MaxEntities,
                                         tp.is_active AS PlanIsActive
                                  FROM billing_cycles bc
                                  JOIN tenant_plans tp ON bc.plan_id = tp.id
                                  WHERE bc.tenant_id = @TenantId
                                    AND bc.workspace_id = @WorkspaceId
                                    AND bc.is_current = TRUE
                """,
                new { TenantId = tenantId, WorkspaceId = workspaceId });
        }

        if (cycleInfo == null)
        {
            _logger.LogWarning("Could not create billing cycle for tenant {TenantId}", tenantId);
            return new UsageLimitCheckResult { IsAllowed = true };
        }

        // Check if plan is active
        if (!cycleInfo.PlanIsActive)
        {
            return new UsageLimitCheckResult
            {
                IsAllowed = false,
                Violation = new UsageLimitViolation
                {
                    LimitType = UsageLimitType.PlanInactive,
                    Code = "PLAN_INACTIVE",
                    Message = $"Plan '{cycleInfo.PlanName}' is inactive"
                }
            };
        }

        var creditsRequired = UsageCreditCosts.GetCost(eventType);
        var creditsRemaining = cycleInfo.CreditsAllocated - cycleInfo.CreditsUsed;

        // Check credit limit
        if (creditsRequired > creditsRemaining)
        {
            _logger.LogWarning(
                "Credit limit exceeded for tenant {TenantId}. Required: {Required}, Remaining: {Remaining}",
                tenantId, creditsRequired, creditsRemaining);

            return new UsageLimitCheckResult
            {
                IsAllowed = false,
                CreditsRemaining = creditsRemaining,
                CreditsRequired = creditsRequired,
                Violation = new UsageLimitViolation
                {
                    LimitType = UsageLimitType.CreditLimitExceeded,
                    Code = "CREDIT_LIMIT_EXCEEDED",
                    Message = $"Credit limit exceeded. Required: {creditsRequired:F2}, Remaining: {creditsRemaining:F2}",
                    RetryAfter = cycleInfo.CycleEnd,
                    Details = new Dictionary<string, object>
                    {
                        ["credits_required"] = creditsRequired,
                        ["credits_remaining"] = creditsRemaining,
                        ["cycle_end"] = cycleInfo.CycleEnd.ToString("O")
                    }
                }
            };
        }

        // Check rate limit if applicable
        int? rateLimitRemaining = null;
        if (cycleInfo.RateLimitPerMinute.HasValue)
        {
            var rateCheckResult = await conn.QueryFirstAsync<RateLimitCheckResult>(
                "SELECT * FROM check_rate_limit(@TenantId, @WorkspaceId, @Limit, @Window)",
                new
                {
                    TenantId = tenantId,
                    WorkspaceId = workspaceId,
                    Limit = cycleInfo.RateLimitPerMinute.Value,
                    Window = cycleInfo.RateLimitWindowSeconds ?? 60
                });

            rateLimitRemaining = cycleInfo.RateLimitPerMinute.Value - rateCheckResult.current_count;

            if (!rateCheckResult.allowed)
            {
                _logger.LogWarning(
                    "Rate limit exceeded for tenant {TenantId}. Limit: {Limit}/min, Current: {Current}",
                    tenantId, cycleInfo.RateLimitPerMinute.Value, rateCheckResult.current_count);

                return new UsageLimitCheckResult
                {
                    IsAllowed = false,
                    CreditsRemaining = creditsRemaining,
                    CreditsRequired = creditsRequired,
                    RateLimitRemaining = 0,
                    RateLimitWindow = cycleInfo.RateLimitWindowSeconds ?? 60,
                    Violation = new UsageLimitViolation
                    {
                        LimitType = UsageLimitType.RateLimitExceeded,
                        Code = "RATE_LIMIT_EXCEEDED",
                        Message = $"Rate limit exceeded. Limit: {cycleInfo.RateLimitPerMinute}/min, Current: {rateCheckResult.current_count}",
                        RetryAfter = rateCheckResult.retry_after,
                        Details = new Dictionary<string, object>
                        {
                            ["limit_per_minute"] = cycleInfo.RateLimitPerMinute.Value,
                            ["current_count"] = rateCheckResult.current_count,
                            ["retry_after"] = rateCheckResult.retry_after?.ToString("O") ?? ""
                        }
                    }
                };
            }
        }

        // Check max memories limit for ingest operations
        if (cycleInfo.MaxMemories.HasValue && eventType == UsageEventType.MemoryIngest)
        {
            var memoryCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId",
                new { TenantId = tenantId });

            if (memoryCount >= cycleInfo.MaxMemories.Value)
            {
                return new UsageLimitCheckResult
                {
                    IsAllowed = false,
                    CreditsRemaining = creditsRemaining,
                    Violation = new UsageLimitViolation
                    {
                        LimitType = UsageLimitType.MaxMemoriesExceeded,
                        Code = "MAX_MEMORIES_EXCEEDED",
                        Message = $"Maximum memories limit exceeded. Limit: {cycleInfo.MaxMemories}, Current: {memoryCount}",
                        Details = new Dictionary<string, object>
                        {
                            ["max_memories"] = cycleInfo.MaxMemories.Value,
                            ["current_count"] = memoryCount
                        }
                    }
                };
            }
        }

        return new UsageLimitCheckResult
        {
            IsAllowed = true,
            CreditsRemaining = creditsRemaining,
            CreditsRequired = creditsRequired,
            RateLimitRemaining = rateLimitRemaining,
            RateLimitWindow = cycleInfo.RateLimitWindowSeconds ?? 60
        };
    }

    public async Task RecordRateLimitHitAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await conn.ExecuteAsync(
            "SELECT record_rate_limit_hit(@TenantId, @WorkspaceId)",
            new { TenantId = tenantId, WorkspaceId = workspaceId });
    }

    public async Task<UsageSummary> GetUsageSummaryAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var cycleInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(
            """
            SELECT bc.credits_allocated,
                                 bc.credits_used,
                                 bc.cycle_start,
                                 bc.cycle_end,
                                 tp.plan_name,
                                 tp.rate_limit_per_minute,
                                 tp.max_memories,
                                 tp.max_entities
                          FROM billing_cycles bc
                          JOIN tenant_plans tp ON bc.plan_id = tp.id
                          WHERE bc.tenant_id = @TenantId
                            AND bc.workspace_id = @WorkspaceId
                            AND bc.is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        if (cycleInfo == null)
        {
            return new UsageSummary
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                PlanName = "none"
            };
        }

        int? currentRateCount = null;
        if (cycleInfo.rate_limit_per_minute != null)
        {
            var rateCheck = await conn.QueryFirstAsync<RateLimitCheckResult>(
                "SELECT * FROM check_rate_limit(@TenantId, @WorkspaceId, @Limit, 60)",
                new
                {
                    TenantId = tenantId,
                    WorkspaceId = workspaceId,
                    Limit = (int)cycleInfo.rate_limit_per_minute
                });
            currentRateCount = rateCheck.current_count;
        }

        int? currentMemories = null;
        int? currentEntities = null;

        if (cycleInfo.max_memories != null || cycleInfo.max_entities != null)
        {
            var counts = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT
                                    (SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId) AS memory_count,
                                    (SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId) AS entity_count
                """,
                new { TenantId = tenantId });

            if (counts != null)
            {
                currentMemories = (int)(counts.memory_count ?? 0);
                currentEntities = (int)(counts.entity_count ?? 0);
            }
        }

        return new UsageSummary
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            PlanName = cycleInfo.plan_name,
            CreditsAllocated = cycleInfo.credits_allocated,
            CreditsUsed = cycleInfo.credits_used,
            CycleStart = cycleInfo.cycle_start,
            CycleEnd = cycleInfo.cycle_end,
            RateLimitPerMinute = (int?)cycleInfo.rate_limit_per_minute,
            CurrentRateCount = currentRateCount,
            MaxMemories = (int?)cycleInfo.max_memories,
            CurrentMemories = currentMemories,
            MaxEntities = (int?)cycleInfo.max_entities,
            CurrentEntities = currentEntities
        };
    }

    private sealed class BillingCycleInfo
    {
        public Guid BillingCycleId { get; set; }
        public decimal CreditsAllocated { get; set; }
        public decimal CreditsUsed { get; set; }
        public DateTimeOffset CycleEnd { get; set; }
        public string PlanName { get; set; } = "";
        public int? RateLimitPerMinute { get; set; }
        public int? RateLimitWindowSeconds { get; set; }
        public int? MaxMemories { get; set; }
        public int? MaxEntities { get; set; }
        public bool PlanIsActive { get; set; }
    }

    private sealed class RateLimitCheckResult
    {
        public bool allowed { get; set; }
        public int current_count { get; set; }
        public int limit_per_window { get; set; }
        public DateTimeOffset? retry_after { get; set; }
    }

    public async Task<UsageRecordResult> RecordUsageAsync(
        string tenantId,
        string workspaceId,
        UsageEventType eventType,
        Guid? memoryId = null,
        string? userId = null,
        Guid? sessionId = null,
        int? latencyMs = null,
        bool success = true,
        string? errorMessage = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var creditsRequired = UsageCreditCosts.GetCost(eventType);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var consumeResult = await conn.QueryFirstOrDefaultAsync<ConsumeCreditsResult>(
            @"SELECT * FROM consume_credits(@TenantId, @WorkspaceId, @EventType, @Credits, @MemoryId, @UserId, @SessionId, @LatencyMs, @Success, @ErrorMessage, @Metadata)",
            new
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                EventType = UsageCreditCosts.ToSnakeCase(eventType),
                Credits = creditsRequired,
                MemoryId = memoryId,
                UserId = userId,
                SessionId = sessionId,
                LatencyMs = latencyMs,
                Success = success,
                ErrorMessage = errorMessage,
                Metadata = metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : null
            });

        if (consumeResult == null)
        {
            _logger.LogWarning("Failed to consume credits for tenant {TenantId}", tenantId);
            return new UsageRecordResult { Success = false, CreditsConsumed = 0 };
        }

        var triggeredAlerts = new List<UsageAlert>();
        var usagePercent = consumeResult.usage_percent;

        if (usagePercent >= 100)
        {
            triggeredAlerts.Add(new UsageAlert
            {
                AlertType = UsageAlertType.CreditLimitReached,
                ThresholdPercent = 100,
                CurrentPercent = usagePercent,
                Message = "Credit limit reached.",
                TriggeredAt = DateTimeOffset.UtcNow
            });
        }
        else if (usagePercent >= 90)
        {
            triggeredAlerts.Add(new UsageAlert
            {
                AlertType = UsageAlertType.CreditWarning90,
                ThresholdPercent = 90,
                CurrentPercent = usagePercent,
                Message = $"90% of credit limit used ({usagePercent:F1}%).",
                TriggeredAt = DateTimeOffset.UtcNow
            });
        }
        else if (usagePercent >= 75)
        {
            triggeredAlerts.Add(new UsageAlert
            {
                AlertType = UsageAlertType.CreditWarning75,
                ThresholdPercent = 75,
                CurrentPercent = usagePercent,
                Message = $"75% of credit limit used ({usagePercent:F1}%).",
                TriggeredAt = DateTimeOffset.UtcNow
            });
        }

        Core.Telemetry.Metrics.RecordCreditsConsumed(
            UsageCreditCosts.ToSnakeCase(eventType),
            creditsRequired,
            tenantId);

        return new UsageRecordResult
        {
            Success = true,
            CreditsConsumed = creditsRequired,
            CreditsRemaining = consumeResult.credits_remaining,
            UsagePercentage = usagePercent,
            TriggeredAlerts = triggeredAlerts
        };
    }

    public async Task<IReadOnlyList<UsageAlert>> GetActiveAlertsAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var alerts = await conn.QueryAsync<AlertRecord>(
            @"SELECT ua.alert_type, ua.threshold_percent, ua.current_percent, ua.message, ua.triggered_at, ua.acknowledged_at
              FROM usage_alerts ua
              JOIN billing_cycles bc ON ua.billing_cycle_id = bc.id
              WHERE bc.tenant_id = @TenantId AND bc.workspace_id = @WorkspaceId AND bc.is_current = TRUE
              ORDER BY ua.triggered_at DESC",
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        return alerts.Select(a => new UsageAlert
        {
            AlertType = Enum.TryParse<UsageAlertType>(a.alert_type, out var at) ? at : UsageAlertType.CreditWarning75,
            ThresholdPercent = a.threshold_percent,
            CurrentPercent = a.current_percent,
            Message = a.message,
            TriggeredAt = a.triggered_at,
            Acknowledged = a.acknowledged_at.HasValue
        }).ToList();
    }

    private sealed class ConsumeCreditsResult
    {
        public bool success { get; set; }
        public decimal credits_consumed { get; set; }
        public decimal credits_remaining { get; set; }
        public decimal usage_percent { get; set; }
        public string? error_message { get; set; }
    }

    private sealed class AlertRecord
    {
        public string alert_type { get; set; } = "";
        public int threshold_percent { get; set; }
        public decimal current_percent { get; set; }
        public string message { get; set; } = "";
        public DateTimeOffset triggered_at { get; set; }
        public DateTimeOffset? acknowledged_at { get; set; }
    }
}
