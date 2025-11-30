using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace SerialMemory.Tests.TenantIsolation;

/// <summary>
/// Base class for tenant isolation tests.
/// Provides:
/// - PostgreSQL testcontainer with pgvector
/// - Two isolated tenants (TenantA, TenantB) with seeded data
/// - Test connection factory with RLS enforcement
/// - Comprehensive test data for all tenant-scoped tables
/// </summary>
public abstract class TenantIsolationTestBase : IAsyncLifetime
{
    protected PostgreSqlContainer PostgresContainer { get; private set; } = null!;
    protected ITestTenantConnectionFactory ConnectionFactory { get; private set; } = null!;
    protected string ConnectionString { get; private set; } = "";

    // Well-known tenant IDs for testing
    protected static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    protected static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // Seeded test data IDs (for verification)
    protected SeededTestData TenantAData { get; private set; } = null!;
    protected SeededTestData TenantBData { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Start PostgreSQL with pgvector
        PostgresContainer = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg17")
            .WithDatabase("testdb")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await PostgresContainer.StartAsync();
        ConnectionString = PostgresContainer.GetConnectionString();

        // First, use superuser to apply schema (which creates app_user role)
        var superuserFactory = new TestTenantConnectionFactory(ConnectionString);
        await using (var conn = await superuserFactory.OpenAsSuperuserAsync())
        {
            // Enable pgvector
            await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");

            // Apply full schema with RLS
            await conn.ExecuteAsync(GetFullSchema());

            // FORCE RLS even for table owner (critical for tests)
            await conn.ExecuteAsync(@"
                ALTER TABLE memories FORCE ROW LEVEL SECURITY;
                ALTER TABLE entities FORCE ROW LEVEL SECURITY;
                ALTER TABLE entity_relationships FORCE ROW LEVEL SECURITY;
                ALTER TABLE memory_entities FORCE ROW LEVEL SECURITY;
                ALTER TABLE user_personas FORCE ROW LEVEL SECURITY;
                ALTER TABLE conversation_sessions FORCE ROW LEVEL SECURITY;
            ");

            // Create app role (non-superuser) for testing RLS enforcement
            await conn.ExecuteAsync(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
                        CREATE ROLE app_user WITH LOGIN PASSWORD 'app_password';
                    END IF;
                END $$;

                GRANT CONNECT ON DATABASE testdb TO app_user;
                GRANT USAGE ON SCHEMA public TO app_user;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_user;
                GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO app_user;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
            ");
        }

        // Now create the real connection factory with app_user for tenant connections
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = "app_user",
            Password = "app_password"
        };
        var appUserConnectionString = builder.ToString();
        ConnectionFactory = new TestTenantConnectionFactory(appUserConnectionString, ConnectionString);

        // Create tenants
        await CreateTenantsAsync();

        // Seed test data for both tenants
        TenantAData = await SeedTenantDataAsync(TenantA, "Tenant A");
        TenantBData = await SeedTenantDataAsync(TenantB, "Tenant B");
    }

    public async Task DisposeAsync()
    {
        await PostgresContainer.DisposeAsync();
    }

    private async Task CreateTenantsAsync()
    {
        await using var conn = await ConnectionFactory.OpenAsSuperuserAsync();

        await conn.ExecuteAsync(@"
            INSERT INTO tenants (id, name, slug, status) VALUES
            (@TenantA, 'Tenant A', 'tenant-a', 'active'),
            (@TenantB, 'Tenant B', 'tenant-b', 'active')
            ON CONFLICT (id) DO NOTHING",
            new { TenantA, TenantB });
    }

    private async Task<SeededTestData> SeedTenantDataAsync(Guid tenantId, string tenantName)
    {
        var data = new SeededTestData();
        await using var conn = await ConnectionFactory.OpenAsync(tenantId);

        // 1. Create conversation sessions
        for (int i = 0; i < 3; i++)
        {
            var sessionId = Guid.CreateVersion7();
            await conn.ExecuteAsync(@"
                INSERT INTO conversation_sessions (id, tenant_id, session_name, started_at, client_type)
                VALUES (@Id, @TenantId, @Name, NOW(), 'test')",
                new { Id = sessionId, TenantId = tenantId, Name = $"{tenantName} Session {i}" });
            data.SessionIds.Add(sessionId);
        }

        // 2. Create memories with embeddings (random 384-dim vectors for pgvector tests)
        var random = new Random(tenantId.GetHashCode()); // Deterministic per tenant
        for (int i = 0; i < 10; i++)
        {
            var memoryId = Guid.CreateVersion7();
            var embedding = GenerateRandomEmbedding(random, 384);

            await conn.ExecuteAsync(@"
                INSERT INTO memories (id, tenant_id, content, embedding, created_at, updated_at, source, conversation_session_id)
                VALUES (@Id, @TenantId, @Content, @Embedding::vector, NOW(), NOW(), 'test', @SessionId)",
                new
                {
                    Id = memoryId,
                    TenantId = tenantId,
                    Content = $"{tenantName} Memory {i}: Secret data that should never leak to other tenants",
                    Embedding = $"[{string.Join(",", embedding)}]",
                    SessionId = data.SessionIds[i % data.SessionIds.Count]
                });
            data.MemoryIds.Add(memoryId);
            data.Embeddings[memoryId] = embedding;
        }

        // 3. Create entities
        var entityTypes = new[] { "PERSON", "ORG", "PROJECT", "CONCEPT" };
        for (int i = 0; i < 8; i++)
        {
            var entityId = Guid.CreateVersion7();
            await conn.ExecuteAsync(@"
                INSERT INTO entities (id, tenant_id, name, entity_type, canonical_name, first_seen_memory_id)
                VALUES (@Id, @TenantId, @Name, @Type, @CanonicalName, @MemoryId)",
                new
                {
                    Id = entityId,
                    TenantId = tenantId,
                    Name = $"{tenantName} Entity {i}",
                    Type = entityTypes[i % entityTypes.Length],
                    CanonicalName = $"{tenantName.ToLower()}_entity_{i}",
                    MemoryId = data.MemoryIds[i % data.MemoryIds.Count]
                });
            data.EntityIds.Add(entityId);
        }

        // 4. Create entity relationships (graph edges)
        for (int i = 0; i < data.EntityIds.Count - 1; i++)
        {
            var relationshipId = Guid.CreateVersion7();
            await conn.ExecuteAsync(@"
                INSERT INTO entity_relationships (id, tenant_id, source_entity_id, target_entity_id, relationship_type, confidence)
                VALUES (@Id, @TenantId, @SourceId, @TargetId, @RelType, @Confidence)",
                new
                {
                    Id = relationshipId,
                    TenantId = tenantId,
                    SourceId = data.EntityIds[i],
                    TargetId = data.EntityIds[i + 1],
                    RelType = "CONNECTED_TO",
                    Confidence = 0.9f
                });
            data.RelationshipIds.Add(relationshipId);
        }

        // 5. Create memory-entity links
        for (int i = 0; i < data.MemoryIds.Count; i++)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO memory_entities (tenant_id, memory_id, entity_id, relevance)
                VALUES (@TenantId, @MemoryId, @EntityId, @Relevance)",
                new
                {
                    TenantId = tenantId,
                    MemoryId = data.MemoryIds[i],
                    EntityId = data.EntityIds[i % data.EntityIds.Count],
                    Relevance = 0.8f
                });
        }

