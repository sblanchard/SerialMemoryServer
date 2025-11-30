using Dapper;
using Xunit;

namespace SerialMemory.Tests.TenantIsolation;

/// <summary>
/// Tests verifying knowledge graph operations respect tenant isolation.
///
/// PART 3.5: Graph queries cannot leak nodes
///
/// CRITICAL: Entity relationships and graph traversals must NEVER cross tenant boundaries.
/// A tenant's knowledge graph must be completely isolated from other tenants.
/// </summary>
[Collection("TenantIsolation")]
public sealed class GraphIsolationTests : TenantIsolationTestBase
{
    // ==========================================
    // ENTITY ISOLATION TESTS
    // ==========================================

    [Fact]
    public async Task TenantA_Cannot_Read_TenantB_Entities()
    {
        var targetEntityId = TenantBData.EntityIds.First();

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var result = await conn.QueryFirstOrDefaultAsync<EntityRecord>(
            "SELECT id, tenant_id, name, entity_type FROM entities WHERE id = @Id",
            new { Id = targetEntityId });

        Assert.Null(result);
    }

    [Fact]
    public async Task Entity_List_Returns_Only_Own_Tenant_Entities()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var entities = await conn.QueryAsync<EntityRecord>(
            "SELECT id, tenant_id, name, entity_type FROM entities");

        var entityList = entities.ToList();

        AssertNoCrossTenantDataBreach(
            entityList,
            TenantA,
            e => e.TenantId,
            "entity list query");

