using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Integration tests for plan management service.
/// </summary>
[Collection("PostgreSQL")]
public class PlanServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private string _connectionString = "";
    private ILoggerFactory _loggerFactory = null!;

    public PlanServiceTests()
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
    public async Task GetAvailablePlans_ReturnsActivePlans()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());

        // Act
        var plans = await planService.GetAvailablePlansAsync();

        // Assert
        Assert.NotEmpty(plans);
        Assert.All(plans, p => Assert.True(p.IsActive));
    }

    [Fact]
    public async Task GetPlanByName_ExistingPlan_ReturnsPlan()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());

        // Act
        var plan = await planService.GetPlanByNameAsync("free");

        // Assert
        Assert.NotNull(plan);
        Assert.Equal("free", plan.PlanName);
        Assert.Equal("Free Tier", plan.DisplayName);
    }

    [Fact]
    public async Task GetPlanByName_NonExistentPlan_ReturnsNull()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());

        // Act
        var plan = await planService.GetPlanByNameAsync("non-existent-plan");

        // Assert
        Assert.Null(plan);
    }

    [Fact]
    public async Task Subscribe_NewTenant_CreatesSubscriptionAndBillingCycle()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());
        var tenantId = $"sub-test-{Guid.CreateVersion7()}";

        // Act
        var subscription = await planService.SubscribeAsync(tenantId, "workspace", "free");

        // Assert
        Assert.NotNull(subscription);
        Assert.Equal(tenantId, subscription.TenantId);
        Assert.Equal("free", subscription.PlanName);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.NotNull(subscription.CurrentCycle);
    }

    [Fact]
    public async Task GetSubscription_ExistingSubscription_ReturnsDetails()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());
        var tenantId = $"get-sub-test-{Guid.CreateVersion7()}";
        await planService.SubscribeAsync(tenantId, "workspace", "free");

        // Act
        var subscription = await planService.GetSubscriptionAsync(tenantId, "workspace");

        // Assert
        Assert.NotNull(subscription);
        Assert.Equal(tenantId, subscription.TenantId);
        Assert.Equal("free", subscription.PlanName);
    }

    [Fact]
    public async Task UpgradePlan_ValidUpgrade_AddsProRatedCredits()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());
        var tenantId = $"upgrade-test-{Guid.CreateVersion7()}";

        // Subscribe to free plan first
        await planService.SubscribeAsync(tenantId, "workspace", "free");

        // Act - Upgrade to pro
        var result = await planService.UpgradePlanAsync(tenantId, "workspace", "pro");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("free", result.OldPlanName);
        Assert.Equal("pro", result.NewPlanName);
        Assert.NotNull(result.CreditAdjustment);
        Assert.True(result.CreditAdjustment >= 0);

        // Verify subscription was updated
        var subscription = await planService.GetSubscriptionAsync(tenantId, "workspace");
        Assert.Equal("pro", subscription?.PlanName);
    }

    [Fact]
    public async Task UpgradePlan_NotAnUpgrade_ReturnsError()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());
        var tenantId = $"upgrade-fail-test-{Guid.CreateVersion7()}";

        // Subscribe to pro plan
        await planService.SubscribeAsync(tenantId, "workspace", "pro");

        // Act - Try to "upgrade" to free (which is actually a downgrade)
        var result = await planService.UpgradePlanAsync(tenantId, "workspace", "free");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("NOT_AN_UPGRADE", result.ErrorCode);
    }

    [Fact]
    public async Task DowngradePlan_ValidDowngrade_SchedulesForEndOfCycle()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());
        var tenantId = $"downgrade-test-{Guid.CreateVersion7()}";

        // Subscribe to pro plan first
        await planService.SubscribeAsync(tenantId, "workspace", "pro");

        // Act - Downgrade to free
        var result = await planService.DowngradePlanAsync(tenantId, "workspace", "free");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("pro", result.OldPlanName);
        Assert.Equal("free", result.NewPlanName);
        Assert.True(result.EffectiveDate > DateTimeOffset.UtcNow);

        // Verify subscription shows pending downgrade
        var subscription = await planService.GetSubscriptionAsync(tenantId, "workspace");
        Assert.Equal(SubscriptionStatus.PendingDowngrade, subscription?.Status);
        Assert.Equal("free", subscription?.PendingPlanChange);
    }

    [Fact]
    public async Task DowngradePlan_NotADowngrade_ReturnsError()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());
        var tenantId = $"downgrade-fail-test-{Guid.CreateVersion7()}";

        // Subscribe to free plan
        await planService.SubscribeAsync(tenantId, "workspace", "free");

        // Act - Try to "downgrade" to pro (which is actually an upgrade)
        var result = await planService.DowngradePlanAsync(tenantId, "workspace", "pro");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("NOT_A_DOWNGRADE", result.ErrorCode);
    }

    [Fact]
    public async Task CancelSubscription_SetsStatusToCancelled()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());
        var tenantId = $"cancel-test-{Guid.CreateVersion7()}";

        await planService.SubscribeAsync(tenantId, "workspace", "free");

        // Act
        await planService.CancelSubscriptionAsync(tenantId, "workspace");

        // Assert
        var subscription = await planService.GetSubscriptionAsync(tenantId, "workspace");
        Assert.Equal(SubscriptionStatus.Cancelled, subscription?.Status);
        Assert.NotNull(subscription?.CancelledAt);
    }

    [Fact]
    public async Task Subscribe_ToNonExistentPlan_ThrowsException()
    {
        // Arrange
        var planService = new PlanService(_connectionString, _loggerFactory.CreateLogger<PlanService>());
        var tenantId = $"bad-plan-test-{Guid.CreateVersion7()}";

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            planService.SubscribeAsync(tenantId, "workspace", "non-existent-plan"));
    }
}
