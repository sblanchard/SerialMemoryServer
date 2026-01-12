using Dapper;
using Xunit;

namespace SerialMemory.Tests.TenantIsolation;

/// <summary>
/// Canonical tests verifying Row-Level Security (RLS) tenant isolation.
///
/// FAIL CONDITIONS:
/// - ANY cross-tenant data access = test failure = build failure
/// - Query without tenant context succeeds = test failure = build failure
/// - Write with mismatched tenant_id succeeds = test failure = build failure
///
/// These tests MUST pass for any data-access code to be deployed.
/// </summary>
[Collection("TenantIsolation")]
public sealed class RlsIsolationTests : TenantIsolationTestBase
{
    // ==========================================
    // PART 3.1: TenantA cannot read TenantB memories
    // ==========================================

    [Fact]
    public async Task TenantA_Cannot_Read_TenantB_Memories()
    {
        // Arrange: Get a specific memory ID from Tenant B's seeded data
        var targetMemoryId = TenantBData.MemoryIds.First();

        // Act: Tenant A attempts to read Tenant B's memory
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);
        var result = await conn.QueryFirstOrDefaultAsync<MemoryRecord>(
            "SELECT id, tenant_id, content FROM memories WHERE id = @Id",
            new { Id = targetMemoryId });

        // Assert: RLS should filter this out completely
        Assert.Null(result);
    }

    [Fact]
    public async Task TenantA_Cannot_Read_TenantB_Memories_ViaStar()
    {
        // Test that SELECT * doesn't leak data
        await using var connA = await ConnectionFactory.OpenAsync(TenantA);
        var memoriesA = (await connA.QueryAsync<MemoryRecord>(
            "SELECT id, tenant_id, content FROM memories")).ToList();

        // Verify ALL returned memories belong to Tenant A
        AssertNoCrossTenantDataBreach(
            memoriesA,
            TenantA,
            m => m.TenantId,
            "SELECT * FROM memories as TenantA");

        // Also verify we got the expected count
        Assert.Equal(TenantAData.MemoryIds.Count, memoriesA.Count);
    }

    // ==========================================
    // PART 3.2: TenantB cannot read TenantA memories
    // ==========================================

    [Fact]
    public async Task TenantB_Cannot_Read_TenantA_Memories()
    {
        var targetMemoryId = TenantAData.MemoryIds.First();

        await using var conn = await ConnectionFactory.OpenAsync(TenantB);
        var result = await conn.QueryFirstOrDefaultAsync<MemoryRecord>(
            "SELECT id, tenant_id, content FROM memories WHERE id = @Id",
            new { Id = targetMemoryId });

        Assert.Null(result);
    }

    [Fact]
    public async Task TenantB_Cannot_Read_TenantA_Memories_ViaStar()
    {
        await using var connB = await ConnectionFactory.OpenAsync(TenantB);
        var memoriesB = (await connB.QueryAsync<MemoryRecord>(
            "SELECT id, tenant_id, content FROM memories")).ToList();

        AssertNoCrossTenantDataBreach(
            memoriesB,
            TenantB,
            m => m.TenantId,
            "SELECT * FROM memories as TenantB");

        Assert.Equal(TenantBData.MemoryIds.Count, memoriesB.Count);
    }

    // ==========================================
    // PART 3.4: Queries without WHERE tenant_id still isolate (RLS proof)
    // ==========================================

    [Fact]
    public async Task Query_Without_Where_TenantId_Still_Isolates_Memories()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Query without any WHERE clause - RLS should still filter
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");

        // Should only see TenantA's 10 memories, not TenantB's
        Assert.Equal(TenantAData.MemoryIds.Count, count);
    }

    [Fact]
    public async Task Query_Without_Where_TenantId_Still_Isolates_Entities()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entities");

        Assert.Equal(TenantBData.EntityIds.Count, count);
    }

    [Fact]
    public async Task Query_Without_Where_TenantId_Still_Isolates_EntityRelationships()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entity_relationships");

        Assert.Equal(TenantAData.RelationshipIds.Count, count);
    }

    [Fact]
    public async Task Query_Without_Where_TenantId_Still_Isolates_Sessions()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM conversation_sessions");

        Assert.Equal(TenantBData.SessionIds.Count, count);
    }

    [Fact]
    public async Task Query_Without_Where_TenantId_Still_Isolates_UserPersonas()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM user_personas");

        Assert.Equal(TenantAData.PersonaIds.Count, count);
    }

    // ==========================================
    // RLS GUARDRAIL TESTS
    // ==========================================

    [Fact]
    public async Task Query_Without_TenantContext_Fails_With_SecurityViolation()
    {
        await using var conn = await ConnectionFactory.OpenWithoutTenantContextAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        });

        Assert.Contains("tenant_id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Insert_Without_TenantContext_Fails()
    {
        await using var conn = await ConnectionFactory.OpenWithoutTenantContextAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ExecuteAsync(@"
                INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
                VALUES (@Id, @TenantId, 'Sneaky insert', NOW(), NOW())",
                new { Id = Guid.CreateVersion7(), TenantId = TenantA });
        });

        Assert.Contains("tenant_id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Insert_With_Mismatched_TenantId_Fails()
    {
        // Connect as TenantA
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Try to insert with TenantB's ID
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ExecuteAsync(@"
                INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
                VALUES (@Id, @TenantId, 'Wrong tenant insert', NOW(), NOW())",
                new { Id = Guid.CreateVersion7(), TenantId = TenantB });
        });

        Assert.Contains("row-level security", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================
    // CROSS-TENANT MODIFICATION TESTS
    // ==========================================

    [Fact]
    public async Task TenantA_Cannot_Update_TenantB_Memories()
    {
        var targetMemoryId = TenantBData.MemoryIds.First();

        await using var connA = await ConnectionFactory.OpenAsync(TenantA);
        var rowsAffected = await connA.ExecuteAsync(
            "UPDATE memories SET content = 'HACKED!' WHERE id = @Id",
            new { Id = targetMemoryId });

        // RLS should prevent finding the row
        Assert.Equal(0, rowsAffected);

        // Verify original content unchanged
        await using var connB = await ConnectionFactory.OpenAsync(TenantB);
        var content = await connB.ExecuteScalarAsync<string>(
            "SELECT content FROM memories WHERE id = @Id",
            new { Id = targetMemoryId });

        Assert.Contains("Tenant B", content);
    }

    [Fact]
    public async Task TenantB_Cannot_Delete_TenantA_Memories()
    {
        var targetMemoryId = TenantAData.MemoryIds.First();

        await using var connB = await ConnectionFactory.OpenAsync(TenantB);
        var rowsDeleted = await connB.ExecuteAsync(
            "DELETE FROM memories WHERE id = @Id",
            new { Id = targetMemoryId });

        Assert.Equal(0, rowsDeleted);

        // Verify memory still exists
        await using var connA = await ConnectionFactory.OpenAsync(TenantA);
        var exists = await connA.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM memories WHERE id = @Id)",
            new { Id = targetMemoryId });

        Assert.True(exists);
    }

    [Fact]
    public async Task TenantA_Cannot_Update_TenantB_Entities()
    {
        var targetEntityId = TenantBData.EntityIds.First();

        await using var connA = await ConnectionFactory.OpenAsync(TenantA);
        var rowsAffected = await connA.ExecuteAsync(
            "UPDATE entities SET name = 'HACKED!' WHERE id = @Id",
            new { Id = targetEntityId });

        Assert.Equal(0, rowsAffected);
    }

    [Fact]
    public async Task TenantB_Cannot_Delete_TenantA_Relationships()
    {
        var targetRelId = TenantAData.RelationshipIds.First();

        await using var connB = await ConnectionFactory.OpenAsync(TenantB);
        var rowsDeleted = await connB.ExecuteAsync(
            "DELETE FROM entity_relationships WHERE id = @Id",
            new { Id = targetRelId });

        Assert.Equal(0, rowsDeleted);
    }

    // ==========================================
    // EDGE CASE TESTS
    // ==========================================

    [Fact]
    public async Task Count_Star_Returns_Only_Own_Tenant_Data()
    {
        // Verify superuser sees all data
        await using var superConn = await ConnectionFactory.OpenAsSuperuserAsync();
        var totalMemories = await superConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        Assert.Equal(TenantAData.MemoryIds.Count + TenantBData.MemoryIds.Count, totalMemories);

        // TenantA sees only their data
        await using var connA = await ConnectionFactory.OpenAsync(TenantA);
        var countA = await connA.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        Assert.Equal(TenantAData.MemoryIds.Count, countA);

        // TenantB sees only their data
        await using var connB = await ConnectionFactory.OpenAsync(TenantB);
        var countB = await connB.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        Assert.Equal(TenantBData.MemoryIds.Count, countB);
    }

    [Fact]
    public async Task Subquery_Cannot_Bypass_RLS()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Try to access TenantB's data via subquery
        var leakedContent = await conn.QueryAsync<string>(@"
            SELECT content FROM memories
            WHERE id IN (SELECT id FROM memories)");

        // All results should still be TenantA's data only
        foreach (var content in leakedContent)
        {
            Assert.Contains("Tenant A", content);
        }
    }

    [Fact]
    public async Task Union_Cannot_Bypass_RLS()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var results = await conn.QueryAsync<MemoryRecord>(@"
            SELECT id, tenant_id, content FROM memories
            UNION ALL
            SELECT id, tenant_id, content FROM memories");

        AssertNoCrossTenantDataBreach(
            results,
            TenantA,
            m => m.TenantId,
            "UNION query");
    }

    [Fact]
    public async Task CTE_Cannot_Bypass_RLS()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        var results = await conn.QueryAsync<MemoryRecord>(@"
            WITH all_memories AS (
                SELECT id, tenant_id, content FROM memories
            )
            SELECT * FROM all_memories");

        AssertNoCrossTenantDataBreach(
            results,
            TenantB,
            m => m.TenantId,
            "CTE query");
    }

    // ==========================================
    // HELPER TYPES
    // ==========================================

    private sealed record MemoryRecord(Guid id, Guid tenant_id, string content)
    {
        public Guid Id => id;
        public Guid TenantId => tenant_id;
    }
}
