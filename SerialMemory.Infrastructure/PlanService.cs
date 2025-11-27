using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for managing tenant plans and subscriptions.
/// </summary>
public sealed class PlanService : IPlanService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PlanService> _logger;

    public PlanService(string connectionString, ILogger<PlanService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public PlanService(NpgsqlDataSource dataSource, ILogger<PlanService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TenantPlan>> GetAvailablePlansAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var plans = await conn.QueryAsync<TenantPlanDto>(
            """
            SELECT id, plan_name, display_name, credits_per_cycle, cycle_days,
                                 max_memories, max_entities, is_active, metadata,
                                 rate_limit_per_minute, credit_carryover_enabled,
                                 credit_carryover_max_percent, created_at, updated_at
                          FROM tenant_plans
                          WHERE is_active = TRUE
                          ORDER BY credits_per_cycle
            """);

        return plans.Select(MapToPlan).ToList();
    }

    public async Task<TenantPlan?> GetPlanByNameAsync(
        string planName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var plan = await conn.QueryFirstOrDefaultAsync<TenantPlanDto>(
            """
            SELECT id, plan_name, display_name, credits_per_cycle, cycle_days,
                                 max_memories, max_entities, is_active, metadata,
                                 rate_limit_per_minute, credit_carryover_enabled,
                                 credit_carryover_max_percent, created_at, updated_at
                          FROM tenant_plans
                          WHERE plan_name = @PlanName
            """,
            new { PlanName = planName });

        return plan != null ? MapToPlan(plan) : null;
    }

    public async Task<TenantSubscription?> GetSubscriptionAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sub = await conn.QueryFirstOrDefaultAsync<SubscriptionDto>(
            """
            SELECT ts.id, ts.tenant_id, ts.workspace_id, ts.plan_id,
                                 tp.plan_name, ts.status, ts.started_at, ts.cancelled_at,
                                 ts.effective_end_date, pp.plan_name AS pending_plan_name,
                                 ts.pending_change_date
                          FROM tenant_subscriptions ts
                          JOIN tenant_plans tp ON ts.plan_id = tp.id
                          LEFT JOIN tenant_plans pp ON ts.pending_plan_id = pp.id
                          WHERE ts.tenant_id = @TenantId
                            AND ts.workspace_id = @WorkspaceId
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        if (sub == null) return null;

        // Get current billing cycle
        var cycle = await conn.QueryFirstOrDefaultAsync<BillingCycleDto>(
            """
            SELECT id, tenant_id, workspace_id, plan_id, cycle_start, cycle_end,
                                 credits_allocated, credits_used, is_current, closed_at, created_at
                          FROM billing_cycles
                          WHERE tenant_id = @TenantId
                            AND workspace_id = @WorkspaceId
                            AND is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        return new TenantSubscription
        {
            Id = sub.id,
            TenantId = sub.tenant_id,
            WorkspaceId = sub.workspace_id,
            PlanId = sub.plan_id,
            PlanName = sub.plan_name,
            Status = ParseStatus(sub.status),
            StartedAt = sub.started_at,
            CancelledAt = sub.cancelled_at,
            EffectiveEndDate = sub.effective_end_date,
            PendingPlanChange = sub.pending_plan_name,
            PendingChangeDate = sub.pending_change_date,
            CurrentCycle = cycle != null ? MapToCycle(cycle) : null
        };
    }

    public async Task<TenantSubscription> SubscribeAsync(
        string tenantId,
        string workspaceId,
        string planName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Get plan
            var plan = await conn.QueryFirstOrDefaultAsync<TenantPlanDto>(
                "SELECT * FROM tenant_plans WHERE plan_name = @PlanName AND is_active = TRUE",
                new { PlanName = planName },
                tx);

            if (plan == null)
            {
                throw new ArgumentException($"Plan '{planName}' not found or inactive");
            }

            // Check for existing subscription
            var existingSub = await conn.QueryFirstOrDefaultAsync<SubscriptionDto>(
                "SELECT * FROM tenant_subscriptions WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId",
                new { TenantId = tenantId, WorkspaceId = workspaceId },
                tx);

            Guid subId;
            if (existingSub != null)
            {
                // Update existing subscription
                await conn.ExecuteAsync(
                    """
                    UPDATE tenant_subscriptions
                                          SET plan_id = @PlanId, status = 'active', cancelled_at = NULL,
                                              effective_end_date = NULL, pending_plan_id = NULL,
                                              pending_change_date = NULL, updated_at = NOW()
                                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                    """,
                    new { PlanId = plan.id, TenantId = tenantId, WorkspaceId = workspaceId },
                    tx);
                subId = existingSub.id;
            }
            else
            {
                // Create new subscription
                subId = await conn.QueryFirstAsync<Guid>(
                    """
                    INSERT INTO tenant_subscriptions (tenant_id, workspace_id, plan_id, status)
                                          VALUES (@TenantId, @WorkspaceId, @PlanId, 'active')
                                          RETURNING id
                    """,
                    new { TenantId = tenantId, WorkspaceId = workspaceId, PlanId = plan.id },
                    tx);
            }

            // Close any existing billing cycles
            await conn.ExecuteAsync(
                """
                UPDATE billing_cycles
                                  SET is_current = FALSE, closed_at = NOW()
                                  WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId AND is_current = TRUE
                """,
                new { TenantId = tenantId, WorkspaceId = workspaceId },
                tx);

            // Create new billing cycle
            var cycleId = await conn.QueryFirstAsync<Guid>(
                """
                INSERT INTO billing_cycles (tenant_id, workspace_id, plan_id, cycle_start, cycle_end, credits_allocated)
                                  VALUES (@TenantId, @WorkspaceId, @PlanId, NOW(), NOW() + (@CycleDays || ' days')::INTERVAL, @Credits)
                                  RETURNING id
                """,
                new
                {
                    TenantId = tenantId,
                    WorkspaceId = workspaceId,
                    PlanId = plan.id,
                    CycleDays = plan.cycle_days,
                    Credits = plan.credits_per_cycle
                },
                tx);

            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Tenant {TenantId} subscribed to plan {PlanName}",
                tenantId, planName);

            return (await GetSubscriptionAsync(tenantId, workspaceId, cancellationToken))!;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PlanChangeResult> UpgradePlanAsync(
        string tenantId,
        string workspaceId,
        string newPlanName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Get current subscription
            var currentSub = await conn.QueryFirstOrDefaultAsync<SubscriptionDto>(
                """
                SELECT ts.*, tp.plan_name AS plan_name, tp.credits_per_cycle AS current_credits
                                  FROM tenant_subscriptions ts
                                  JOIN tenant_plans tp ON ts.plan_id = tp.id
                                  WHERE ts.tenant_id = @TenantId AND ts.workspace_id = @WorkspaceId
                """,
                new { TenantId = tenantId, WorkspaceId = workspaceId },
                tx);

            if (currentSub == null)
            {
                return new PlanChangeResult
                {
                    Success = false,
                    ErrorCode = "NO_SUBSCRIPTION",
                    ErrorMessage = "No active subscription found"
                };
            }

            // Get new plan
            var newPlan = await conn.QueryFirstOrDefaultAsync<TenantPlanDto>(
                "SELECT * FROM tenant_plans WHERE plan_name = @PlanName AND is_active = TRUE",
                new { PlanName = newPlanName },
                tx);

            if (newPlan == null)
            {
                return new PlanChangeResult
                {
                    Success = false,
                    ErrorCode = "PLAN_NOT_FOUND",
                    ErrorMessage = $"Plan '{newPlanName}' not found"
                };
            }

            // Verify this is an upgrade (new plan has more credits)
            if (newPlan.credits_per_cycle <= currentSub.current_credits)
            {
                return new PlanChangeResult
                {
                    Success = false,
                    ErrorCode = "NOT_AN_UPGRADE",
                    ErrorMessage = "New plan must have higher credit allocation for upgrade. Use downgrade instead."
                };
            }

            // Get current billing cycle
            var currentCycle = await conn.QueryFirstOrDefaultAsync<BillingCycleDto>(
                """
                SELECT * FROM billing_cycles
                                  WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId AND is_current = TRUE
                """,
                new { TenantId = tenantId, WorkspaceId = workspaceId },
                tx);

            // Calculate proration
            decimal creditAdjustment = 0;
            if (currentCycle != null)
            {
                var totalDays = (currentCycle.cycle_end - currentCycle.cycle_start).TotalDays;
                var remainingDays = (currentCycle.cycle_end - DateTimeOffset.UtcNow).TotalDays;
                var remainingRatio = remainingDays / totalDays;

                // Add prorated difference between plans
                var creditDifference = newPlan.credits_per_cycle - currentSub.current_credits;
                creditAdjustment = creditDifference * (decimal)remainingRatio;

                // Update current cycle with additional credits
                await conn.ExecuteAsync(
                    """
                    UPDATE billing_cycles
                                          SET credits_allocated = credits_allocated + @Adjustment, plan_id = @NewPlanId
                                          WHERE id = @CycleId
                    """,
                    new { Adjustment = creditAdjustment, NewPlanId = newPlan.id, CycleId = currentCycle.id },
                    tx);

                // Record credit adjustment
                await conn.ExecuteAsync(
                    """
                    INSERT INTO credit_adjustments (billing_cycle_id, adjustment_type, amount, reason)
                                          VALUES (@CycleId, 'upgrade_proration', @Amount, @Reason)
                    """,
                    new
                    {
                        CycleId = currentCycle.id,
                        Amount = creditAdjustment,
                        Reason = $"Pro-rated upgrade from {currentSub.plan_name} to {newPlanName}"
                    },
                    tx);
            }

            // Update subscription
            await conn.ExecuteAsync(
                """
                UPDATE tenant_subscriptions
                                  SET plan_id = @NewPlanId, updated_at = NOW()
                                  WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                """,
                new { NewPlanId = newPlan.id, TenantId = tenantId, WorkspaceId = workspaceId },
                tx);

            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Tenant {TenantId} upgraded from {OldPlan} to {NewPlan}. Credit adjustment: {Adjustment}",
                tenantId, currentSub.plan_name, newPlanName, creditAdjustment);

            return new PlanChangeResult
            {
                Success = true,
                OldPlanName = currentSub.plan_name,
                NewPlanName = newPlanName,
                EffectiveDate = DateTimeOffset.UtcNow,
                CreditAdjustment = creditAdjustment,
                NewCycle = currentCycle != null ? await GetCurrentCycleAsync(conn, tenantId, workspaceId) : null
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to upgrade plan for tenant {TenantId}", tenantId);
            return new PlanChangeResult
            {
                Success = false,
                ErrorCode = "UPGRADE_FAILED",
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<PlanChangeResult> DowngradePlanAsync(
        string tenantId,
        string workspaceId,
        string newPlanName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Get current subscription
            var currentSub = await conn.QueryFirstOrDefaultAsync<SubscriptionDto>(
                """
                SELECT ts.*, tp.plan_name AS plan_name, tp.credits_per_cycle AS current_credits
                                  FROM tenant_subscriptions ts
                                  JOIN tenant_plans tp ON ts.plan_id = tp.id
                                  WHERE ts.tenant_id = @TenantId AND ts.workspace_id = @WorkspaceId
                """,
                new { TenantId = tenantId, WorkspaceId = workspaceId },
                tx);

            if (currentSub == null)
            {
                return new PlanChangeResult
                {
                    Success = false,
                    ErrorCode = "NO_SUBSCRIPTION",
                    ErrorMessage = "No active subscription found"
                };
            }

            // Get new plan
            var newPlan = await conn.QueryFirstOrDefaultAsync<TenantPlanDto>(
                "SELECT * FROM tenant_plans WHERE plan_name = @PlanName AND is_active = TRUE",
                new { PlanName = newPlanName },
                tx);

            if (newPlan == null)
            {
                return new PlanChangeResult
                {
                    Success = false,
                    ErrorCode = "PLAN_NOT_FOUND",
                    ErrorMessage = $"Plan '{newPlanName}' not found"
                };
            }

            // Verify this is a downgrade
            if (newPlan.credits_per_cycle >= currentSub.current_credits)
            {
                return new PlanChangeResult
                {
                    Success = false,
                    ErrorCode = "NOT_A_DOWNGRADE",
                    ErrorMessage = "New plan must have lower credit allocation for downgrade. Use upgrade instead."
                };
            }

            // Get current billing cycle end date
            var currentCycle = await conn.QueryFirstOrDefaultAsync<BillingCycleDto>(
                """
                SELECT * FROM billing_cycles
                                  WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId AND is_current = TRUE
                """,
                new { TenantId = tenantId, WorkspaceId = workspaceId },
                tx);

            var effectiveDate = currentCycle?.cycle_end ?? DateTimeOffset.UtcNow;

            // Schedule downgrade for end of current cycle
            await conn.ExecuteAsync(
                """
                UPDATE tenant_subscriptions
                                  SET status = 'pending_downgrade',
                                      pending_plan_id = @NewPlanId,
                                      pending_change_date = @EffectiveDate,
                                      updated_at = NOW()
                                  WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                """,
                new
                {
                    NewPlanId = newPlan.id,
                    EffectiveDate = effectiveDate,
                    TenantId = tenantId,
                    WorkspaceId = workspaceId
                },
                tx);

            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Tenant {TenantId} scheduled downgrade from {OldPlan} to {NewPlan} effective {Date}",
                tenantId, currentSub.plan_name, newPlanName, effectiveDate);

            return new PlanChangeResult
            {
                Success = true,
                OldPlanName = currentSub.plan_name,
                NewPlanName = newPlanName,
                EffectiveDate = effectiveDate
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to downgrade plan for tenant {TenantId}", tenantId);
            return new PlanChangeResult
            {
                Success = false,
                ErrorCode = "DOWNGRADE_FAILED",
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task CancelSubscriptionAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get current cycle end
        var cycleEnd = await conn.QueryFirstOrDefaultAsync<DateTimeOffset?>(
            """
            SELECT cycle_end FROM billing_cycles
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId AND is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        await conn.ExecuteAsync(
            """
            UPDATE tenant_subscriptions
                          SET status = 'cancelled',
                              cancelled_at = NOW(),
                              effective_end_date = @EffectiveEnd,
                              updated_at = NOW()
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
            """,
            new
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                EffectiveEnd = cycleEnd ?? DateTimeOffset.UtcNow
            });

        _logger.LogInformation("Tenant {TenantId} cancelled subscription", tenantId);
    }

    private async Task<BillingCycle?> GetCurrentCycleAsync(
        NpgsqlConnection conn,
        string tenantId,
        string workspaceId)
    {
        var dto = await conn.QueryFirstOrDefaultAsync<BillingCycleDto>(
            """
            SELECT * FROM billing_cycles
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId AND is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        return dto != null ? MapToCycle(dto) : null;
    }

    private static TenantPlan MapToPlan(TenantPlanDto dto) => new()
    {
        Id = dto.id,
        PlanName = dto.plan_name,
        DisplayName = dto.display_name,
        CreditsPerCycle = dto.credits_per_cycle,
        CycleDays = dto.cycle_days,
        MaxMemories = dto.max_memories,
        MaxEntities = dto.max_entities,
        IsActive = dto.is_active,
        CreatedAt = dto.created_at,
        UpdatedAt = dto.updated_at
    };

    private static BillingCycle MapToCycle(BillingCycleDto dto) => new()
    {
        Id = dto.id,
        TenantId = dto.tenant_id,
        WorkspaceId = dto.workspace_id,
        PlanId = dto.plan_id,
        CycleStart = dto.cycle_start,
        CycleEnd = dto.cycle_end,
        CreditsAllocated = dto.credits_allocated,
        CreditsUsed = dto.credits_used,
        IsCurrent = dto.is_current,
        ClosedAt = dto.closed_at,
        CreatedAt = dto.created_at
    };

    private static SubscriptionStatus ParseStatus(string status) => status switch
    {
        "active" => SubscriptionStatus.Active,
        "past_due" => SubscriptionStatus.PastDue,
        "cancelled" => SubscriptionStatus.Cancelled,
        "suspended" => SubscriptionStatus.Suspended,
        "pending_downgrade" => SubscriptionStatus.PendingDowngrade,
        _ => SubscriptionStatus.Active
    };

    private sealed class TenantPlanDto
    {
        public Guid id { get; set; }
        public string plan_name { get; set; } = "";
        public string display_name { get; set; } = "";
        public decimal credits_per_cycle { get; set; }
        public int cycle_days { get; set; }
        public int? max_memories { get; set; }
        public int? max_entities { get; set; }
        public bool is_active { get; set; }
        public string? metadata { get; set; }
        public int? rate_limit_per_minute { get; set; }
        public bool credit_carryover_enabled { get; set; }
        public decimal? credit_carryover_max_percent { get; set; }
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }
    }

    private sealed class SubscriptionDto
    {
        public Guid id { get; set; }
        public string tenant_id { get; set; } = "";
        public string workspace_id { get; set; } = "";
        public Guid plan_id { get; set; }
        public string plan_name { get; set; } = "";
        public string status { get; set; } = "";
        public DateTimeOffset started_at { get; set; }
        public DateTimeOffset? cancelled_at { get; set; }
        public DateTimeOffset? effective_end_date { get; set; }
        public string? pending_plan_name { get; set; }
        public DateTimeOffset? pending_change_date { get; set; }
        public decimal current_credits { get; set; }
    }

    private sealed class BillingCycleDto
    {
        public Guid id { get; set; }
        public string tenant_id { get; set; } = "";
        public string workspace_id { get; set; } = "";
        public Guid? plan_id { get; set; }
        public DateTimeOffset cycle_start { get; set; }
        public DateTimeOffset cycle_end { get; set; }
        public decimal credits_allocated { get; set; }
        public decimal credits_used { get; set; }
        public bool is_current { get; set; }
        public DateTimeOffset? closed_at { get; set; }
        public DateTimeOffset created_at { get; set; }
    }
}