        // 6. Create user personas
        var attributes = new[] { ("preference", "editor", "vscode"), ("skill", "language", "csharp"), ("goal", "project", "memory_system") };
        foreach (var (attrType, attrKey, attrValue) in attributes)
        {
            var personaId = Guid.CreateVersion7();
            await conn.ExecuteAsync(@"
                INSERT INTO user_personas (id, tenant_id, user_id, attribute_type, attribute_key, attribute_value)
                VALUES (@Id, @TenantId, 'default_user', @AttrType, @AttrKey, @AttrValue)",
                new
                {
                    Id = personaId,
                    TenantId = tenantId,
                    AttrType = attrType,
                    AttrKey = $"{tenantName.ToLower()}_{attrKey}",
                    AttrValue = $"{tenantName}_{attrValue}"
                });
            data.PersonaIds.Add(personaId);
        }

        return data;
    }

    private static float[] GenerateRandomEmbedding(Random random, int dimensions)
    {
        var embedding = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            embedding[i] = (float)(random.NextDouble() * 2 - 1); // -1 to 1
        }

        // Normalize
        var magnitude = (float)Math.Sqrt(embedding.Sum(x => x * x));
        for (int i = 0; i < dimensions; i++)
        {
            embedding[i] /= magnitude;
        }

        return embedding;
    }

    private static string GetFullSchema() => @"
        -- Tenants table (not RLS protected - system table)
        CREATE TABLE IF NOT EXISTS tenants (
            id UUID PRIMARY KEY,
            name TEXT NOT NULL,
            slug TEXT UNIQUE,
            status TEXT DEFAULT 'active',
            created_at TIMESTAMPTZ DEFAULT NOW()
        );

        -- Memories table
        CREATE TABLE IF NOT EXISTS memories (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            tenant_id UUID NOT NULL,
            content TEXT NOT NULL,
            embedding vector(384),
            created_at TIMESTAMP NOT NULL DEFAULT NOW(),
            updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
            source TEXT,
            conversation_session_id UUID,
            metadata JSONB,
            content_tsvector tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
        );

        -- Entities table
        CREATE TABLE IF NOT EXISTS entities (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            tenant_id UUID NOT NULL,
            name TEXT NOT NULL,
            entity_type TEXT NOT NULL,
            canonical_name TEXT,
            created_at TIMESTAMP NOT NULL DEFAULT NOW(),
            first_seen_memory_id UUID,
            metadata JSONB,
            UNIQUE(tenant_id, name, entity_type)
        );

        -- Entity relationships (graph edges)
        CREATE TABLE IF NOT EXISTS entity_relationships (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            tenant_id UUID NOT NULL,
            source_entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
            target_entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
            relationship_type TEXT NOT NULL,
            confidence REAL DEFAULT 1.0,
            created_at TIMESTAMP NOT NULL DEFAULT NOW(),
            first_seen_memory_id UUID,
            metadata JSONB,
            UNIQUE(tenant_id, source_entity_id, target_entity_id, relationship_type)
        );

        -- Memory-entity links
        CREATE TABLE IF NOT EXISTS memory_entities (
            tenant_id UUID NOT NULL,
            memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            entity_id UUID NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
            relevance REAL DEFAULT 1.0,
            PRIMARY KEY (memory_id, entity_id)
        );

        -- User personas
        CREATE TABLE IF NOT EXISTS user_personas (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            tenant_id UUID NOT NULL,
            user_id TEXT NOT NULL DEFAULT 'default_user',
            attribute_type TEXT NOT NULL,
            attribute_key TEXT NOT NULL,
            attribute_value TEXT NOT NULL,
            confidence REAL DEFAULT 1.0,
            created_at TIMESTAMP NOT NULL DEFAULT NOW(),
            updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
            source_memory_id UUID,
            UNIQUE(tenant_id, user_id, attribute_type, attribute_key)
        );

        -- Conversation sessions
        CREATE TABLE IF NOT EXISTS conversation_sessions (
            id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            tenant_id UUID NOT NULL,
            session_name TEXT,
            started_at TIMESTAMP NOT NULL DEFAULT NOW(),
            ended_at TIMESTAMP,
            client_type TEXT,
            metadata JSONB
        );

        -- Indexes
        CREATE INDEX IF NOT EXISTS idx_memories_tenant ON memories(tenant_id);
        CREATE INDEX IF NOT EXISTS idx_memories_embedding ON memories USING ivfflat (embedding vector_cosine_ops) WITH (lists = 10);
        CREATE INDEX IF NOT EXISTS idx_entities_tenant ON entities(tenant_id);
        CREATE INDEX IF NOT EXISTS idx_entity_relationships_tenant ON entity_relationships(tenant_id);
        CREATE INDEX IF NOT EXISTS idx_memory_entities_tenant ON memory_entities(tenant_id);
        CREATE INDEX IF NOT EXISTS idx_user_personas_tenant ON user_personas(tenant_id);
        CREATE INDEX IF NOT EXISTS idx_conversation_sessions_tenant ON conversation_sessions(tenant_id);

        -- RLS Functions
        CREATE OR REPLACE FUNCTION set_tenant_context(p_tenant_id UUID)
        RETURNS VOID AS $$
        BEGIN
            PERFORM set_config('app.tenant_id', p_tenant_id::text, false);
        END;
        $$ LANGUAGE plpgsql;

        CREATE OR REPLACE FUNCTION require_tenant_context()
        RETURNS BOOLEAN AS $$
        DECLARE
            v_tenant_id UUID;
        BEGIN
            v_tenant_id := NULLIF(current_setting('app.tenant_id', true), '')::uuid;
            IF v_tenant_id IS NULL THEN
                RAISE EXCEPTION 'SECURITY VIOLATION: tenant_id not set. Call set_tenant_context() before querying.';
            END IF;
            RETURN TRUE;
        EXCEPTION
            WHEN invalid_text_representation THEN
                RAISE EXCEPTION 'SECURITY VIOLATION: Invalid tenant_id format';
        END;
        $$ LANGUAGE plpgsql STABLE;

        -- Enable and FORCE RLS (FORCE ensures even table owners are subject to RLS)
        ALTER TABLE memories ENABLE ROW LEVEL SECURITY;
        ALTER TABLE memories FORCE ROW LEVEL SECURITY;
        ALTER TABLE entities ENABLE ROW LEVEL SECURITY;
        ALTER TABLE entities FORCE ROW LEVEL SECURITY;
        ALTER TABLE entity_relationships ENABLE ROW LEVEL SECURITY;
        ALTER TABLE entity_relationships FORCE ROW LEVEL SECURITY;
        ALTER TABLE memory_entities ENABLE ROW LEVEL SECURITY;
        ALTER TABLE memory_entities FORCE ROW LEVEL SECURITY;
        ALTER TABLE user_personas ENABLE ROW LEVEL SECURITY;
        ALTER TABLE user_personas FORCE ROW LEVEL SECURITY;
        ALTER TABLE conversation_sessions ENABLE ROW LEVEL SECURITY;
        ALTER TABLE conversation_sessions FORCE ROW LEVEL SECURITY;

        -- RLS Policies with guardrail (for non-superusers OR when tenant context is set)
        -- Note: Superuser with FORCE RLS still needs a matching policy
        CREATE POLICY tenant_isolation_memories ON memories FOR ALL
            USING (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            )
            WITH CHECK (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            );

        CREATE POLICY tenant_isolation_entities ON entities FOR ALL
            USING (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            )
            WITH CHECK (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            );

        CREATE POLICY tenant_isolation_entity_relationships ON entity_relationships FOR ALL
            USING (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            )
            WITH CHECK (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            );

        CREATE POLICY tenant_isolation_memory_entities ON memory_entities FOR ALL
            USING (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            )
            WITH CHECK (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            );

        CREATE POLICY tenant_isolation_user_personas ON user_personas FOR ALL
            USING (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            )
            WITH CHECK (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            );

        CREATE POLICY tenant_isolation_conversation_sessions ON conversation_sessions FOR ALL
            USING (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            )
            WITH CHECK (
                CASE
                    WHEN current_setting('app.bypass_rls', true) = 'true' THEN true
                    ELSE require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid
                END
            );

        -- Cross-tenant FK protection triggers (FK constraints bypass RLS, so we need triggers)
        CREATE OR REPLACE FUNCTION check_entity_relationship_tenant()
        RETURNS TRIGGER AS $$
        DECLARE
            source_tenant UUID;
            target_tenant UUID;
        BEGIN
            SELECT tenant_id INTO source_tenant FROM entities WHERE id = NEW.source_entity_id;
            SELECT tenant_id INTO target_tenant FROM entities WHERE id = NEW.target_entity_id;

            IF source_tenant IS NULL THEN
                RAISE EXCEPTION 'SECURITY VIOLATION: source_entity_id % does not exist or is not visible', NEW.source_entity_id;
            END IF;

            IF target_tenant IS NULL THEN
                RAISE EXCEPTION 'SECURITY VIOLATION: target_entity_id % does not exist or is not visible', NEW.target_entity_id;
            END IF;

            IF source_tenant != NEW.tenant_id THEN
                RAISE EXCEPTION 'SECURITY VIOLATION: source_entity belongs to different tenant';
            END IF;

            IF target_tenant != NEW.tenant_id THEN
                RAISE EXCEPTION 'SECURITY VIOLATION: target_entity belongs to different tenant';
            END IF;

            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;

        CREATE OR REPLACE FUNCTION check_memory_entity_tenant()
        RETURNS TRIGGER AS $$
        DECLARE
            memory_tenant UUID;
            entity_tenant UUID;
        BEGIN
            SELECT tenant_id INTO memory_tenant FROM memories WHERE id = NEW.memory_id;
            SELECT tenant_id INTO entity_tenant FROM entities WHERE id = NEW.entity_id;

            IF memory_tenant IS NULL OR entity_tenant IS NULL THEN
                RAISE EXCEPTION 'SECURITY VIOLATION: Referenced memory or entity does not exist or is not visible';
            END IF;

            IF memory_tenant != NEW.tenant_id OR entity_tenant != NEW.tenant_id THEN
                RAISE EXCEPTION 'SECURITY VIOLATION: Cross-tenant memory-entity link attempted';
            END IF;

            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_entity_relationship_tenant_check ON entity_relationships;
        CREATE TRIGGER trg_entity_relationship_tenant_check
            BEFORE INSERT OR UPDATE ON entity_relationships
            FOR EACH ROW EXECUTE FUNCTION check_entity_relationship_tenant();

        DROP TRIGGER IF EXISTS trg_memory_entity_tenant_check ON memory_entities;
        CREATE TRIGGER trg_memory_entity_tenant_check
            BEFORE INSERT OR UPDATE ON memory_entities
            FOR EACH ROW EXECUTE FUNCTION check_memory_entity_tenant();
    ";

    /// <summary>
    /// Asserts that a cross-tenant data breach has occurred.
    /// Fails the test with detailed information about the breach.
    /// </summary>
    protected static void AssertNoCrossTenantDataBreach<T>(
        IEnumerable<T> results,
        Guid expectedTenantId,
        Func<T, Guid> tenantIdSelector,
        string operationDescription)
    {
        var breaches = results
            .Where(r => tenantIdSelector(r) != expectedTenantId)
            .ToList();

        if (breaches.Count > 0)
        {
            var breachTenants = breaches
                .Select(tenantIdSelector)
                .Distinct()
                .ToList();

            throw new TenantIsolationBreachException(
                expectedTenantId,
                breachTenants,
                operationDescription,
                breaches.Count);
        }
    }

    /// <summary>
    /// Verifies that a result set contains ONLY data belonging to the expected tenant.
    /// </summary>
    protected static void AssertTenantDataOnly<T>(
        IEnumerable<T> results,
        Guid expectedTenantId,
        Func<T, Guid> tenantIdSelector)
    {
        foreach (var result in results)
        {
            var actualTenantId = tenantIdSelector(result);
            Assert.Equal(expectedTenantId, actualTenantId);
        }
    }
}