        Assert.Equal(TenantAData.EntityIds.Count, entityList.Count);
    }

    [Fact]
    public async Task Entity_Search_By_Type_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        var persons = await conn.QueryAsync<EntityRecord>(
            "SELECT id, tenant_id, name, entity_type FROM entities WHERE entity_type = 'PERSON'");

        var personList = persons.ToList();

        AssertNoCrossTenantDataBreach(
            personList,
            TenantB,
            e => e.TenantId,
            "entity search by type");
    }

    [Fact]
    public async Task Entity_Search_By_Name_Pattern_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Search with pattern that would match both tenants' data
        var entities = await conn.QueryAsync<EntityRecord>(
            "SELECT id, tenant_id, name, entity_type FROM entities WHERE name LIKE '%Entity%'");

        var entityList = entities.ToList();

        AssertNoCrossTenantDataBreach(
            entityList,
            TenantA,
            e => e.TenantId,
            "entity name pattern search");

        // Should only match TenantA's entities
        foreach (var entity in entityList)
        {
            Assert.Contains("Tenant A", entity.name);
        }
    }

    // ==========================================
    // RELATIONSHIP ISOLATION TESTS
    // ==========================================

    [Fact]
    public async Task TenantA_Cannot_Read_TenantB_Relationships()
    {
        var targetRelId = TenantBData.RelationshipIds.First();

        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var result = await conn.QueryFirstOrDefaultAsync<RelationshipRecord>(
            "SELECT id, tenant_id, source_entity_id, target_entity_id, relationship_type FROM entity_relationships WHERE id = @Id",
            new { Id = targetRelId });

        Assert.Null(result);
    }

    [Fact]
    public async Task Relationship_List_Returns_Only_Own_Tenant_Relationships()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        var relationships = await conn.QueryAsync<RelationshipRecord>(
            "SELECT id, tenant_id, source_entity_id, target_entity_id, relationship_type FROM entity_relationships");

        var relList = relationships.ToList();

        AssertNoCrossTenantDataBreach(
            relList,
            TenantB,
            r => r.TenantId,
            "relationship list query");

        Assert.Equal(TenantBData.RelationshipIds.Count, relList.Count);
    }

    // ==========================================
    // GRAPH TRAVERSAL ISOLATION TESTS
    // ==========================================

    [Fact]
    public async Task Graph_Traversal_Cannot_Reach_Other_Tenant_Nodes()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var startEntityId = TenantAData.EntityIds.First();

        // Traverse graph from a starting entity
        var connectedEntities = await conn.QueryAsync<EntityRecord>(@"
            SELECT DISTINCT e.id, e.tenant_id, e.name, e.entity_type
            FROM entities e
            JOIN entity_relationships r ON e.id = r.target_entity_id
            WHERE r.source_entity_id = @StartId",
            new { StartId = startEntityId });

        var entityList = connectedEntities.ToList();

        // All traversed entities must belong to TenantA
        AssertNoCrossTenantDataBreach(
            entityList,
            TenantA,
            e => e.TenantId,
            "graph traversal");

        // Should only reach TenantA's entities
        foreach (var entity in entityList)
        {
            Assert.Contains(entity.id, TenantAData.EntityIds);
        }
    }

    [Fact]
    public async Task Recursive_Graph_Traversal_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var startEntityId = TenantAData.EntityIds.First();

        // Recursive CTE for multi-hop graph traversal
        var reachableEntities = await conn.QueryAsync<EntityRecord>(@"
            WITH RECURSIVE graph_walk AS (
                -- Start from the initial entity
                SELECT e.id, e.tenant_id, e.name, e.entity_type, 0 AS depth
                FROM entities e
                WHERE e.id = @StartId

                UNION ALL

                -- Recursively traverse relationships
                SELECT e.id, e.tenant_id, e.name, e.entity_type, gw.depth + 1
                FROM entities e
                JOIN entity_relationships r ON e.id = r.target_entity_id
                JOIN graph_walk gw ON r.source_entity_id = gw.id
                WHERE gw.depth < 3  -- Limit depth
            )
            SELECT DISTINCT id, tenant_id, name, entity_type
            FROM graph_walk",
            new { StartId = startEntityId });

        var reachableList = reachableEntities.ToList();

        // CRITICAL: Recursive traversal must still respect RLS
        AssertNoCrossTenantDataBreach(
            reachableList,
            TenantA,
            e => e.TenantId,
            "recursive graph traversal");
    }

    [Fact]
    public async Task Bidirectional_Traversal_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        var entityId = TenantBData.EntityIds[TenantBData.EntityIds.Count / 2];

        // Find all entities connected in either direction
        var connected = await conn.QueryAsync<EntityRecord>(@"
            SELECT e.id, e.tenant_id, e.name, e.entity_type
            FROM entities e
            WHERE e.id IN (
                SELECT target_entity_id FROM entity_relationships WHERE source_entity_id = @EntityId
                UNION
                SELECT source_entity_id FROM entity_relationships WHERE target_entity_id = @EntityId
            )",
            new { EntityId = entityId });

        var connectedList = connected.ToList();

        AssertNoCrossTenantDataBreach(
            connectedList,
            TenantB,
            e => e.TenantId,
            "bidirectional graph traversal");
    }

    // ==========================================
    // MEMORY-ENTITY LINK ISOLATION TESTS
    // ==========================================

    [Fact]
    public async Task Memory_Entity_Links_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var links = await conn.QueryAsync<MemoryEntityLink>(
            "SELECT tenant_id, memory_id, entity_id, relevance FROM memory_entities");

        var linkList = links.ToList();

        AssertNoCrossTenantDataBreach(
            linkList,
            TenantA,
            l => l.TenantId,
            "memory-entity links");
    }

    [Fact]
    public async Task Find_Entities_For_Memory_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var memoryId = TenantAData.MemoryIds.First();

        var entities = await conn.QueryAsync<EntityRecord>(@"
            SELECT e.id, e.tenant_id, e.name, e.entity_type
            FROM entities e
            JOIN memory_entities me ON e.id = me.entity_id
            WHERE me.memory_id = @MemoryId",
            new { MemoryId = memoryId });

        var entityList = entities.ToList();

        AssertNoCrossTenantDataBreach(
            entityList,
            TenantA,
            e => e.TenantId,
            "find entities for memory");
    }

    [Fact]
    public async Task Find_Memories_For_Entity_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        var entityId = TenantBData.EntityIds.First();

        var memories = await conn.QueryAsync<MemoryRecord>(@"
            SELECT m.id, m.tenant_id, m.content
            FROM memories m
            JOIN memory_entities me ON m.id = me.memory_id
            WHERE me.entity_id = @EntityId",
            new { EntityId = entityId });

        var memoryList = memories.ToList();

        AssertNoCrossTenantDataBreach(
            memoryList,
            TenantB,
            m => m.TenantId,
            "find memories for entity");
    }

    // ==========================================
    // CROSS-TENANT GRAPH ATTACK TESTS
    // ==========================================

    [Fact]
    public async Task CrossTenant_Relationship_Cannot_Reach_Other_Tenant_Entity()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Attempting to create a relationship pointing to TenantB's entity ID
        // should be blocked by the cross-tenant FK protection trigger
        var relationshipId = Guid.CreateVersion7();
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ExecuteAsync(@"
                INSERT INTO entity_relationships
                    (id, tenant_id, source_entity_id, target_entity_id, relationship_type)
                VALUES
                    (@Id, @TenantId, @SourceId, @TargetId, 'CROSS_TENANT_ATTACK')",
                new
                {
                    Id = relationshipId,
                    TenantId = TenantA,
                    SourceId = TenantAData.EntityIds.First(),
                    TargetId = TenantBData.EntityIds.First() // Other tenant's entity ID
                });
        });

        // CRITICAL: Cross-tenant FK must be blocked by trigger
        Assert.Contains("SECURITY VIOLATION", exception.Message);
    }

    [Fact]
    public async Task CrossTenant_MemoryLink_Cannot_Reach_Other_Tenant_Entity()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Attempting to create a link to TenantB's entity ID
        // should be blocked by the cross-tenant FK protection trigger
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ExecuteAsync(@"
                INSERT INTO memory_entities (tenant_id, memory_id, entity_id, relevance)
                VALUES (@TenantId, @MemoryId, @EntityId, 1.0)",
                new
                {
                    TenantId = TenantA,
                    MemoryId = TenantAData.MemoryIds.First(),
                    EntityId = TenantBData.EntityIds.First() // Other tenant's entity ID
                });
        });

        // CRITICAL: Cross-tenant FK must be blocked by trigger
        Assert.Contains("SECURITY VIOLATION", exception.Message);
    }

    [Fact]
    public async Task Graph_Aggregation_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        // Aggregate query across graph
        var stats = await conn.QueryFirstAsync<GraphStats>(@"
            SELECT
                (SELECT COUNT(*) FROM entities) AS entity_count,
                (SELECT COUNT(*) FROM entity_relationships) AS relationship_count,
                (SELECT COUNT(*) FROM memory_entities) AS link_count");

        // Should only count TenantA's data
        Assert.Equal(TenantAData.EntityIds.Count, stats.EntityCount);
        Assert.Equal(TenantAData.RelationshipIds.Count, stats.RelationshipCount);
    }

    // ==========================================
    // SESSION ISOLATION TESTS
    // ==========================================

    [Fact]
    public async Task Conversation_Sessions_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantA);

        var sessions = await conn.QueryAsync<SessionRecord>(
            "SELECT id, tenant_id, session_name FROM conversation_sessions");

        var sessionList = sessions.ToList();

        AssertNoCrossTenantDataBreach(
            sessionList,
            TenantA,
            s => s.TenantId,
            "conversation sessions");

        Assert.Equal(TenantAData.SessionIds.Count, sessionList.Count);
    }

    [Fact]
    public async Task User_Personas_Isolated()
    {
        await using var conn = await ConnectionFactory.OpenAsync(TenantB);

        var personas = await conn.QueryAsync<PersonaRecord>(
            "SELECT id, tenant_id, user_id, attribute_key FROM user_personas");

        var personaList = personas.ToList();

        AssertNoCrossTenantDataBreach(
            personaList,
            TenantB,
            p => p.TenantId,
            "user personas");

        Assert.Equal(TenantBData.PersonaIds.Count, personaList.Count);
    }

    // ==========================================
    // HELPER TYPES
    // ==========================================

    private sealed record EntityRecord(Guid id, Guid tenant_id, string name, string entity_type)
    {
        public Guid TenantId => tenant_id;
    }
    private sealed record RelationshipRecord(Guid id, Guid tenant_id, Guid source_entity_id, Guid target_entity_id, string relationship_type)
    {
        public Guid TenantId => tenant_id;
    }
    private sealed record MemoryEntityLink(Guid tenant_id, Guid memory_id, Guid entity_id, float relevance)
    {
        public Guid TenantId => tenant_id;
    }
    private sealed record MemoryRecord(Guid id, Guid tenant_id, string content)
    {
        public Guid TenantId => tenant_id;
    }
    private sealed record SessionRecord(Guid id, Guid tenant_id, string session_name)
    {
        public Guid TenantId => tenant_id;
    }
    private sealed record PersonaRecord(Guid id, Guid tenant_id, string user_id, string attribute_key)
    {
        public Guid TenantId => tenant_id;
    }
    private sealed record GraphStats(long entity_count, long relationship_count, long link_count)
    {
        public long EntityCount => entity_count;
        public long RelationshipCount => relationship_count;
    }
}
