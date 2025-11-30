using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Tests that verify tenant isolation via RLS (Row-Level Security).
/// These tests ensure that:
/// - Tenant A cannot read Tenant B's memories
/// - Queries without tenant context fail or return empty
/// - Writes without proper tenant context are rejected
/// </summary>
[Collection("PostgreSQL")]
public class TenantIsolationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private string _connectionString = "";
    private string _appUserConnectionString = ""; // Non-superuser connection for RLS testing

    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public TenantIsolationTests()
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

        // Build app_user connection string (non-superuser for RLS testing)
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Username = "app_user",
            Password = "app_user"
        };
        _appUserConnectionString = builder.ConnectionString;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Apply schema with RLS
        await ApplySchema(conn);

        // Create test tenants
        await conn.ExecuteAsync(@"
            INSERT INTO tenants (id, name, slug, status) VALUES
            (@TenantA, 'Tenant A', 'tenant-a', 'active'),
            (@TenantB, 'Tenant B', 'tenant-b', 'active')
            ON CONFLICT (id) DO NOTHING",
            new { TenantA, TenantB });
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    private static async Task ApplySchema(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");

        await conn.ExecuteAsync(@"
            -- Tenants table
            CREATE TABLE IF NOT EXISTS tenants (
                id UUID PRIMARY KEY,
                name TEXT NOT NULL,
                slug TEXT UNIQUE,
                status TEXT DEFAULT 'active',
                created_at TIMESTAMPTZ DEFAULT NOW()
            );

            -- Memories table with tenant_id
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

            -- Create a non-superuser role for testing RLS
            -- Superusers ALWAYS bypass RLS, even with FORCE ROW LEVEL SECURITY
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
                    CREATE ROLE app_user WITH LOGIN PASSWORD 'app_user';
                END IF;
            END
            $$;

            -- Grant necessary permissions to app_user
            GRANT USAGE ON SCHEMA public TO app_user;
            GRANT ALL ON ALL TABLES IN SCHEMA public TO app_user;
            GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO app_user;
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO app_user;

            -- RLS function to set tenant context
            CREATE OR REPLACE FUNCTION set_tenant_context(p_tenant_id UUID)
            RETURNS VOID AS $$
            BEGIN
                PERFORM set_config('app.tenant_id', p_tenant_id::text, false);
            END;
            $$ LANGUAGE plpgsql;

            -- RLS function to get tenant context
            CREATE OR REPLACE FUNCTION get_tenant_context()
            RETURNS UUID AS $$
            BEGIN
                RETURN NULLIF(current_setting('app.tenant_id', true), '')::uuid;
            EXCEPTION WHEN OTHERS THEN
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql STABLE;

            -- GUARDRAIL: Fails if tenant context not set
            CREATE OR REPLACE FUNCTION require_tenant_context()
            RETURNS BOOLEAN AS $$
            DECLARE
                v_tenant_id UUID;
            BEGIN
                v_tenant_id := NULLIF(current_setting('app.tenant_id', true), '')::uuid;
                IF v_tenant_id IS NULL THEN
                    RAISE EXCEPTION 'SECURITY VIOLATION: tenant_id not set';
                END IF;
                RETURN TRUE;
            EXCEPTION
                WHEN invalid_text_representation THEN
                    RAISE EXCEPTION 'SECURITY VIOLATION: Invalid tenant_id format';
            END;
            $$ LANGUAGE plpgsql STABLE;

            -- Enable RLS
            ALTER TABLE memories ENABLE ROW LEVEL SECURITY;
            -- FORCE RLS for table owner - but note: superusers STILL bypass this
            ALTER TABLE memories FORCE ROW LEVEL SECURITY;

            -- Create RLS policy with guardrail
            DROP POLICY IF EXISTS tenant_isolation_memories ON memories;
            CREATE POLICY tenant_isolation_memories ON memories
                FOR ALL
                USING (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid)
                WITH CHECK (require_tenant_context() AND tenant_id = current_setting('app.tenant_id', true)::uuid);

            -- Index for performance
            CREATE INDEX IF NOT EXISTS idx_memories_tenant_id ON memories(tenant_id);
        ");
    }

    [Fact]
    public async Task TenantA_CannotRead_TenantB_Memories()
    {
        // Arrange: Create memory as Tenant A (using app_user to enforce RLS)
        await using var connA = new NpgsqlConnection(_appUserConnectionString);
        await connA.OpenAsync();
        await connA.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantA });

        var memoryId = Guid.CreateVersion7();
        await connA.ExecuteAsync(@"
            INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
            VALUES (@Id, @TenantId, 'Secret data for Tenant A', NOW(), NOW())",
            new { Id = memoryId, TenantId = TenantA });

        // Verify Tenant A can see their memory
        var countA = await connA.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM memories WHERE id = @Id",
            new { Id = memoryId });
        Assert.Equal(1, countA);

        // Act: Try to read as Tenant B (using app_user to enforce RLS)
        await using var connB = new NpgsqlConnection(_appUserConnectionString);
        await connB.OpenAsync();
        await connB.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantB });

        var countB = await connB.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM memories WHERE id = @Id",
            new { Id = memoryId });

        // Assert: Tenant B should see 0 rows (RLS filters them out)
        Assert.Equal(0, countB);
    }

    [Fact]
    public async Task TenantB_CannotUpdate_TenantA_Memories()
    {
        // Arrange: Create memory as Tenant A (using app_user to enforce RLS)
        await using var connA = new NpgsqlConnection(_appUserConnectionString);
        await connA.OpenAsync();
        await connA.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantA });

        var memoryId = Guid.CreateVersion7();
        await connA.ExecuteAsync(@"
            INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
            VALUES (@Id, @TenantId, 'Original content', NOW(), NOW())",
            new { Id = memoryId, TenantId = TenantA });

        // Act: Try to update as Tenant B (using app_user to enforce RLS)
        await using var connB = new NpgsqlConnection(_appUserConnectionString);
        await connB.OpenAsync();
        await connB.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantB });

        var rowsAffected = await connB.ExecuteAsync(
            "UPDATE memories SET content = 'Hacked!' WHERE id = @Id",
            new { Id = memoryId });

        // Assert: No rows should be updated (RLS blocks access)
        Assert.Equal(0, rowsAffected);

        // Verify original content is unchanged
        await connA.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantA });
        var content = await connA.ExecuteScalarAsync<string>(
            "SELECT content FROM memories WHERE id = @Id",
            new { Id = memoryId });
        Assert.Equal("Original content", content);
    }

    [Fact]
    public async Task TenantB_CannotDelete_TenantA_Memories()
    {
        // Arrange: Create memory as Tenant A (using app_user to enforce RLS)
        await using var connA = new NpgsqlConnection(_appUserConnectionString);
        await connA.OpenAsync();
        await connA.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantA });

        var memoryId = Guid.CreateVersion7();
        await connA.ExecuteAsync(@"
            INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
            VALUES (@Id, @TenantId, 'Data to protect', NOW(), NOW())",
            new { Id = memoryId, TenantId = TenantA });

        // Act: Try to delete as Tenant B (using app_user to enforce RLS)
        await using var connB = new NpgsqlConnection(_appUserConnectionString);
        await connB.OpenAsync();
        await connB.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantB });

        var rowsDeleted = await connB.ExecuteAsync(
            "DELETE FROM memories WHERE id = @Id",
            new { Id = memoryId });

        // Assert: No rows should be deleted (RLS blocks access)
        Assert.Equal(0, rowsDeleted);

        // Verify memory still exists for Tenant A
        await connA.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantA });
        var exists = await connA.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM memories WHERE id = @Id)",
            new { Id = memoryId });
        Assert.True(exists);
    }

    [Fact]
    public async Task QueryWithoutTenantContext_FailsWithGuardrail()
    {
        // Arrange: Create memory as Tenant A (using app_user to enforce RLS)
        await using var connSetup = new NpgsqlConnection(_appUserConnectionString);
        await connSetup.OpenAsync();
        await connSetup.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantA });

        await connSetup.ExecuteAsync(@"
            INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
            VALUES (gen_random_uuid(), @TenantId, 'Test memory', NOW(), NOW())",
            new { TenantId = TenantA });

        // Act: Query without setting tenant context (new connection, no context set)
        // Using app_user to enforce RLS
        await using var connNoContext = new NpgsqlConnection(_appUserConnectionString);
        await connNoContext.OpenAsync();
        // Deliberately NOT calling set_tenant_context

        // Assert: Either the guardrail throws an exception OR RLS returns 0 rows
        // Both outcomes are acceptable for security - the key is that no data leaks
        try
        {
            var count = await connNoContext.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
            // If we get here without exception, RLS should have filtered all rows
            // The guardrail function require_tenant_context() raises an exception,
            // so if we get a count it means RLS blocked access (count = 0)
            Assert.Equal(0, count);
        }
        catch (PostgresException ex) when (ex.Message.Contains("tenant_id", StringComparison.OrdinalIgnoreCase) ||
                                           ex.Message.Contains("SECURITY VIOLATION", StringComparison.OrdinalIgnoreCase))
        {
            // Expected: The guardrail raised a security violation
            Assert.Contains("tenant_id", ex.Message.ToLower());
        }
    }

    [Fact]
    public async Task WriteWithMismatchedTenantId_IsRejected()
    {
        // Arrange: Set context for Tenant A (using app_user to enforce RLS)
        await using var conn = new NpgsqlConnection(_appUserConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantA });

        // Act: Try to insert with Tenant B's ID (mismatched)
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.ExecuteAsync(@"
                INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
                VALUES (gen_random_uuid(), @TenantId, 'Sneaky insert', NOW(), NOW())",
                new { TenantId = TenantB }); // Wrong tenant!
        });

        // Assert: RLS WITH CHECK should reject this
        Assert.Contains("row-level security", exception.Message.ToLower());
    }

    [Fact]
    public async Task TenantConnectionFactory_SetsTenantContext()
    {
        // Arrange (using app_user to enforce RLS)
        var tenantContext = new TestTenantContext(TenantA.ToString());
        var factory = new TenantDbConnectionFactory(_appUserConnectionString, tenantContext);

        // Act: Create a memory using the factory
        await using var conn = await factory.OpenNpgsqlAsync();

        var memoryId = Guid.CreateVersion7();
        await conn.ExecuteAsync(@"
            INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
            VALUES (@Id, @TenantId, 'Factory test', NOW(), NOW())",
            new { Id = memoryId, TenantId = TenantA });

        // Verify the context was set by checking we can read it back
        var content = await conn.ExecuteScalarAsync<string>(
            "SELECT content FROM memories WHERE id = @Id",
            new { Id = memoryId });

        // Assert
        Assert.Equal("Factory test", content);
    }

    [Fact]
    public async Task EachTenant_OnlySeesOwnMemories()
    {
        // Arrange: Create memories for both tenants (using app_user to enforce RLS)
        await using var connA = new NpgsqlConnection(_appUserConnectionString);
        await connA.OpenAsync();
        await connA.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantA });

        for (int i = 0; i < 5; i++)
        {
            await connA.ExecuteAsync(@"
                INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
                VALUES (gen_random_uuid(), @TenantId, @Content, NOW(), NOW())",
                new { TenantId = TenantA, Content = $"Tenant A memory {i}" });
        }

        await using var connB = new NpgsqlConnection(_appUserConnectionString);
        await connB.OpenAsync();
        await connB.ExecuteAsync("SELECT set_tenant_context(@TenantId)", new { TenantId = TenantB });

        for (int i = 0; i < 3; i++)
        {
            await connB.ExecuteAsync(@"
                INSERT INTO memories (id, tenant_id, content, created_at, updated_at)
                VALUES (gen_random_uuid(), @TenantId, @Content, NOW(), NOW())",
                new { TenantId = TenantB, Content = $"Tenant B memory {i}" });
        }

        // Act & Assert: Tenant A sees only their 5 memories
        var countA = await connA.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        Assert.Equal(5, countA);

        // Tenant B sees only their 3 memories
        var countB = await connB.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM memories");
        Assert.Equal(3, countB);
    }

    [Fact]
    public async Task PostgresKnowledgeGraphStore_EnforcesTenantIsolation()
    {
        // Arrange: Create store for Tenant A (using app_user to enforce RLS)
        var tenantContextA = new TestTenantContext(TenantA.ToString());
        var storeA = new PostgresKnowledgeGraphStore(_appUserConnectionString, tenantContextA);

        // Create a memory as Tenant A
        var memoryA = new Memory
        {
            Content = "Tenant A's knowledge graph memory",
            Source = "test"
        };
        var memoryIdA = await storeA.CreateMemoryAsync(memoryA);

        // Arrange: Create store for Tenant B (using app_user to enforce RLS)
        var tenantContextB = new TestTenantContext(TenantB.ToString());
        var storeB = new PostgresKnowledgeGraphStore(_appUserConnectionString, tenantContextB);

        // Act: Try to read Tenant A's memory as Tenant B
        var retrievedMemory = await storeB.GetMemoryByIdAsync(memoryIdA);

        // Assert: Tenant B should not see Tenant A's memory
        Assert.Null(retrievedMemory);

        // But Tenant A should see their own memory
        var ownMemory = await storeA.GetMemoryByIdAsync(memoryIdA);
        Assert.NotNull(ownMemory);
        Assert.Equal("Tenant A's knowledge graph memory", ownMemory.Content);
    }

    /// <summary>
    /// Simple test tenant context implementation
    /// </summary>
    private sealed class TestTenantContext : ITenantContext
    {
        public TestTenantContext(string tenantId)
        {
            TenantId = tenantId;
        }

        public string TenantId { get; }
        public string WorkspaceId => "default";
        public string? UserId => null;
        public Guid? SessionId => null;
        public bool IsLabMode { get; }
        public bool AllowPowerMode { get; }
        public IReadOnlyList<string> Scopes { get; }
        public string? UserEmail => null;
        public string? UserRole => null;
        public bool IsRootAdmin => false;
        public bool IsOwner => false;
    }
}