/// <summary>
/// Container for seeded test data per tenant.
/// </summary>
public sealed class SeededTestData
{
    public List<Guid> MemoryIds { get; } = new();
    public List<Guid> EntityIds { get; } = new();
    public List<Guid> RelationshipIds { get; } = new();
    public List<Guid> SessionIds { get; } = new();
    public List<Guid> PersonaIds { get; } = new();
    public Dictionary<Guid, float[]> Embeddings { get; } = new();
}

/// <summary>
/// Exception thrown when a tenant isolation breach is detected.
/// This causes the build to fail.
/// </summary>
public sealed class TenantIsolationBreachException : Exception
{
    public Guid ExpectedTenantId { get; }
    public IReadOnlyList<Guid> BreachedTenantIds { get; }
    public string Operation { get; }
    public int BreachCount { get; }

    public TenantIsolationBreachException(
        Guid expectedTenantId,
        IReadOnlyList<Guid> breachedTenantIds,
        string operation,
        int breachCount)
        : base($"CRITICAL SECURITY BREACH: Tenant isolation violated during '{operation}'. " +
               $"Expected tenant {expectedTenantId}, but {breachCount} record(s) from tenant(s) " +
               $"{string.Join(", ", breachedTenantIds)} were exposed.")
    {
        ExpectedTenantId = expectedTenantId;
        BreachedTenantIds = breachedTenantIds;
        Operation = operation;
        BreachCount = breachCount;
    }
}
