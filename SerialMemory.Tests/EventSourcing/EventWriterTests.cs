using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SerialMemory.EventSourcing.Store;
using Xunit;

namespace SerialMemory.Tests.EventSourcing;

/// <summary>
/// Integration tests for the EventWriter service.
/// Verifies that events are correctly written to memory_events and event_log tables.
/// </summary>
[Trait("Category", "Integration")]
public class EventWriterTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private NpgsqlDataSource _dataSource = null!;
    private EventWriter _eventWriter = null!;
    private Guid _testTenantId;

    public EventWriterTests()
    {
        _connectionString = Environment.GetEnvironmentVariable("TEST_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=contextdb_test;Username=postgres;Password=postgres";
        _testTenantId = Guid.CreateVersion7();
    }

    public async Task InitializeAsync()
    {
        _dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        _eventWriter = new EventWriter(
            _dataSource,
            NullLogger<EventWriter>.Instance);

        // Setup test tenant
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync($"""
            INSERT INTO tenants (id, name, slug, status)
            VALUES ('{_testTenantId}', 'Test Tenant', 'test-{_testTenantId:N}', 'active')
            ON CONFLICT (id) DO NOTHING
            """);
    }

    public async Task DisposeAsync()
    {
        // Cleanup test data
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        await conn.ExecuteAsync($"DELETE FROM event_log WHERE tenant_id = '{_testTenantId}'");
        await conn.ExecuteAsync($"DELETE FROM memory_events WHERE tenant_id = '{_testTenantId}'");
        await conn.ExecuteAsync($"DELETE FROM mind_health WHERE tenant_id = '{_testTenantId}'");
        await conn.ExecuteAsync($"DELETE FROM branch_metadata WHERE tenant_id = '{_testTenantId}'");

        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task EmitSystemEventAsync_WritesToEventLog()
    {
        // Arrange
        var payload = new { test = true, value = 42 };

        // Act
        var eventId = await _eventWriter.EmitSystemEventAsync(
            _testTenantId,
            "TestEvent",
            payload,
            "test_actor",
            "test",
            "info");

        // Assert
        eventId.Should().NotBeEmpty();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = _testTenantId.ToString() });

        var result = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM event_log WHERE id = @Id",
            new { Id = eventId });

        result.Should().NotBeNull();
        ((string)result.event_type).Should().Be("TestEvent");
        ((string)result.actor).Should().Be("test_actor");
        ((string)result.category).Should().Be("test");
    }

    [Fact]
    public async Task EmitMemoryEventAsync_WritesToBothTables()
    {
        // Arrange
        var memoryId = Guid.CreateVersion7();
        var eventData = new { content_length = 100, source = "test" };

        // Act
        var eventId = await _eventWriter.EmitMemoryEventAsync(
            _testTenantId,
            memoryId,
            "MemoryCreated",
            eventData,
            "test_worker");

        // Assert
        eventId.Should().NotBeEmpty();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = _testTenantId.ToString() });

        // Verify memory_events
        var memoryEvent = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM memory_events WHERE event_id = @Id",
            new { Id = eventId });
        memoryEvent.Should().NotBeNull();
        ((Guid)memoryEvent.stream_id).Should().Be(memoryId);

        // Verify event_log
        var eventLog = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM event_log WHERE memory_id = @MemoryId AND event_type = 'MemoryCreated'",
            new { MemoryId = memoryId });
        eventLog.Should().NotBeNull();
    }

    [Fact]
    public async Task EmitLayerGeneratedAsync_EmitsCorrectEventType()
    {
        // Arrange
        var memoryId = Guid.CreateVersion7();

        // Act
        var eventId = await _eventWriter.EmitLayerGeneratedAsync(
            _testTenantId,
            memoryId,
            "L1_CONTEXT",
            new { summary = "Test context" },
            "gpt-4",
            150,
            0.95m);

        // Assert
        eventId.Should().NotBeEmpty();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = _testTenantId.ToString() });

        var result = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM event_log WHERE memory_id = @MemoryId AND event_type = 'LayerGenerated'",
            new { MemoryId = memoryId });

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task EmitBranchCreatedAsync_EmitsCorrectEvent()
    {
        // Arrange
        var branchId = Guid.CreateVersion7();

        // Act
        var eventId = await _eventWriter.EmitBranchCreatedAsync(
            _testTenantId,
            branchId,
            "feature-test",
            "main",
            "Test branch description");

        // Assert
        eventId.Should().NotBeEmpty();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = _testTenantId.ToString() });

        var result = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM event_log WHERE event_type = 'BranchCreated' AND tenant_id = @TenantId",
            new { TenantId = _testTenantId });

        result.Should().NotBeNull();
        ((string)result.category).Should().Be("branch");
    }

    [Fact]
    public async Task EmitConflictDetectedAsync_EmitsWarningEvent()
    {
        // Arrange
        var memoryAId = Guid.CreateVersion7();
        var memoryBId = Guid.CreateVersion7();

        // Act
        var eventId = await _eventWriter.EmitConflictDetectedAsync(
            _testTenantId,
            memoryAId,
            memoryBId,
            0.92m,
            "semantic",
            "These memories contain conflicting information");

        // Assert
        eventId.Should().NotBeEmpty();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = _testTenantId.ToString() });

        var result = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM event_log WHERE id = @Id",
            new { Id = eventId });

        result.Should().NotBeNull();
        ((string)result.severity).Should().Be("warning");
        ((string)result.category).Should().Be("integrity");
    }

    [Fact]
    public async Task ComputeMindHealthAsync_CreatesHealthRecord()
    {
        // Act
        var healthId = await _eventWriter.ComputeMindHealthAsync(_testTenantId);

        // Assert
        healthId.Should().NotBeEmpty();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = _testTenantId.ToString() });

        var result = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM mind_health WHERE id = @Id",
            new { Id = healthId });

        result.Should().NotBeNull();
        ((string)result.health_status).Should().BeOneOf("healthy", "warning", "critical");
    }

    [Fact]
    public async Task CreateTimelineSnapshotAsync_CreatesSnapshot()
    {
        // Arrange
        var memoryId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();

        // Act
        var snapshotId = await _eventWriter.CreateTimelineSnapshotAsync(
            _testTenantId,
            memoryId,
            eventId,
            "LayerGenerated",
            "Test memory content",
            "L1_CONTEXT",
            0.95m);

        // Assert
        snapshotId.Should().NotBeEmpty();

        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = _testTenantId.ToString() });

        var result = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM memory_timeline WHERE id = @Id",
            new { Id = snapshotId });

        result.Should().NotBeNull();
        ((Guid)result.memory_id).Should().Be(memoryId);
        ((string)result.layer).Should().Be("L1_CONTEXT");
    }

    [Fact]
    public async Task RecentEvents_QueryReturnsEvents()
    {
        // Arrange - emit several events
        await _eventWriter.EmitSystemEventAsync(_testTenantId, "Event1", new { n = 1 });
        await _eventWriter.EmitSystemEventAsync(_testTenantId, "Event2", new { n = 2 });
        await _eventWriter.EmitSystemEventAsync(_testTenantId, "Event3", new { n = 3 });

        // Act
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, false)",
            new { TenantId = _testTenantId.ToString() });

        var events = (await conn.QueryAsync<dynamic>(
            "SELECT * FROM get_recent_events(@TenantId, 10, 0, NULL, NULL)",
            new { TenantId = _testTenantId })).ToList();

        // Assert
        events.Should().HaveCountGreaterThanOrEqualTo(3);
    }
}

