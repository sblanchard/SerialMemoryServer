using Dapper;
using Xunit;

namespace SerialMemory.Tests.TenantIsolation;

/// <summary>
/// BUILD GATE TESTS
///
/// These tests serve as the final verification that tenant isolation is enforced.
/// If ANY of these tests fail, the build MUST fail and deployment MUST be blocked.
///
/// Rule: No future data-access code is allowed without passing these tests.
/// </summary>
[Collection("TenantIsolation")]
public sealed class TenantIsolationBuildGateTests : TenantIsolationTestBase
{
    /// <summary>
    /// CRITICAL BUILD GATE: Comprehensive cross-tenant data leak test.
    /// This single test verifies ALL tables are properly isolated.
    /// </summary>
    [Fact]
    public async Task BuildGate_NoTenantDataLeakage_AnyTable()
    {
        var tables = new[]
        {
            ("memories", "id", "tenant_id"),
            ("entities", "id", "tenant_id"),
            ("entity_relationships", "id", "tenant_id"),
            ("memory_entities", "memory_id", "tenant_id"),
            ("user_personas", "id", "tenant_id"),
            ("conversation_sessions", "id", "tenant_id")
        };

        var failures = new List<string>();

        // Test each table for both tenants
        foreach (var (table, pkColumn, tenantColumn) in tables)
        {
            // TenantA should only see TenantA's data
            await using var connA = await ConnectionFactory.OpenAsync(TenantA);
            var resultA = await connA.QueryAsync<(Guid TenantId, int Count)>($@"
                SELECT {tenantColumn} AS TenantId, COUNT(*) AS Count
                FROM {table}
                GROUP BY {tenantColumn}");

            foreach (var row in resultA)
            {
                if (row.TenantId != TenantA)
                {
                    failures.Add($"BREACH: TenantA saw {row.Count} rows from tenant {row.TenantId} in {table}");
                }
            }

            // TenantB should only see TenantB's data
            await using var connB = await ConnectionFactory.OpenAsync(TenantB);
            var resultB = await connB.QueryAsync<(Guid TenantId, int Count)>($@"
                SELECT {tenantColumn} AS TenantId, COUNT(*) AS Count
                FROM {table}
                GROUP BY {tenantColumn}");

            foreach (var row in resultB)
            {
                if (row.TenantId != TenantB)
                {
                    failures.Add($"BREACH: TenantB saw {row.Count} rows from tenant {row.TenantId} in {table}");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new TenantIsolationBreachException(
                Guid.Empty,
                new[] { TenantA, TenantB },
                "BuildGate comprehensive leak test",
                failures.Count)
            {
                Data = { ["Failures"] = string.Join("\n", failures) }
            };
        }
    }

    /// <summary>
    /// BUILD GATE: RLS guardrail must reject queries without tenant context.
    /// </summary>
    [Fact]
    public async Task BuildGate_RlsGuardrail_RejectsNoContext()
    {
        var tables = new[] { "memories", "entities", "entity_relationships", "user_personas", "conversation_sessions" };

        await using var conn = await ConnectionFactory.OpenWithoutTenantContextAsync();

        foreach (var table in tables)
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table}");
            });

            Assert.Contains("tenant_id", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// BUILD GATE: Vector similarity search must be isolated.
    /// </summary>
    [Fact]
    public async Task BuildGate_VectorSearch_Isolated()
    {
        // Use TenantB's embedding to search as TenantA
        var queryEmbedding = TenantBData.Embeddings.First().Value;
        var queryVector = $"[{string.Join(",", queryEmbedding)}]";

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var results = await conn.QueryAsync<(Guid Id, Guid TenantId)>(@"
            SELECT id, tenant_id
            FROM memories
            ORDER BY embedding <=> @QueryVector::vector
            LIMIT 100",
            new { QueryVector = queryVector });

        // CRITICAL: No TenantB data should appear
        foreach (var result in results)
        {
            Assert.Equal(TenantA, result.TenantId);
        }
    }

    /// <summary>
    /// BUILD GATE: Graph traversal must be isolated.
    /// </summary>
    [Fact]
    public async Task BuildGate_GraphTraversal_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var startEntityId = TenantAData.EntityIds.First();

        // Multi-hop traversal
        var reachable = await conn.QueryAsync<(Guid Id, Guid TenantId)>(@"
            WITH RECURSIVE traverse AS (
                SELECT id, tenant_id FROM entities WHERE id = @Start
                UNION ALL
                SELECT e.id, e.tenant_id FROM entities e
                JOIN entity_relationships r ON e.id = r.target_entity_id
                JOIN traverse t ON r.source_entity_id = t.id
                WHERE e.id != t.id
            )
            SELECT DISTINCT id, tenant_id FROM traverse",
            new { Start = startEntityId });

        foreach (var node in reachable)
        {
            Assert.Equal(TenantA, node.TenantId);
        }
    }

    /// <summary>
    /// BUILD GATE: Writes with wrong tenant_id must fail.
    /// </summary>
    [Fact]
    public async Task BuildGate_WrongTenantWrite_Fails()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Try to write with TenantB's ID while connected as TenantA
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ExecuteAsync(@"
                INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
                VALUES (@Id, @TenantId, 'Malicious insert', NOW(), NOW())",
                new { Id = Guid.CreateVersion7(), TenantId = TenantB });
        });

