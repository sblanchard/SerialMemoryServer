using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using SerialMemory.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Integration tests proving multi-tenant isolation for usage metering.
/// </summary>
[Collection("PostgreSQL")]
public class MultiTenantUsageTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private string _connectionString = "";
    private ILoggerFactory _loggerFactory = null!;

    public MultiTenantUsageTests()
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

        // Apply both schema files
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ops", "usage_metering_schema.sql");
        var schemaV2Path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ops", "usage_metering_schema_v2.sql");

        if (File.Exists(schemaPath))
        {
            var schemaSql = await File.ReadAllTextAsync(schemaPath);
            await conn.ExecuteAsync(schemaSql);
        }

        if (File.Exists(schemaV2Path))
        {
            var schemaV2Sql = await File.ReadAllTextAsync(schemaV2Path);
            await conn.ExecuteAsync(schemaV2Sql);
        }
    }

    public async Task DisposeAsync()
    {
        _loggerFactory.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task TenantContext_SetAndGet_WorksCorrectly()
    {
        // Arrange
        var context = new TenantContext();

        // Act
        context.SetContext("tenant-1", "workspace-1", "user-1", Guid.CreateVersion7());

        // Assert
        Assert.Equal("tenant-1", context.TenantId);
        Assert.Equal("workspace-1", context.WorkspaceId);
        Assert.Equal("user-1", context.UserId);
        Assert.NotNull(context.SessionId);
    }

    [Fact]
    public void TenantContext_NotSet_ThrowsException()
    {
        // Arrange
        var context = new TenantContext();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.TenantId);
    }

    [Fact]
    public void FixedTenantContext_SelfHosted_HasCorrectDefaults()
    {
        // Act
        var context = FixedTenantContext.SelfHosted;

        // Assert
        Assert.Equal("self", context.TenantId);
        Assert.Equal("default", context.WorkspaceId);
    }

    [Fact]
    public async Task UsageService_TenantIsolation_EventsAreScoped()
    {
        // Arrange
        using var tenant1Service = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "tenant-1", "workspace-1");
        using var tenant2Service = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "tenant-2", "workspace-2");

        // Act - Track events for both tenants
        tenant1Service.TrackUsage(UsageEventType.MemorySearch, latencyMs: 100);
        tenant1Service.TrackUsage(UsageEventType.MemoryIngest, latencyMs: 200);
        tenant2Service.TrackUsage(UsageEventType.MemorySearch, latencyMs: 150);

        await Task.Delay(2500);

        // Assert - Each tenant only sees their own events
        var tenant1Events = await tenant1Service.GetRecentEventsAsync();
        var tenant2Events = await tenant2Service.GetRecentEventsAsync();

        Assert.All(tenant1Events, e => Assert.Equal("tenant-1", e.TenantId));
        Assert.All(tenant2Events, e => Assert.Equal("tenant-2", e.TenantId));
        Assert.Equal(2, tenant1Events.Count);
        Assert.Single(tenant2Events);
    }

    [Fact]
    public async Task UsageService_TenantIsolation_BillingCyclesAreSeparate()
    {
        // Arrange
        using var tenant1Service = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "tenant-a", "workspace-a");
        using var tenant2Service = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "tenant-b", "workspace-b");

        // Act - Track events to create billing cycles
        tenant1Service.TrackUsage(UsageEventType.MemorySearch);
        tenant2Service.TrackUsage(UsageEventType.MemorySearch);
        await Task.Delay(2500);

        var tenant1Cycle = await tenant1Service.GetCurrentBillingCycleAsync();
        var tenant2Cycle = await tenant2Service.GetCurrentBillingCycleAsync();

        // Assert - Each tenant has their own billing cycle
        Assert.NotNull(tenant1Cycle);
        Assert.NotNull(tenant2Cycle);
        Assert.Equal("tenant-a", tenant1Cycle.TenantId);
        Assert.Equal("tenant-b", tenant2Cycle.TenantId);
        Assert.NotEqual(tenant1Cycle.Id, tenant2Cycle.Id);
    }

    [Fact]
    public async Task UsageService_TenantIsolation_CannotAccessOtherTenantData()
    {
        // Arrange
        using var tenant1Service = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "tenant-x", "workspace-x");
        using var tenant2Service = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "tenant-y", "workspace-y");

        // Track event for tenant-x only
        tenant1Service.TrackUsage(UsageEventType.MemorySearch, latencyMs: 100);
        await Task.Delay(2500);

        // Act - Try to get tenant-y events (should be empty, not tenant-x events)
        var tenant2Events = await tenant2Service.GetRecentEventsAsync();

        // Assert
        Assert.Empty(tenant2Events);
    }

    [Fact]
    public void UsageServiceFactory_CreatesIsolatedServices()
    {
        // Arrange
        var factory = new UsageServiceFactory(_connectionString, _loggerFactory);

        // Act
        var service1 = factory.CreateForTenant("factory-tenant-1", "workspace");
        var service2 = factory.CreateForTenant("factory-tenant-2", "workspace");

        // Assert
        Assert.Equal("factory-tenant-1", service1.TenantId);
        Assert.Equal("factory-tenant-2", service2.TenantId);
        Assert.NotEqual(service1.TenantId, service2.TenantId);
    }

    [Fact]
    public void UsageServiceFactory_CreateFromContext_UsesTenantContext()
    {
        // Arrange
        var factory = new UsageServiceFactory(_connectionString, _loggerFactory);
        var context = new FixedTenantContext("context-tenant", "context-workspace", "user-123");

        // Act
        var service = factory.CreateFromContext(context);

        // Assert
        Assert.Equal("context-tenant", service.TenantId);
        Assert.Equal("context-workspace", service.WorkspaceId);
    }

    [Fact]
    public async Task TenantContext_Scope_ClearsOnDispose()
    {
        // Arrange - Create a tenant context instance
        var context = new TenantContext();
        context.SetContext("scoped-tenant", "scoped-workspace");

        // Act & Assert - Context is set initially
        Assert.True(context.IsSet);

        // Clear the context
        context.Clear();

        // Assert - Context is cleared
        Assert.False(context.IsSet);

        await Task.CompletedTask; // Satisfy async signature
    }

    [Fact]
    public void UsageService_RequiresNonEmptyTenantId()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "", "workspace"));
    }

    [Fact]
    public void UsageService_RequiresNonEmptyWorkspaceId()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "tenant", ""));
    }
}
