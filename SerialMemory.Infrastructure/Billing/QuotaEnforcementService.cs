using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Deployment;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Billing;

/// <summary>
/// Service for enforcing usage quotas and billing limits.
/// Integrates with kill switch, plan limits, rate limits, and credit consumption.
/// </summary>
public sealed class QuotaEnforcementService : Core.Interfaces.IQuotaEnforcementService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IKillSwitchService _killSwitchService;
    private readonly IDeploymentContext _deploymentContext;
    private readonly ILogger<QuotaEnforcementService> _logger;

    public QuotaEnforcementService(
        NpgsqlDataSource dataSource,
        IKillSwitchService killSwitchService,
        IDeploymentContext deploymentContext,
        ILogger<QuotaEnforcementService> logger)
    {
        _dataSource = dataSource;
        _killSwitchService = killSwitchService;
        _deploymentContext = deploymentContext;
        _logger = logger;
    }

    public QuotaEnforcementService(
        string connectionString,
        IKillSwitchService killSwitchService,
        IDeploymentContext deploymentContext,
        ILogger<QuotaEnforcementService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _killSwitchService = killSwitchService;
        _deploymentContext = deploymentContext;
        _logger = logger;
    }

    public async Task<QuotaCheckResult> CheckAsync(
        string tenantId,
        string workspaceId,
        UsageEventType operation,
        int units = 1,
        CancellationToken ct = default)
    {
        // In self-hosted mode without quotas enabled, always allow
        if (!_deploymentContext.QuotasEnabled)
        {
            var cost = GetOperationCost(operation, units);
            return QuotaCheckResult.Allowed(cost, decimal.MaxValue, decimal.MaxValue);
        }

        // Step 1: Check kill switch (already handled by middleware, but double-check)
        var killState = await _killSwitchService.GetStateAsync(tenantId, ct: ct);
        if (killState.IsBlocked)
        {
            return QuotaCheckResult.Denied(
                new QuotaViolation
                {
                    Type = QuotaViolationType.KillSwitch,
                    Code = killState.GetErrorCode(),
                    Message = killState.GetEffectiveReason(),
                    RetryAfter = killState.ExpiresAt
                },
                GetOperationCost(operation, units),
                0,
                0);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Step 2: Get billing cycle and plan info
        var cycleInfo = await GetOrCreateBillingCycleAsync(conn, tenantId, workspaceId, ct);
        if (cycleInfo == null)
        {
            // Should not happen in SaaS mode, but handle gracefully
            _logger.LogWarning("No billing cycle found for tenant {TenantId}", tenantId);
            var fallbackCost = GetOperationCost(operation, units);
            return QuotaCheckResult.Allowed(fallbackCost, 1000, 1000);
        }

        // Step 3: Check if plan is active
        if (!cycleInfo.PlanIsActive)
        {
            return QuotaCheckResult.Denied(
                new QuotaViolation
                {
                    Type = QuotaViolationType.PlanInactive,
                    Code = "PLAN_INACTIVE",
                    Message = $"Plan '{cycleInfo.PlanName}' is inactive. Please contact support."
                },
                GetOperationCost(operation, units),
                cycleInfo.CreditsRemaining,
                cycleInfo.CreditsAllocated);
        }

        // Step 4: Calculate credit cost
        var creditCost = GetOperationCost(operation, units);
        var warnings = new List<QuotaWarning>();

        // Step 5: Check credit limit
        if (creditCost > cycleInfo.CreditsRemaining)
        {
            _logger.LogWarning(
                "Credit limit exceeded for tenant {TenantId}. Required: {Cost}, Remaining: {Remaining}",
                tenantId, creditCost, cycleInfo.CreditsRemaining);

            return QuotaCheckResult.Denied(
                new QuotaViolation
                {
                    Type = QuotaViolationType.CreditLimitExceeded,
                    Code = "CREDIT_LIMIT_EXCEEDED",
                    Message = $"Credit limit exceeded. Required: {creditCost:F2}, Remaining: {cycleInfo.CreditsRemaining:F2}. Cycle resets on {cycleInfo.CycleEnd:yyyy-MM-dd}.",
                    RetryAfter = cycleInfo.CycleEnd,
                    Details = new Dictionary<string, object>
                    {
                        ["credits_required"] = creditCost,
                        ["credits_remaining"] = cycleInfo.CreditsRemaining,
                        ["cycle_end"] = cycleInfo.CycleEnd.ToString("O")
                    }
                },
                creditCost,
                cycleInfo.CreditsRemaining,
                cycleInfo.CreditsAllocated);
        }

        // Step 6: Check rate limit if applicable
        int? rateLimitRemaining = null;
        DateTimeOffset? rateLimitResetsAt = null;

        if (cycleInfo.RateLimitPerMinute.HasValue)
        {
            var rateCheck = await CheckRateLimitAsync(conn, tenantId, workspaceId, cycleInfo.RateLimitPerMinute.Value, cycleInfo.RateLimitWindowSeconds ?? 60);
            rateLimitRemaining = cycleInfo.RateLimitPerMinute.Value - rateCheck.CurrentCount;
            rateLimitResetsAt = rateCheck.ResetsAt;

            if (!rateCheck.Allowed)
            {
                _logger.LogWarning(
                    "Rate limit exceeded for tenant {TenantId}. Limit: {Limit}/window, Current: {Current}",
                    tenantId, cycleInfo.RateLimitPerMinute.Value, rateCheck.CurrentCount);

                return QuotaCheckResult.Denied(
                    new QuotaViolation
                    {
                        Type = QuotaViolationType.RateLimitExceeded,
                        Code = "RATE_LIMIT_EXCEEDED",
                        Message = $"Rate limit exceeded. Limit: {cycleInfo.RateLimitPerMinute}/min. Please wait before retrying.",
                        RetryAfter = rateCheck.ResetsAt,
                        Details = new Dictionary<string, object>
                        {
                            ["limit_per_minute"] = cycleInfo.RateLimitPerMinute.Value,
                            ["current_count"] = rateCheck.CurrentCount,
                            ["resets_at"] = rateCheck.ResetsAt.ToString("O")
                        }
                    },
                    creditCost,
                    cycleInfo.CreditsRemaining,
                    cycleInfo.CreditsAllocated) with
                {
                    RateLimitRemaining = 0,
                    RateLimitWindowSeconds = cycleInfo.RateLimitWindowSeconds ?? 60,
                    RateLimitResetsAt = rateCheck.ResetsAt
                };
            }

            // Add warning if approaching rate limit
            if (rateLimitRemaining <= 5)
            {
                warnings.Add(new QuotaWarning
                {
                    Type = QuotaWarningType.ApproachingRateLimit,
                    Message = $"Approaching rate limit. {rateLimitRemaining} requests remaining.",
                    Threshold = cycleInfo.RateLimitPerMinute.Value,
                    CurrentValue = rateCheck.CurrentCount
                });
            }
        }

        // Step 7: Check max memories for ingest operations
        if (cycleInfo.MaxMemories.HasValue && operation == UsageEventType.MemoryIngest)
        {
            var memoryCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId::uuid",
                new { TenantId = tenantId });

            if (memoryCount >= cycleInfo.MaxMemories.Value)
            {
                return QuotaCheckResult.Denied(
                    new QuotaViolation
                    {
                        Type = QuotaViolationType.MaxMemoriesExceeded,
                        Code = "MAX_MEMORIES_EXCEEDED",
                        Message = $"Maximum memories limit reached. Limit: {cycleInfo.MaxMemories}, Current: {memoryCount}. Upgrade your plan for more storage.",
                        Details = new Dictionary<string, object>
                        {
                            ["max_memories"] = cycleInfo.MaxMemories.Value,
                            ["current_count"] = memoryCount
                        }
                    },
                    creditCost,
                    cycleInfo.CreditsRemaining,
                    cycleInfo.CreditsAllocated);
            }

            // Warning if approaching limit
            var percentUsed = (decimal)memoryCount / cycleInfo.MaxMemories.Value * 100;
            if (percentUsed >= 80)
            {
                warnings.Add(new QuotaWarning
                {
                    Type = QuotaWarningType.ApproachingMemoryLimit,
                    Message = $"Approaching memory limit. {memoryCount}/{cycleInfo.MaxMemories} memories used ({percentUsed:F1}%).",
                    Threshold = 80,
                    CurrentValue = percentUsed
                });
            }
        }

        // Step 8: Check credit usage warnings
        var usagePercent = cycleInfo.CreditsAllocated > 0
            ? ((cycleInfo.CreditsAllocated - cycleInfo.CreditsRemaining) / cycleInfo.CreditsAllocated) * 100
            : 0;

        if (usagePercent >= 90)
        {
            warnings.Add(new QuotaWarning
            {
                Type = QuotaWarningType.ApproachingCreditLimit,
                Message = $"90% of credit limit used. {cycleInfo.CreditsRemaining:F2} credits remaining.",
                Threshold = 90,
                CurrentValue = usagePercent
            });
        }
        else if (usagePercent >= 75)
        {
            warnings.Add(new QuotaWarning
            {
                Type = QuotaWarningType.ApproachingCreditLimit,
                Message = $"75% of credit limit used. {cycleInfo.CreditsRemaining:F2} credits remaining.",
                Threshold = 75,
                CurrentValue = usagePercent
            });
        }

        // All checks passed
        return new QuotaCheckResult
        {
            IsAllowed = true,
            CreditsCost = creditCost,
            CreditsRemaining = cycleInfo.CreditsRemaining,
            CreditsAllocated = cycleInfo.CreditsAllocated,
            RateLimitRemaining = rateLimitRemaining,
            RateLimitWindowSeconds = cycleInfo.RateLimitWindowSeconds ?? 60,
            RateLimitResetsAt = rateLimitResetsAt,
            Warnings = warnings
        };
    }

    public async Task<QuotaRecordResult> RecordAsync(
        string tenantId,
        string workspaceId,
        UsageEventType operation,
        int units = 1,
        Guid? memoryId = null,
        string? userId = null,
        int? latencyMs = null,
        bool success = true,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        var creditCost = GetOperationCost(operation, units);

        // In self-hosted mode without quotas, just log but don't block
        if (!_deploymentContext.QuotasEnabled)
        {
            return new QuotaRecordResult
            {
                Success = true,
                CreditsConsumed = creditCost,
                CreditsRemaining = decimal.MaxValue,
                UsagePercentage = 0
            };
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        try
        {
            // Record usage event and consume credits
            var result = await conn.QueryFirstOrDefaultAsync<ConsumeCreditsResult>(
                """
                SELECT * FROM consume_credits(
                    @TenantId, @WorkspaceId, @EventType, @Credits,
                    @MemoryId, @UserId, @SessionId, @LatencyMs, @Success, @ErrorMessage, @Metadata
                )
                """,
                new
                {
                    TenantId = tenantId,
                    WorkspaceId = workspaceId,
                    EventType = UsageCreditCosts.ToSnakeCase(operation),
                    Credits = creditCost,
                    MemoryId = memoryId,
                    UserId = userId,
                    SessionId = (Guid?)null,
                    LatencyMs = latencyMs,
                    Success = success,
                    ErrorMessage = errorMessage,
                    Metadata = (string?)null
                });

            if (result == null || !result.success)
            {
                _logger.LogWarning("Failed to record usage for tenant {TenantId}: {Error}",
                    tenantId, result?.error_message ?? "Unknown error");

                return new QuotaRecordResult
                {
                    Success = false,
                    CreditsConsumed = 0,
                    CreditsRemaining = 0,
                    UsagePercentage = 0
                };
            }

            // Check and trigger alerts
            var triggeredAlerts = await CheckAndTriggerAlertsAsync(conn, tenantId, workspaceId, result.usage_percent, ct);

            // Record telemetry
            Core.Telemetry.Metrics.RecordCreditsConsumed(
                UsageCreditCosts.ToSnakeCase(operation),
                creditCost,
                tenantId);

            // Determine if we should auto-throttle
            var shouldThrottle = result.usage_percent >= 100;
            if (shouldThrottle)
            {
                // Auto-enable TenantNoIngest if 100% usage
                await _killSwitchService.EnableTenantNoIngestAsync(
                    tenantId,
                    "Credit limit reached - automatic throttle",
                    "system",
                    TimeSpan.FromHours(1), // Auto-lift after 1 hour
                    ct);
            }

            return new QuotaRecordResult
            {
                Success = true,
                CreditsConsumed = creditCost,
                CreditsRemaining = result.credits_remaining,
                UsagePercentage = result.usage_percent,
                TriggeredAlerts = triggeredAlerts,
                ShouldThrottle = shouldThrottle
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording usage for tenant {TenantId}", tenantId);
            return new QuotaRecordResult
            {
                Success = false,
                CreditsConsumed = 0,
                CreditsRemaining = 0,
                UsagePercentage = 0
            };
        }
    }

    public async Task<QuotaStatus> GetStatusAsync(
        string tenantId,
        string workspaceId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var cycleInfo = await GetBillingCycleInfoAsync(conn, tenantId, workspaceId);

        if (cycleInfo == null)
        {
            return new QuotaStatus
            {
                PlanName = "none",
                PlanDisplayName = "No Plan",
                CreditsAllocated = 0,
                CreditsUsed = 0,
                CycleStart = DateTimeOffset.UtcNow,
                CycleEnd = DateTimeOffset.UtcNow.AddDays(30)
            };
        }

        // Get current rate count
        int? currentRateCount = null;
        if (cycleInfo.RateLimitPerMinute.HasValue)
        {
            var rateCheck = await CheckRateLimitAsync(conn, tenantId, workspaceId, cycleInfo.RateLimitPerMinute.Value, 60);
            currentRateCount = rateCheck.CurrentCount;
        }

        // Get memory/entity counts if limits exist
        int? currentMemories = null;
        int? currentEntities = null;
        if (cycleInfo.MaxMemories.HasValue || cycleInfo.MaxEntities.HasValue)
        {
            var counts = await conn.QueryFirstOrDefaultAsync<(int memory_count, int entity_count)>(
                """
                SELECT
                    (SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId::uuid) AS memory_count,
                    (SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId::uuid) AS entity_count
                """,
                new { TenantId = tenantId });
            currentMemories = counts.memory_count;
            currentEntities = counts.entity_count;
        }

        // Get kill switch state
        var killState = await _killSwitchService.GetStateAsync(tenantId, ct: ct);

        // Get active alerts
        var alerts = await conn.QueryAsync<AlertDto>(
            """
            SELECT ua.alert_type, ua.threshold_percent, ua.message, ua.triggered_at, ua.acknowledged_at
            FROM usage_alerts ua
            JOIN billing_cycles bc ON ua.billing_cycle_id = bc.id
            WHERE bc.tenant_id = @TenantId::uuid AND bc.workspace_id = @WorkspaceId AND bc.is_current = TRUE
            ORDER BY ua.triggered_at DESC
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        return new QuotaStatus
        {
            PlanName = cycleInfo.PlanName,
            PlanDisplayName = cycleInfo.PlanDisplayName,
            CreditsAllocated = cycleInfo.CreditsAllocated,
            CreditsUsed = cycleInfo.CreditsAllocated - cycleInfo.CreditsRemaining,
            CycleStart = cycleInfo.CycleStart,
            CycleEnd = cycleInfo.CycleEnd,
            RateLimitPerMinute = cycleInfo.RateLimitPerMinute,
            CurrentRateCount = currentRateCount,
            MaxMemories = cycleInfo.MaxMemories,
            CurrentMemories = currentMemories,
            MaxEntities = cycleInfo.MaxEntities,
            CurrentEntities = currentEntities,
            KillSwitchActive = killState.IsBlocked || killState.IsReadOnly || killState.TenantNoIngest,
            KillSwitchReason = killState.IsBlocked || killState.IsReadOnly || killState.TenantNoIngest
                ? killState.GetEffectiveReason()
                : null,
            ActiveAlerts = alerts.Select(a => new ActiveAlert
            {
                AlertType = a.alert_type,
                ThresholdPercent = a.threshold_percent,
                Message = a.message,
                TriggeredAt = a.triggered_at,
                Acknowledged = a.acknowledged_at.HasValue
            }).ToList()
        };
    }

    public async Task<IReadOnlyList<TriggeredAlert>> CheckAlertsAsync(
        string tenantId,
        string workspaceId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var cycleInfo = await GetBillingCycleInfoAsync(conn, tenantId, workspaceId);
        if (cycleInfo == null) return [];

        var usagePercent = cycleInfo.CreditsAllocated > 0
            ? ((cycleInfo.CreditsAllocated - cycleInfo.CreditsRemaining) / cycleInfo.CreditsAllocated) * 100
            : 0;

        return await CheckAndTriggerAlertsAsync(conn, tenantId, workspaceId, usagePercent, ct);
    }

    public decimal GetOperationCost(UsageEventType operation, int units = 1)
    {
        return UsageCreditCosts.GetCost(operation) * units;
    }

    #region Private Helpers

    private async Task<BillingCycleInfo?> GetOrCreateBillingCycleAsync(
        NpgsqlConnection conn,
        string tenantId,
        string workspaceId,
        CancellationToken ct)
    {
        var cycleInfo = await GetBillingCycleInfoAsync(conn, tenantId, workspaceId);

        if (cycleInfo == null)
        {
            // Try to create a billing cycle
            await conn.ExecuteScalarAsync<Guid>(
                "SELECT get_or_create_billing_cycle_v2(@TenantId, @WorkspaceId)",
                new { TenantId = tenantId, WorkspaceId = workspaceId });

            cycleInfo = await GetBillingCycleInfoAsync(conn, tenantId, workspaceId);
        }

        return cycleInfo;
    }

    private static async Task<BillingCycleInfo?> GetBillingCycleInfoAsync(
        NpgsqlConnection conn,
        string tenantId,
        string workspaceId)
    {
        return await conn.QueryFirstOrDefaultAsync<BillingCycleInfo>(
            """
            SELECT
                bc.id AS BillingCycleId,
                bc.credits_allocated AS CreditsAllocated,
                bc.credits_allocated - bc.credits_used AS CreditsRemaining,
                bc.cycle_start AS CycleStart,
                bc.cycle_end AS CycleEnd,
                tp.plan_name AS PlanName,
                tp.display_name AS PlanDisplayName,
                tp.rate_limit_per_minute AS RateLimitPerMinute,
                tp.rate_limit_window_seconds AS RateLimitWindowSeconds,
                tp.max_memories AS MaxMemories,
                tp.max_entities AS MaxEntities,
                tp.is_active AS PlanIsActive
            FROM billing_cycles bc
            JOIN tenant_plans tp ON bc.plan_id = tp.id
            WHERE bc.tenant_id = @TenantId::uuid
              AND bc.workspace_id = @WorkspaceId
              AND bc.is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });
    }

    private static async Task<RateLimitCheck> CheckRateLimitAsync(
        NpgsqlConnection conn,
        string tenantId,
        string workspaceId,
        int limit,
        int windowSeconds)
    {
        var result = await conn.QueryFirstAsync<RateLimitCheckResult>(
            "SELECT * FROM check_rate_limit(@TenantId, @WorkspaceId, @Limit, @Window)",
            new { TenantId = tenantId, WorkspaceId = workspaceId, Limit = limit, Window = windowSeconds });

        return new RateLimitCheck
        {
            Allowed = result.allowed,
            CurrentCount = result.current_count,
            ResetsAt = result.retry_after ?? DateTimeOffset.UtcNow.AddSeconds(windowSeconds)
        };
    }

    private async Task<IReadOnlyList<TriggeredAlert>> CheckAndTriggerAlertsAsync(
        NpgsqlConnection conn,
        string tenantId,
        string workspaceId,
        decimal usagePercent,
        CancellationToken ct)
    {
        var triggered = new List<TriggeredAlert>();

        // Get thresholds to check
        var thresholds = new[] { 80, 90, 100 };

        foreach (var threshold in thresholds)
        {
            if (usagePercent >= threshold)
            {
                // Check if already triggered for this cycle
                var alreadyTriggered = await conn.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM usage_alerts ua
                        JOIN billing_cycles bc ON ua.billing_cycle_id = bc.id
                        WHERE bc.tenant_id = @TenantId::uuid
                          AND bc.workspace_id = @WorkspaceId
                          AND bc.is_current = TRUE
                          AND ua.threshold_percent = @Threshold
                    )
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId, Threshold = threshold });

                if (!alreadyTriggered)
                {
                    // Insert alert
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO usage_alerts (id, billing_cycle_id, alert_type, threshold_percent, current_percent, message, triggered_at)
                        SELECT @Id, bc.id, @AlertType, @Threshold, @CurrentPercent, @Message, NOW()
                        FROM billing_cycles bc
                        WHERE bc.tenant_id = @TenantId::uuid
                          AND bc.workspace_id = @WorkspaceId
                          AND bc.is_current = TRUE
                        """,
                        new
                        {
                            Id = Guid.CreateVersion7(),
                            TenantId = tenantId,
                            WorkspaceId = workspaceId,
                            AlertType = GetAlertType(threshold),
                            Threshold = threshold,
                            CurrentPercent = usagePercent,
                            Message = GetAlertMessage(threshold, usagePercent)
                        });

                    triggered.Add(new TriggeredAlert
                    {
                        AlertType = GetAlertType(threshold),
                        ThresholdPercent = threshold,
                        CurrentPercent = usagePercent,
                        Message = GetAlertMessage(threshold, usagePercent),
                        TriggeredAt = DateTimeOffset.UtcNow
                    });

                    _logger.LogWarning(
                        "Usage alert triggered for tenant {TenantId}: {Threshold}% threshold crossed (current: {Current}%)",
                        tenantId, threshold, usagePercent);
                }
            }
        }

        return triggered;
    }

    private static string GetAlertType(int threshold) => threshold switch
    {
        100 => "CREDIT_LIMIT_REACHED",
        >= 90 => "CREDIT_WARNING_90",
        >= 80 => "CREDIT_WARNING_80",
        _ => "CREDIT_INFO"
    };

    private static string GetAlertMessage(int threshold, decimal currentPercent) => threshold switch
    {
        100 => $"Credit limit reached ({currentPercent:F1}% used). Operations may be limited.",
        >= 90 => $"90% of credit limit used ({currentPercent:F1}%). Consider upgrading your plan.",
        >= 80 => $"80% of credit limit used ({currentPercent:F1}%).",
        _ => $"Usage at {currentPercent:F1}%"
    };

    #endregion

    #region DTOs

    private sealed class BillingCycleInfo
    {
        public Guid BillingCycleId { get; set; }
        public decimal CreditsAllocated { get; set; }
        public decimal CreditsRemaining { get; set; }
        public DateTimeOffset CycleStart { get; set; }
        public DateTimeOffset CycleEnd { get; set; }
        public string PlanName { get; set; } = "";
        public string PlanDisplayName { get; set; } = "";
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

    private sealed class RateLimitCheck
    {
        public bool Allowed { get; set; }
        public int CurrentCount { get; set; }
        public DateTimeOffset ResetsAt { get; set; }
    }

    private sealed class ConsumeCreditsResult
    {
        public bool success { get; set; }
        public decimal credits_consumed { get; set; }
        public decimal credits_remaining { get; set; }
        public decimal usage_percent { get; set; }
        public string? error_message { get; set; }
    }

    private sealed class AlertDto
    {
        public string alert_type { get; set; } = "";
        public int threshold_percent { get; set; }
        public string message { get; set; } = "";
        public DateTimeOffset triggered_at { get; set; }
        public DateTimeOffset? acknowledged_at { get; set; }
    }

    #endregion
}
