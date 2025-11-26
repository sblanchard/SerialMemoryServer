using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Integration tests for audit logging with hash chains.
/// </summary>
[Collection("PostgreSQL")]
public class AuditLogTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private string _connectionString = "";
    private ILoggerFactory _loggerFactory = null!;

    public AuditLogTests()
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
    public async Task AppendAsync_CreatesEntryWithHash()
    {
        // Arrange
        var auditService = new AuditLogService(_connectionString, _loggerFactory.CreateLogger<AuditLogService>());

        // Act
        var entry = await auditService.AppendAsync(
            "audit-tenant-1",
            "workspace",
            AuditEventType.UsageEventRecorded,
            "Test usage event recorded",
            new Dictionary<string, object> { ["event_type"] = "memory_search" });

        // Assert
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal("audit-tenant-1", entry.TenantId);
        Assert.Equal("workspace", entry.WorkspaceId);
        Assert.Equal(AuditEventType.UsageEventRecorded, entry.EventType);
        Assert.NotEmpty(entry.ContentHash);
    }

    [Fact]
    public async Task AppendAsync_BuildsHashChain()
    {
        // Arrange
        var auditService = new AuditLogService(_connectionString, _loggerFactory.CreateLogger<AuditLogService>());
        var tenantId = $"chain-tenant-{Guid.CreateVersion7()}";

        // Act - Append multiple entries
        var entry1 = await auditService.AppendAsync(tenantId, "workspace", AuditEventType.TenantCreated, "Tenant created");
        var entry2 = await auditService.AppendAsync(tenantId, "workspace", AuditEventType.PlanSubscribed, "Subscribed to free");
        var entry3 = await auditService.AppendAsync(tenantId, "workspace", AuditEventType.UsageEventRecorded, "First usage");

        // Assert - Verify chain
        Assert.Equal("", entry1.PreviousHash); // First entry has no previous
        Assert.Equal(entry1.ContentHash, entry2.PreviousHash);
        Assert.Equal(entry2.ContentHash, entry3.PreviousHash);
    }

    [Fact]
    public async Task VerifyIntegrity_ValidChain_ReturnsValid()
    {
        // Arrange
        var auditService = new AuditLogService(_connectionString, _loggerFactory.CreateLogger<AuditLogService>());
        using var usageService = new UsageService(_connectionString, _loggerFactory.CreateLogger<UsageService>(), "verify-tenant", "workspace");

        // Create billing cycle first
        usageService.TrackUsage(Core.Models.UsageEventType.MemorySearch);
        await Task.Delay(2500);

        var cycle = await usageService.GetCurrentBillingCycleAsync();
        Assert.NotNull(cycle);

        // Create audit entries linked to the cycle
        await auditService.AppendAsync("verify-tenant", "workspace", AuditEventType.BillingCycleCreated, "Cycle created");
        await auditService.AppendAsync("verify-tenant", "workspace", AuditEventType.UsageEventRecorded, "Event 1");
        await auditService.AppendAsync("verify-tenant", "workspace", AuditEventType.UsageEventRecorded, "Event 2");

        // Act
        var result = await auditService.VerifyIntegrityAsync(cycle.Id);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(result.TotalEntries, result.VerifiedEntries);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task GetEntriesByDateRange_ReturnsFilteredEntries()
    {
        // Arrange
        var auditService = new AuditLogService(_connectionString, _loggerFactory.CreateLogger<AuditLogService>());
        var tenantId = $"date-range-tenant-{Guid.CreateVersion7()}";

        await auditService.AppendAsync(tenantId, "workspace", AuditEventType.TenantCreated, "Entry 1");
        await auditService.AppendAsync(tenantId, "workspace", AuditEventType.PlanSubscribed, "Entry 2");

        // Act
        var entries = await auditService.GetEntriesByDateRangeAsync(
            tenantId,
            "workspace",
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(1));

        // Assert
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(tenantId, e.TenantId));
    }

    [Fact]
    public async Task ComputeContentHash_ProducesConsistentHash()
    {
        // Arrange
        var tenantId = "hash-test";
        var workspaceId = "workspace";
        var eventType = "UsageEventRecorded";
        var description = "Test event";
        var dataJson = "{\"key\":\"value\"}";
        var timestamp = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var previousHash = "abc123";

        // Act
        var hash1 = AuditLogService.ComputeContentHash(tenantId, workspaceId, eventType, description, dataJson, timestamp, previousHash);
        var hash2 = AuditLogService.ComputeContentHash(tenantId, workspaceId, eventType, description, dataJson, timestamp, previousHash);

        // Assert - Same inputs produce same hash
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 produces 64 hex chars
    }

    [Fact]
    public async Task ComputeContentHash_DifferentInputsProduceDifferentHashes()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var hash1 = AuditLogService.ComputeContentHash("tenant1", "workspace", "Event1", "Desc1", null, timestamp, "");
        var hash2 = AuditLogService.ComputeContentHash("tenant2", "workspace", "Event1", "Desc1", null, timestamp, "");
        var hash3 = AuditLogService.ComputeContentHash("tenant1", "workspace", "Event2", "Desc1", null, timestamp, "");

        // Assert
        Assert.NotEqual(hash1, hash2);
        Assert.NotEqual(hash1, hash3);
        Assert.NotEqual(hash2, hash3);
    }

    [Fact]
    public async Task AppendAsync_WithData_IncludesDataInEntry()
    {
        // Arrange
        var auditService = new AuditLogService(_connectionString, _loggerFactory.CreateLogger<AuditLogService>());
        var data = new Dictionary<string, object>
        {
            ["credits_used"] = 10.5,
            ["event_count"] = 100,
            ["plan"] = "pro"
        };

        // Act
        var entry = await auditService.AppendAsync(
            "data-tenant",
            "workspace",
            AuditEventType.BillingCycleClosed,
            "Cycle closed with usage",
            data);

        // Assert
        Assert.NotNull(entry.Data);
        Assert.True(entry.Data.ContainsKey("plan"));
    }

    [Fact]
    public async Task AppendAsync_SequenceNumbers_AreMonotonic()
    {
        // Arrange
        var auditService = new AuditLogService(_connectionString, _loggerFactory.CreateLogger<AuditLogService>());
        var tenantId = $"sequence-tenant-{Guid.CreateVersion7()}";

        // Act
        var entry1 = await auditService.AppendAsync(tenantId, "workspace", AuditEventType.TenantCreated, "Entry 1");
        var entry2 = await auditService.AppendAsync(tenantId, "workspace", AuditEventType.PlanSubscribed, "Entry 2");
        var entry3 = await auditService.AppendAsync(tenantId, "workspace", AuditEventType.UsageEventRecorded, "Entry 3");

        // Assert
        Assert.True(entry2.SequenceNumber > entry1.SequenceNumber);
        Assert.True(entry3.SequenceNumber > entry2.SequenceNumber);
    }

    [Fact]
    public async Task VerifyIntegrity_NonExistentCycle_ReturnsInvalid()
    {
        // Arrange
        var auditService = new AuditLogService(_connectionString, _loggerFactory.CreateLogger<AuditLogService>());

        // Act
        var result = await auditService.VerifyIntegrityAsync(Guid.CreateVersion7());

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public async Task AuditLog_TenantIsolation_EntriesAreScopedToTenant()
    {
        // Arrange
        var auditService = new AuditLogService(_connectionString, _loggerFactory.CreateLogger<AuditLogService>());
        var tenant1 = $"iso-tenant-1-{Guid.CreateVersion7()}";
        var tenant2 = $"iso-tenant-2-{Guid.CreateVersion7()}";

        await auditService.AppendAsync(tenant1, "workspace", AuditEventType.TenantCreated, "Tenant 1 created");
        await auditService.AppendAsync(tenant1, "workspace", AuditEventType.PlanSubscribed, "Tenant 1 subscribed");
        await auditService.AppendAsync(tenant2, "workspace", AuditEventType.TenantCreated, "Tenant 2 created");

        // Act
        var tenant1Entries = await auditService.GetEntriesByDateRangeAsync(
            tenant1, "workspace", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        var tenant2Entries = await auditService.GetEntriesByDateRangeAsync(
            tenant2, "workspace", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));

        // Assert
        Assert.Equal(2, tenant1Entries.Count);
        Assert.Single(tenant2Entries);
        Assert.All(tenant1Entries, e => Assert.Equal(tenant1, e.TenantId));
        Assert.All(tenant2Entries, e => Assert.Equal(tenant2, e.TenantId));
    }
}
