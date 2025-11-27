using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace SerialMemory.Mcp.Tests;

[Collection("PostgreSQL")]
public class UsageServiceIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private string _connectionString = "";

    public UsageServiceIntegrationTests()
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

        // Apply schema
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Create pgvector extension
        await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");

        // Read and apply usage metering schema
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ops", "usage_metering_schema.sql");
        if (File.Exists(schemaPath))
        {
            var schemaSql = await File.ReadAllTextAsync(schemaPath);
            await conn.ExecuteAsync(schemaSql);
        }
        else
        {
            // Apply inline schema if file not found
            await ApplyInlineSchema(conn);
        }
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    private static async Task ApplyInlineSchema(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'usage_event_type') THEN
                    CREATE TYPE usage_event_type AS ENUM (
                        'memory_ingest', 'memory_search', 'memory_multi_hop_search',
                        'memory_update', 'memory_delete', 'memory_merge', 'memory_split',
                        'memory_decay', 'memory_reinforce', 'memory_expire',
                        'crawl_relationships', 'export_workspace', 'export_memories',
                        'export_graph', 'reembed_memories'
                    );
                END IF;
            END $$;

            CREATE TABLE IF NOT EXISTS tenant_plans (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                plan_name TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                credits_per_cycle DECIMAL(12, 2) NOT NULL DEFAULT 1000.00,
                cycle_days INTEGER NOT NULL DEFAULT 30,
                max_memories INTEGER,
                max_entities INTEGER,
                is_active BOOLEAN NOT NULL DEFAULT TRUE,
                metadata JSONB,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS billing_cycles (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                plan_id UUID REFERENCES tenant_plans(id),
                cycle_start TIMESTAMPTZ NOT NULL,
                cycle_end TIMESTAMPTZ NOT NULL,
                credits_allocated DECIMAL(12, 2) NOT NULL DEFAULT 0,
                credits_used DECIMAL(12, 2) NOT NULL DEFAULT 0,
                is_current BOOLEAN NOT NULL DEFAULT TRUE,
                closed_at TIMESTAMPTZ,
                metadata JSONB,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS usage_events (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                billing_cycle_id UUID REFERENCES billing_cycles(id),
                event_type usage_event_type NOT NULL,
                credits_consumed DECIMAL(8, 4) NOT NULL,
                event_timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                memory_id UUID,
                user_id TEXT,
                session_id UUID,
                latency_ms INTEGER,
                success BOOLEAN NOT NULL DEFAULT TRUE,
                error_message TEXT,
                metadata JSONB
            );

            CREATE TABLE IF NOT EXISTS usage_daily_rollups (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                tenant_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                rollup_date DATE NOT NULL,
                event_type usage_event_type NOT NULL,
                event_count INTEGER NOT NULL DEFAULT 0,
                total_credits DECIMAL(12, 4) NOT NULL DEFAULT 0,
                success_count INTEGER NOT NULL DEFAULT 0,
                failure_count INTEGER NOT NULL DEFAULT 0,
                avg_latency_ms DECIMAL(8, 2),
                p95_latency_ms INTEGER,
                p99_latency_ms INTEGER,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                CONSTRAINT usage_daily_rollups_unique UNIQUE (tenant_id, workspace_id, rollup_date, event_type)
            );

            INSERT INTO tenant_plans (plan_name, display_name, credits_per_cycle, cycle_days)
            VALUES ('self', 'Self-Hosted', 999999999.00, 30)
            ON CONFLICT (plan_name) DO NOTHING;

            CREATE OR REPLACE FUNCTION get_or_create_billing_cycle(
                p_tenant_id TEXT,
                p_workspace_id TEXT
            )
            RETURNS UUID AS $$
            DECLARE
                v_cycle_id UUID;
                v_plan_id UUID;
                v_credits DECIMAL(12, 2);
                v_cycle_days INTEGER;
            BEGIN
                SELECT id INTO v_cycle_id
                FROM billing_cycles
                WHERE tenant_id = p_tenant_id
                  AND workspace_id = p_workspace_id
                  AND is_current = TRUE
                  AND cycle_end > NOW();

                IF v_cycle_id IS NOT NULL THEN
                    RETURN v_cycle_id;
                END IF;

                UPDATE billing_cycles
                SET is_current = FALSE, closed_at = NOW()
                WHERE tenant_id = p_tenant_id
                  AND workspace_id = p_workspace_id
                  AND is_current = TRUE
                  AND cycle_end <= NOW();

                SELECT id, credits_per_cycle, cycle_days INTO v_plan_id, v_credits, v_cycle_days
                FROM tenant_plans
                WHERE plan_name = 'self' AND is_active = TRUE
                LIMIT 1;

                INSERT INTO billing_cycles (tenant_id, workspace_id, plan_id, cycle_start, cycle_end, credits_allocated)
                VALUES (
                    p_tenant_id,
                    p_workspace_id,
                    v_plan_id,
                    NOW(),
                    NOW() + (v_cycle_days || ' days')::INTERVAL,
                    v_credits
                )
                RETURNING id INTO v_cycle_id;

                RETURN v_cycle_id;
            END;
            $$ LANGUAGE plpgsql;
        ");
    }

    [Fact]
    public async Task TrackUsage_WritesEventToDatabase()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        using var service = new UsageService(_connectionString, loggerFactory.CreateLogger<UsageService>());

        // Act
        service.TrackUsage(
            UsageEventType.MemorySearch,
            latencyMs: 100,
            success: true);

        // Wait for background processing
        await Task.Delay(2000);

        // Assert
        await using var conn = new NpgsqlConnection(_connectionString);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM usage_events WHERE event_type = 'memory_search'");

        Assert.True(count >= 1, "Usage event should be written to database");
    }

    [Fact]
    public async Task TrackUsage_CreatesCorrectCreditsConsumed()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        using var service = new UsageService(_connectionString, loggerFactory.CreateLogger<UsageService>());

        // Act
        service.TrackUsage(UsageEventType.ExportWorkspace, latencyMs: 500);

        // Wait for background processing
        await Task.Delay(2000);

        // Assert
        await using var conn = new NpgsqlConnection(_connectionString);
        var credits = await conn.ExecuteScalarAsync<decimal>(
            "SELECT credits_consumed FROM usage_events WHERE event_type = 'export_workspace' ORDER BY event_timestamp DESC LIMIT 1");

        Assert.Equal(10.0m, credits);
    }

    [Fact]
    public async Task TrackUsage_CreatesBillingCycle()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        using var service = new UsageService(_connectionString, loggerFactory.CreateLogger<UsageService>());

        // Act
        service.TrackUsage(UsageEventType.MemoryIngest);

        // Wait for background processing
        await Task.Delay(2000);

        // Assert
        await using var conn = new NpgsqlConnection(_connectionString);
        var cycleCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM billing_cycles WHERE tenant_id = 'self' AND workspace_id = 'default'");

        Assert.True(cycleCount >= 1, "Billing cycle should be created");
    }

    [Fact]
    public async Task GetCurrentBillingCycle_ReturnsCycle()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        using var service = new UsageService(_connectionString, loggerFactory.CreateLogger<UsageService>());

        // Track an event to ensure cycle exists
        service.TrackUsage(UsageEventType.MemorySearch);
        await Task.Delay(2000);

        // Act
        var cycle = await service.GetCurrentBillingCycleAsync();

        // Assert
        Assert.NotNull(cycle);
        Assert.Equal("self", cycle.TenantId);
        Assert.Equal("default", cycle.WorkspaceId);
        Assert.True(cycle.IsCurrent);
    }

    [Fact]
    public async Task TrackUsage_BatchesMultipleEvents()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        using var service = new UsageService(_connectionString, loggerFactory.CreateLogger<UsageService>());

        // Act - track multiple events quickly
        for (var i = 0; i < 10; i++)
        {
            service.TrackUsage(
                UsageEventType.MemorySearch,
                latencyMs: 100 + i);
        }

        // Wait for background processing
        await Task.Delay(3000);

        // Assert
        await using var conn = new NpgsqlConnection(_connectionString);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM usage_events WHERE event_type = 'memory_search'");

        Assert.True(count >= 10, $"Expected at least 10 events, got {count}");
    }

    [Fact]
    public async Task TrackUsage_RecordsFailures()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        using var service = new UsageService(_connectionString, loggerFactory.CreateLogger<UsageService>());

        // Act
        service.TrackUsage(
            UsageEventType.MemoryIngest,
            latencyMs: 50,
            success: false,
            errorMessage: "Test error message");

        // Wait for background processing
        await Task.Delay(2000);

        // Assert
        await using var conn = new NpgsqlConnection(_connectionString);
        var result = await conn.QueryFirstOrDefaultAsync<dynamic>(
            @"SELECT success, error_message
              FROM usage_events
              WHERE event_type = 'memory_ingest' AND success = FALSE
              ORDER BY event_timestamp DESC LIMIT 1");

        Assert.NotNull(result);
        Assert.False((bool)result.success);
        Assert.Equal("Test error message", (string)result.error_message);
    }

    [Fact]
    public async Task UsageService_DoesNotBlockOnTracking()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        using var service = new UsageService(_connectionString, loggerFactory.CreateLogger<UsageService>());

        // Act & Assert - TrackUsage should return immediately (non-blocking)
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 100; i++)
        {
            service.TrackUsage(UsageEventType.MemorySearch, latencyMs: 10);
        }

        sw.Stop();

        // Should complete in under 100ms (actual DB writes happen in background)
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"TrackUsage should be non-blocking, took {sw.ElapsedMilliseconds}ms for 100 calls");
    }

    [Fact]
    public async Task GetRecentEvents_ReturnsEvents()
    {
        // Arrange
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        using var service = new UsageService(_connectionString, loggerFactory.CreateLogger<UsageService>());

        // Track some events
        service.TrackUsage(UsageEventType.MemorySearch, latencyMs: 100);
        service.TrackUsage(UsageEventType.MemoryIngest, latencyMs: 200);
        await Task.Delay(2000);

        // Act
        var events = await service.GetRecentEventsAsync(limit: 10);

        // Assert
        Assert.NotNull(events);
        Assert.True(events.Count >= 2);
    }
}