// Helper extension for Dapper
public static class NpgsqlConnectionExtensions
{
    public static async Task ExecuteAsync(this NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task ExecuteAsync(this NpgsqlConnection conn, string sql, object param)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var prop in param.GetType().GetProperties())
        {
            cmd.Parameters.AddWithValue($"@{prop.Name}", prop.GetValue(param) ?? DBNull.Value);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T?> QueryFirstOrDefaultAsync<T>(this NpgsqlConnection conn, string sql, object param)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var prop in param.GetType().GetProperties())
        {
            cmd.Parameters.AddWithValue($"@{prop.Name}", prop.GetValue(param) ?? DBNull.Value);
        }
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            // For dynamic, return a dictionary
            if (typeof(T) == typeof(object))
            {
                var expando = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    expando[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                return (T)(object)expando;
            }
        }
        return default;
    }

    public static async Task<IEnumerable<T>> QueryAsync<T>(this NpgsqlConnection conn, string sql, object param)
    {
        var results = new List<T>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var prop in param.GetType().GetProperties())
        {
            cmd.Parameters.AddWithValue($"@{prop.Name}", prop.GetValue(param) ?? DBNull.Value);
        }
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (typeof(T) == typeof(object))
            {
                var expando = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    expando[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add((T)(object)expando);
            }
        }
        return results;
    }

    public static async Task<T?> ExecuteScalarAsync<T>(this NpgsqlConnection conn, string sql, object param)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var prop in param.GetType().GetProperties())
        {
            cmd.Parameters.AddWithValue($"@{prop.Name}", prop.GetValue(param) ?? DBNull.Value);
        }
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value)
            return default;
        return (T)result;
    }
}
