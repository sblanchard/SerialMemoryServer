using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for protecting against cost blow-ups with budget ceilings and emergency cutoffs.
/// </summary>
public sealed class CostProtectionService : ICostProtectionService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<CostProtectionService> _logger;

    public CostProtectionService(string connectionString, ILogger<CostProtectionService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public CostProtectionService(NpgsqlDataSource dataSource, ILogger<CostProtectionService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<BudgetCheckResult> CheckBudgetAsync(
        string tenantId,
        decimal estimatedCost,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Check for emergency cutoff first
        var cutoff = await conn.QueryFirstOrDefaultAsync<EmergencyCutoffDto>(
            """
            SELECT ends_at, reason FROM emergency_cutoffs
                          WHERE tenant_id = @TenantId::uuid
                            AND is_active = TRUE
                            AND (ends_at IS NULL OR ends_at > NOW())
            """,
            new { TenantId = tenantId });

        if (cutoff != null)
        {
            _logger.LogWarning(
                "Tenant {TenantId} is under emergency cutoff: {Reason}",
                tenantId, cutoff.reason);

            return new BudgetCheckResult
            {
                IsAllowed = false,
                CurrentUsage = 0,
                BudgetCeiling = 0,
                EstimatedCost = estimatedCost,
                Violation = new BudgetViolation
                {
                    Type = BudgetViolationType.EmergencyCutoff,
                    Code = "EMERGENCY_CUTOFF",
                    Message = $"Service suspended: {cutoff.reason}",
                    ResumesAt = cutoff.ends_at
                }
            };
        }

        // Get current usage and ceiling
        var budgetInfo = await conn.QueryFirstOrDefaultAsync<BudgetInfoDto>(
            """
            SELECT
                            bc.credits_used AS current_usage,
                            bc.credits_allocated AS budget_ceiling,
                            bc.cycle_start,
                            bc.cycle_end,
                            ts.plan,
                            COALESCE(ts.hard_limit_multiplier, 1.0) AS hard_limit_multiplier
                          FROM billing_cycles bc
                          JOIN tenant_settings ts ON bc.tenant_id = ts.tenant_id
                          WHERE bc.tenant_id = @TenantId::uuid
                            AND bc.is_current = TRUE
            """,
            new { TenantId = tenantId });

        if (budgetInfo == null)
        {
            // No billing cycle - allow with default limits
            return new BudgetCheckResult
            {
                IsAllowed = true,
                CurrentUsage = 0,
                BudgetCeiling = 1000,
                EstimatedCost = estimatedCost
            };
        }

        var effectiveCeiling = budgetInfo.budget_ceiling * budgetInfo.hard_limit_multiplier;
        var projectedUsage = budgetInfo.current_usage + estimatedCost;

        // Check hard ceiling (with some buffer for enterprise plans)
        if (projectedUsage > effectiveCeiling)
        {
            _logger.LogWarning(
                "Tenant {TenantId} would exceed budget ceiling. Current: {Current}, Estimated: {Estimated}, Ceiling: {Ceiling}",
                tenantId, budgetInfo.current_usage, estimatedCost, effectiveCeiling);

            return new BudgetCheckResult
            {
                IsAllowed = false,
                CurrentUsage = budgetInfo.current_usage,
                BudgetCeiling = effectiveCeiling,
                EstimatedCost = estimatedCost,
                Violation = new BudgetViolation
                {
                    Type = BudgetViolationType.CeilingExceeded,
                    Code = "BUDGET_CEILING_EXCEEDED",
                    Message = $"Budget ceiling exceeded. Current: {budgetInfo.current_usage:F2}, Ceiling: {effectiveCeiling:F2}",
                    ResumesAt = budgetInfo.cycle_end
                }
            };
        }

        // Check warning thresholds (log but allow)
        var usagePercent = budgetInfo.current_usage / effectiveCeiling * 100;
        switch (usagePercent)
        {
            case >= 90:
                _logger.LogWarning(
                    "Tenant {TenantId} at {Percent:F1}% of budget ceiling",
                    tenantId, usagePercent);
                break;
            case >= 75:
                _logger.LogInformation(
                    "Tenant {TenantId} at {Percent:F1}% of budget ceiling",
                    tenantId, usagePercent);
                break;
        }

        return new BudgetCheckResult
        {
            IsAllowed = true,
            CurrentUsage = budgetInfo.current_usage,
            BudgetCeiling = effectiveCeiling,
            EstimatedCost = estimatedCost
        };
    }

    public async Task RecordCostAsync(
        string tenantId,
        decimal cost,
        string operation,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Update billing cycle
        await conn.ExecuteAsync(
            """
            UPDATE billing_cycles
                          SET credits_used = credits_used + @Cost
                          WHERE tenant_id = @TenantId::uuid AND is_current = TRUE
            """,
            new { TenantId = tenantId, Cost = cost });

        // Log for analysis
        await conn.ExecuteAsync(
            """
            INSERT INTO cost_records (tenant_id, operation, cost, recorded_at)
                          VALUES (@TenantId::uuid, @Operation, @Cost, NOW())
                          ON CONFLICT DO NOTHING
            """,
            new { TenantId = tenantId, Operation = operation, Cost = cost });
    }

    public async Task TriggerEmergencyCutoffAsync(
        string tenantId,
        string reason,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var endsAt = duration.HasValue ? DateTimeOffset.UtcNow.Add(duration.Value) : (DateTimeOffset?)null;

        await conn.ExecuteAsync(
            """
            INSERT INTO emergency_cutoffs (id, tenant_id, reason, triggered_at, ends_at, is_active)
                          VALUES (@Id, @TenantId::uuid, @Reason, NOW(), @EndsAt, TRUE)
                          ON CONFLICT (tenant_id) WHERE is_active = TRUE
                          DO UPDATE SET reason = @Reason, triggered_at = NOW(), ends_at = @EndsAt
            """,
            new { Id = Guid.CreateVersion7(), TenantId = tenantId, Reason = reason, EndsAt = endsAt });

        _logger.LogCritical(
            "EMERGENCY CUTOFF TRIGGERED for tenant {TenantId}: {Reason}. Duration: {Duration}",
            tenantId, reason, duration?.ToString() ?? "indefinite");
    }

    public async Task LiftEmergencyCutoffAsync(
        string tenantId,
        string reason,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            UPDATE emergency_cutoffs
                          SET is_active = FALSE, lifted_at = NOW(), lift_reason = @Reason
                          WHERE tenant_id = @TenantId::uuid AND is_active = TRUE
            """,
            new { TenantId = tenantId, Reason = reason });

        _logger.LogWarning(
            "Emergency cutoff LIFTED for tenant {TenantId}: {Reason}",
            tenantId, reason);
    }

    public async Task<BudgetStatus> GetBudgetStatusAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var budgetInfo = await conn.QueryFirstOrDefaultAsync<BudgetInfoDto>(
            """
            SELECT
                            bc.credits_used AS current_usage,
                            bc.credits_allocated AS budget_ceiling,
                            bc.cycle_start,
                            bc.cycle_end,
                            ts.plan,
                            COALESCE(ts.hard_limit_multiplier, 1.0) AS hard_limit_multiplier
                          FROM billing_cycles bc
                          JOIN tenant_settings ts ON bc.tenant_id = ts.tenant_id
                          WHERE bc.tenant_id = @TenantId::uuid
                            AND bc.is_current = TRUE
            """,
            new { TenantId = tenantId });

        var cutoff = await conn.QueryFirstOrDefaultAsync<EmergencyCutoffDto>(
            """
            SELECT ends_at, reason FROM emergency_cutoffs
                          WHERE tenant_id = @TenantId::uuid
                            AND is_active = TRUE
            """,
            new { TenantId = tenantId });

        if (budgetInfo == null)
        {
            return new BudgetStatus
            {
                TenantId = tenantId,
                CurrentUsage = 0,
                BudgetCeiling = 1000,
                RemainingBudget = 1000,
                UsagePercentage = 0,
                IsEmergencyCutoffActive = cutoff != null,
                EmergencyCutoffEndsAt = cutoff?.ends_at,
                EmergencyCutoffReason = cutoff?.reason,
                CurrentPeriod = new BudgetPeriod
                {
                    Start = DateTimeOffset.UtcNow,
                    End = DateTimeOffset.UtcNow.AddDays(30),
                    DaysRemaining = 30
                }
            };
        }

        var effectiveCeiling = budgetInfo.budget_ceiling * budgetInfo.hard_limit_multiplier;

        return new BudgetStatus
        {
            TenantId = tenantId,
            CurrentUsage = budgetInfo.current_usage,
            BudgetCeiling = effectiveCeiling,
            RemainingBudget = effectiveCeiling - budgetInfo.current_usage,
            UsagePercentage = effectiveCeiling > 0 ? (budgetInfo.current_usage / effectiveCeiling) * 100 : 0,
            IsEmergencyCutoffActive = cutoff != null,
            EmergencyCutoffEndsAt = cutoff?.ends_at,
            EmergencyCutoffReason = cutoff?.reason,
            CurrentPeriod = new BudgetPeriod
            {
                Start = budgetInfo.cycle_start,
                End = budgetInfo.cycle_end,
                DaysRemaining = Math.Max(0, (int)(budgetInfo.cycle_end - DateTimeOffset.UtcNow).TotalDays)
            }
        };
    }

    public async Task SetAlertThresholdAsync(
        string tenantId,
        BudgetAlertThreshold threshold,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            """
            INSERT INTO budget_alert_thresholds (tenant_id, percentage, alert_type, enabled, notification_email, webhook_url)
                          VALUES (@TenantId::uuid, @Percentage, @AlertType, @Enabled, @NotificationEmail, @WebhookUrl)
                          ON CONFLICT (tenant_id, percentage)
                          DO UPDATE SET alert_type = @AlertType, enabled = @Enabled,
                                       notification_email = @NotificationEmail, webhook_url = @WebhookUrl
            """,
            new
            {
                TenantId = tenantId,
                threshold.Percentage,
                threshold.AlertType,
                threshold.Enabled,
                threshold.NotificationEmail,
                threshold.WebhookUrl
            });
    }

    public async Task<IReadOnlyList<TenantBudgetAlert>> GetBudgetAlertsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var alerts = await conn.QueryAsync<TenantBudgetAlertDto>(
            """
            SELECT
                            t.id AS tenant_id,
                            t.name AS tenant_name,
                            bc.credits_used AS current_usage,
                            bc.credits_allocated * COALESCE(ts.hard_limit_multiplier, 1.0) AS budget_ceiling
                          FROM tenants t
                          JOIN tenant_settings ts ON t.id = ts.tenant_id
                          JOIN billing_cycles bc ON t.id = bc.tenant_id AND bc.is_current = TRUE
                          WHERE t.status = 'active'
                            AND bc.credits_used > bc.credits_allocated * 0.5
                          ORDER BY (bc.credits_used / NULLIF(bc.credits_allocated, 0)) DESC
            """);

        return alerts.Select(a =>
        {
            var usagePercent = a.budget_ceiling > 0 ? (a.current_usage / a.budget_ceiling) * 100 : 0;
            var severity = usagePercent switch
            {
                >= 100 => BudgetAlertSeverity.Emergency,
                >= 90 => BudgetAlertSeverity.Critical,
                >= 75 => BudgetAlertSeverity.Warning,
                _ => BudgetAlertSeverity.Info
            };

            return new TenantBudgetAlert
            {
                TenantId = a.tenant_id.ToString(),
                TenantName = a.tenant_name,
                CurrentUsage = a.current_usage,
                BudgetCeiling = a.budget_ceiling,
                UsagePercentage = usagePercent,
                Severity = severity,
                Message = severity switch
                {
                    BudgetAlertSeverity.Emergency => $"Budget EXCEEDED: {usagePercent:F1}% used",
                    BudgetAlertSeverity.Critical => $"Budget critical: {usagePercent:F1}% used",
                    BudgetAlertSeverity.Warning => $"Budget warning: {usagePercent:F1}% used",
                    _ => $"Budget at {usagePercent:F1}%"
                }
            };
        }).ToList();
    }

    // DTOs
    private sealed class BudgetInfoDto
    {
        public decimal current_usage { get; set; }
        public decimal budget_ceiling { get; set; }
        public DateTimeOffset cycle_start { get; set; }
        public DateTimeOffset cycle_end { get; set; }
        public string plan { get; set; } = "";
        public decimal hard_limit_multiplier { get; set; }
    }

    private sealed class EmergencyCutoffDto
    {
        public DateTimeOffset? ends_at { get; set; }
        public string reason { get; set; } = "";
    }

    private sealed class TenantBudgetAlertDto
    {
        public Guid tenant_id { get; set; }
        public string tenant_name { get; set; } = "";
        public decimal current_usage { get; set; }
        public decimal budget_ceiling { get; set; }
    }
}