        Assert.Contains("row-level security", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// BUILD GATE: Total row counts must match expected seeded data.
    /// </summary>
    [Fact]
    public async Task BuildGate_DataIntegrity_RowCounts()
    {
        // Verify superuser sees all data
        await using var superConn = await ConnectionFactory.OpenAsSuperuserAsync();

        var totalMemories = await superConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        Assert.Equal(TenantAData.MemoryIds.Count + TenantBData.MemoryIds.Count, totalMemories);

        var totalEntities = await superConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entities");
        Assert.Equal(TenantAData.EntityIds.Count + TenantBData.EntityIds.Count, totalEntities);

        // Verify each tenant sees only their data
        await using var connA = await ConnectionFactory.OpenAsync(TenantA);
        var countA = await connA.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        Assert.Equal(TenantAData.MemoryIds.Count, countA);

        await using var connB = await ConnectionFactory.OpenAsync(TenantB);
        var countB = await connB.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        Assert.Equal(TenantBData.MemoryIds.Count, countB);
    }

    /// <summary>
    /// BUILD GATE: Cross-tenant foreign key attacks must fail.
    /// </summary>
    [Fact]
    public async Task BuildGate_CrossTenantForeignKey_Fails()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Try to create relationship pointing to TenantB's entity
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ExecuteAsync(@"
                INSERT INTO entity_relationships
                    (id, tenant_id, source_entity_id, target_entity_id, relationship_type)
                VALUES
                    (@Id, @TenantId, @SourceId, @TargetId, 'ATTACK')",
                new
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = TenantA,
                    SourceId = TenantAData.EntityIds.First(),
                    TargetId = TenantBData.EntityIds.First()
                });
        });

        // Should fail (FK violation or RLS)
        Assert.NotNull(exception);
    }

    /// <summary>
    /// BUILD GATE: Verify RLS is FORCED on all tenant-scoped tables.
    /// </summary>
    [Fact]
    public async Task BuildGate_RlsForcedOnAllTables()
    {
        await using var conn = await ConnectionFactory.OpenAsSuperuserAsync();

        var tables = new[] { "memories", "entities", "entity_relationships", "memory_entities", "user_personas", "conversation_sessions" };

        foreach (var table in tables)
        {
            var (rlsEnabled, rlsForced) = await conn.QueryFirstAsync<(bool RlsEnabled, bool RlsForced)>(@"
                SELECT relrowsecurity, relforcerowsecurity
                FROM pg_class WHERE relname = @Table",
                new { Table = table });

            Assert.True(rlsEnabled, $"RLS not enabled on {table}");
            Assert.True(rlsForced, $"RLS not forced on {table}");
        }
    }
}
