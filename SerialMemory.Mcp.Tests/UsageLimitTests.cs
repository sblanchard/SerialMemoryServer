using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Exceptions;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Integration tests for usage limit enforcement.
/// </summary>
[Collection("PostgreSQL")]
public class UsageLimitTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private string _connectionString = "";
    private ILoggerFactory _loggerFactory = null!;

    public UsageLimitTests()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16")
            .WithDatabase("testdb")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
        _loggerFactory = LoggerFactory.Create(b => b.AddConsole());

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ops", "usage_metering_schema.sql");
        var schemaV2Path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ops", "usage_metering_schema_v2.sql");

        if (File.Exists(schemaPath))
        {
            await conn.ExecuteAsync(await File.ReadAllTextAsync(schemaPath));
        }

        if (File.Exists(schemaV2Path))
        {
            await conn.ExecuteAsync(await File.ReadAllTextAsync(schemaV2Path));
        }
    }

    public async Task DisposeAsync()
    {
        _loggerFactory.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task CheckLimits_WithSufficientCredits_ReturnsAllowed()
    {
        // Arrange
        var limitService = new UsageLimitService(_connectionString, _loggerFactory.CreateLogger<UsageLimitService>());

        // Act
        var result = await limitService.CheckLimitsAsync("limit-test-1", "workspace", UsageEventType.MemorySearch);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Null(result.Violation);
    }

    [Fact]
    public async Task CheckLimits_CreditLimitExceeded_ReturnsViolation()
    {
        // Arrange
        var limitService = new UsageLimitService(_connectionString, _loggerFactory.CreateLogger<UsageLimitService>());
        await using var conn = new NpgsqlConnection(_connectionString);

        // Create a low-credit plan and subscription
        var planId = await conn.QueryFirstAsync<Guid>(
            @"INSERT INTO tenant_plans (plan_name, display_name, credits_per_cycle, cycle_days)
              VALUES ('test_low', 'Test Low', 0.5, 30)
              ON CONFLICT (plan_name) DO UPDATE SET credits_per_cycle = 0.5
              RETURNING id");

        await conn.ExecuteAsync(
            @"INSERT INTO tenant_subscriptions (tenant_id, workspace_id, plan_id, status)
              VALUES ('credit-limit-tenant', 'workspace', @PlanId, 'active')
              ON CONFLICT (tenant_id, workspace_id) DO UPDATE SET plan_id = @PlanId",
            new { PlanId = planId });

        // Create billing cycle with almost depleted credits
        await conn.ExecuteAsync(
            @"INSERT INTO billing_cycles (tenant_id, workspace_id, plan_id, cycle_start, cycle_end, credits_allocated, credits_used, is_current)
              VALUES ('credit-limit-tenant', 'workspace', @PlanId, NOW(), NOW() + INTERVAL '30 days', 0.5, 0.4, TRUE)
              ON CONFLICT DO NOTHING",
            new { PlanId = planId });

        // Act - Try an operation that costs 1.0 credits (MemoryIngest)
        var result = await limitService.CheckLimitsAsync("credit-limit-tenant", "workspace", UsageEventType.MemoryIngest);

        // Assert
        Assert.False(result.IsAllowed);
        Assert.NotNull(result.Violation);
        Assert.Equal(UsageLimitType.CreditLimitExceeded, result.Violation.LimitType);
        Assert.Equal("CREDIT_LIMIT_EXCEEDED", result.Violation.Code);
    }

    [Fact]
    public async Task CheckLimits_RateLimitExceeded_ReturnsViolation()
    {
        // Arrange
        var limitService = new UsageLimitService(_connectionString, _loggerFactory.CreateLogger<UsageLimitService>());
        await using var conn = new NpgsqlConnection(_connectionString);

        // Create a plan with low rate limit
        var planId = await conn.QueryFirstAsync<Guid>(
            @"INSERT INTO tenant_plans (plan_name, display_name, credits_per_cycle, cycle_days, rate_limit_per_minute)
              VALUES ('test_ratelimit', 'Test Rate Limit', 1000, 30, 2)
              ON CONFLICT (plan_name) DO UPDATE SET rate_limit_per_minute = 2
              RETURNING id");

        await conn.ExecuteAsync(
            @"INSERT INTO tenant_subscriptions (tenant_id, workspace_id, plan_id, status)
              VALUES ('rate-limit-tenant', 'workspace', @PlanId, 'active')
              ON CONFLICT (tenant_id, workspace_id) DO UPDATE SET plan_id = @PlanId",
            new { PlanId = planId });

        // Create billing cycle
        await conn.ExecuteAsync(
            @"INSERT INTO billing_cycles (tenant_id, workspace_id, plan_id, cycle_start, cycle_end, credits_allocated, is_current)
              VALUES ('rate-limit-tenant', 'workspace', @PlanId, NOW(), NOW() + INTERVAL '30 days', 1000, TRUE)
              ON CONFLICT DO NOTHING",
            new { PlanId = planId });

        // Record rate limit hits to exceed limit
        await limitService.RecordRateLimitHitAsync("rate-limit-tenant", "workspace");
        await limitService.RecordRateLimitHitAsync("rate-limit-tenant", "workspace");
        await limitService.RecordRateLimitHitAsync("rate-limit-tenant", "workspace");

        // Act
        var result = await limitService.CheckLimitsAsync("rate-limit-tenant", "workspace", UsageEventType.MemorySearch);

        // Assert
        Assert.False(result.IsAllowed);
        Assert.NotNull(result.Violation);
        Assert.Equal(UsageLimitType.RateLimitExceeded, result.Violation.LimitType);
        Assert.Equal("RATE_LIMIT_EXCEEDED", result.Violation.Code);
        Assert.NotNull(result.Violation.RetryAfter);
    }

    [Fact]
    public async Task GetUsageSummary_ReturnsCorrectData()
    {
        // Arrange
        var limitService = new UsageLimitService(_connectionString, _loggerFactory.CreateLogger<UsageLimitService>());
        using var usageService = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "summary-tenant", "workspace");

        // Create some usage
        usageService.TrackUsage(UsageEventType.MemorySearch);
        usageService.TrackUsage(UsageEventType.MemoryIngest);
        await Task.Delay(2500);

        // Act
        var summary = await limitService.GetUsageSummaryAsync("summary-tenant", "workspace");

        // Assert
        Assert.Equal("summary-tenant", summary.TenantId);
        Assert.Equal("workspace", summary.WorkspaceId);
        Assert.True(summary.CreditsAllocated > 0);
    }

    [Fact]
    public void UsageLimitExceededException_CreditLimitExceeded_HasCorrectDetails()
    {
        // Act
        var exception = UsageLimitExceededException.CreditLimitExceeded(
            creditsRequired: 10m,
            creditsRemaining: 5m,
            cycleEnd: DateTimeOffset.UtcNow.AddDays(10));

        // Assert
        Assert.Equal(UsageLimitType.CreditLimitExceeded, exception.LimitType);
        Assert.Equal("CREDIT_LIMIT_EXCEEDED", exception.ErrorCode);
        Assert.NotNull(exception.Details);
        Assert.Equal(10m, exception.Details["credits_required"]);
        Assert.Equal(5m, exception.Details["credits_remaining"]);
    }

    [Fact]
    public void UsageLimitExceededException_RateLimitExceeded_HasCorrectDetails()
    {
        // Act
        var retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
        var exception = UsageLimitExceededException.RateLimitExceeded(
            limitPerMinute: 60,
            currentCount: 65,
            retryAfter: retryAfter);

        // Assert
        Assert.Equal(UsageLimitType.RateLimitExceeded, exception.LimitType);
        Assert.Equal("RATE_LIMIT_EXCEEDED", exception.ErrorCode);
        Assert.Equal(retryAfter, exception.RetryAfter);
    }

    [Fact]
    public void UsageLimitExceededException_ToErrorResponse_CreatesStructuredResponse()
    {
        // Arrange
        var exception = UsageLimitExceededException.CreditLimitExceeded(10m, 5m, DateTimeOffset.UtcNow.AddDays(5));

        // Act
        var response = exception.ToErrorResponse();

        // Assert
        Assert.Equal("CREDIT_LIMIT_EXCEEDED", response.Error);
        Assert.Equal("CreditLimitExceeded", response.LimitType);
        Assert.NotNull(response.Details);
    }

    [Fact]
    public async Task CheckLimits_InactivePlan_ReturnsViolation()
    {
        // Arrange
        var limitService = new UsageLimitService(_connectionString, _loggerFactory.CreateLogger<UsageLimitService>());
        await using var conn = new NpgsqlConnection(_connectionString);

        // Create an inactive plan
        var planId = await conn.QueryFirstAsync<Guid>(
            @"INSERT INTO tenant_plans (plan_name, display_name, credits_per_cycle, cycle_days, is_active)
              VALUES ('test_inactive', 'Inactive Plan', 1000, 30, FALSE)
              ON CONFLICT (plan_name) DO UPDATE SET is_active = FALSE
              RETURNING id");

        await conn.ExecuteAsync(
            @"INSERT INTO tenant_subscriptions (tenant_id, workspace_id, plan_id, status)
              VALUES ('inactive-plan-tenant', 'workspace', @PlanId, 'active')
              ON CONFLICT (tenant_id, workspace_id) DO UPDATE SET plan_id = @PlanId",
            new { PlanId = planId });

        // Create billing cycle
        await conn.ExecuteAsync(
            @"INSERT INTO billing_cycles (tenant_id, workspace_id, plan_id, cycle_start, cycle_end, credits_allocated, is_current)
              VALUES ('inactive-plan-tenant', 'workspace', @PlanId, NOW(), NOW() + INTERVAL '30 days', 1000, TRUE)
              ON CONFLICT DO NOTHING",
            new { PlanId = planId });

        // Act
        var result = await limitService.CheckLimitsAsync("inactive-plan-tenant", "workspace", UsageEventType.MemorySearch);

        // Assert
        Assert.False(result.IsAllowed);
        Assert.NotNull(result.Violation);
        Assert.Equal(UsageLimitType.PlanInactive, result.Violation.LimitType);
    }
}
