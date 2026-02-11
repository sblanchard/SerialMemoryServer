using System.Dynamic;
using Dapper;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using static SerialMemory.Core.Services.DebugFileLogger;

namespace SerialMemory.Infrastructure;

/// <summary>
/// PostgreSQL implementation of the knowledge graph store using pgvector for semantic search.
/// All operations are scoped to the current tenant via RLS (Row-Level Security).
/// </summary>
/// <remarks>
/// IMPORTANT: This class relies SOLELY on RLS for tenant isolation.
/// All connections are opened via ITenantDbConnectionFactory which sets app.tenant_id.
/// Queries do NOT manually filter by tenant_id - RLS policies handle this automatically.
/// </remarks>
public class PostgresKnowledgeGraphStore : IKnowledgeGraphStore
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new tenant-scoped PostgreSQL knowledge graph store.
    /// </summary>
    /// <param name="connectionFactory">Factory for creating tenant-scoped connections</param>
    /// <param name="tenantContext">Tenant context for multi-tenant isolation</param>
    public PostgresKnowledgeGraphStore(ITenantDbConnectionFactory connectionFactory, ITenantContext tenantContext)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    }

    /// <summary>
    /// Creates a new PostgreSQL knowledge graph store from a connection string.
    /// For self-hosted/single-tenant mode using the default tenant.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    public PostgresKnowledgeGraphStore(string connectionString)
        : this(
            new TenantDbConnectionFactory(connectionString, new FixedTenantContext(DefaultTenantId, "default")),
            new FixedTenantContext(DefaultTenantId, "default"))
    {
    }

    /// <summary>
    /// Default tenant ID for self-hosted single-tenant mode.
    /// Uses a fixed non-empty GUID to satisfy RLS requirements.
    /// </summary>
    private const string DefaultTenantId = "01945c6e-5b9a-7000-8000-000000000001";

    /// <summary>
    /// Creates a new PostgreSQL knowledge graph store from a connection string with tenant context.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="tenantContext">Tenant context for multi-tenant isolation</param>
    public PostgresKnowledgeGraphStore(string connectionString, ITenantContext tenantContext)
        : this(new TenantDbConnectionFactory(connectionString, tenantContext), tenantContext)
    {
    }

    /// <summary>
    /// Gets the current tenant ID as a UUID.
    /// </summary>
    private Guid TenantId => Guid.Parse(_tenantContext.TenantId);

    /// <summary>
    /// Gets the current workspace ID from tenant context.
    /// </summary>
    private string WorkspaceId => _tenantContext.WorkspaceId;

    /// <summary>
    /// Opens a connection with tenant context set for RLS enforcement.
    /// Returns NpgsqlConnection for pgvector operations.
    /// </summary>
    private Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => ((TenantDbConnectionFactory)_connectionFactory).OpenNpgsqlAsync(cancellationToken);

    #region Memory Operations

    public async Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        // Capture tenant ID and validate before SQL
        var tenantId = TenantId;
        Log("CreateMemory", $"TenantContext.TenantId={_tenantContext.TenantId}, Parsed TenantId={tenantId}");

        // Note: Guid.Empty (00000000-...) is valid in SelfHosted mode

        const string sql = """

                                       INSERT INTO memories (id, tenant_id, workspace_id, content, embedding, source, conversation_session_id, metadata, memory_type)
                                       VALUES (@Id, @TenantId, @WorkspaceId, @Content, @Embedding, @Source, @SessionId, @Metadata::jsonb, @MemoryType)
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        Log("CreateMemory", $"Connection opened, about to INSERT with tenantId={tenantId}");

        // Use NpgsqlCommand directly to properly handle Vector type
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@Id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter("@TenantId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter("@WorkspaceId", NpgsqlTypes.NpgsqlDbType.Text) { Value = WorkspaceId });
        cmd.Parameters.Add(new NpgsqlParameter("@Content", NpgsqlTypes.NpgsqlDbType.Text) { Value = memory.Content });
        cmd.Parameters.AddWithValue("@Embedding", memory.Embedding != null ? new Vector(memory.Embedding) : DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("@Source", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)memory.Source ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@SessionId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)memory.ConversationSessionId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@Metadata", NpgsqlTypes.NpgsqlDbType.Text) { Value = memory.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(memory.Metadata) : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@MemoryType", NpgsqlTypes.NpgsqlDbType.Text) { Value = memory.MemoryType ?? "knowledge" });

        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            Log("CreateMemory", $"INSERT succeeded for memory id={id}");
        }
        catch (Exception ex)
        {
            Log("CreateMemory", $"ERROR: INSERT failed: {ex.Message}");
            throw;
        }

        return id;
    }

    public async Task UpdateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE memories SET content = @Content, embedding = @Embedding, source = @Source,
                   metadata = @Metadata::jsonb, updated_at = NOW()
            WHERE id = @Id AND tenant_id = @TenantId
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("@Id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = memory.Id });
        cmd.Parameters.Add(new NpgsqlParameter("@TenantId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = TenantId });
        cmd.Parameters.Add(new NpgsqlParameter("@Content", NpgsqlTypes.NpgsqlDbType.Text) { Value = memory.Content });
        cmd.Parameters.AddWithValue("@Embedding", memory.Embedding != null ? new Vector(memory.Embedding) : DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("@Source", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)memory.Source ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@Metadata", NpgsqlTypes.NpgsqlDbType.Text) { Value = memory.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(memory.Metadata) : DBNull.Value });
        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (rowsAffected == 0)
            throw new InvalidOperationException($"Memory {memory.Id} not found or not owned by tenant");
    }

    public async Task<Memory?> GetMemoryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = "SELECT * FROM memories WHERE id = @Id";

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var result = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
            sql,
            new { Id = id },
            cancellationToken: cancellationToken
        ));

        return result != null ? MapToMemory(result) : null;
    }

    public async Task<List<Memory>> GetRecentMemoriesAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT id, content, created_at, updated_at, source, conversation_session_id, metadata
                                       FROM memories
                                       ORDER BY created_at DESC
                                       LIMIT @Limit
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Limit = limit },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToMemory).ToList();
    }

    public async Task<List<Memory>> SearchMemoriesByEmbeddingAsync(
        float[] queryEmbedding,
        int limit = 10,
        float threshold = 0.7f,
        string? memoryType = null,
        CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        var sql = """
                  SELECT
                      id, content, created_at, updated_at, source, conversation_session_id, metadata, memory_type,
                      1 - (embedding <=> @QueryEmbedding) as similarity
                  FROM memories
                  WHERE 1 - (embedding <=> @QueryEmbedding) > @Threshold
                  """ +
                  (memoryType != null ? " AND memory_type = @MemoryType" : "") +
                  """

                  ORDER BY embedding <=> @QueryEmbedding
                  LIMIT @Limit
                  """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        // Use NpgsqlCommand directly to properly handle Vector type
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@QueryEmbedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("@Threshold", threshold);
        cmd.Parameters.AddWithValue("@Limit", limit);
        if (memoryType != null)
            cmd.Parameters.AddWithValue("@MemoryType", memoryType);

        var results = new List<dynamic>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dynamic row = new ExpandoObject();
            var dict = (IDictionary<string, object?>)row;
            dict["id"] = reader.GetGuid(0);
            dict["content"] = reader.GetString(1);
            dict["created_at"] = reader.GetDateTime(2);
            dict["updated_at"] = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            dict["source"] = reader.IsDBNull(4) ? null : reader.GetString(4);
            dict["conversation_session_id"] = reader.IsDBNull(5) ? null : reader.GetGuid(5);
            dict["metadata"] = reader.IsDBNull(6) ? null : reader.GetString(6);
            dict["memory_type"] = reader.IsDBNull(7) ? "knowledge" : reader.GetString(7);
            dict["similarity"] = reader.GetFloat(8);
            results.Add(row);
        }

        return results.Select(MapToMemory).ToList();
    }

    public async Task<List<Memory>> SearchMemoriesByTextAsync(
        string query,
        int limit = 10,
        string? memoryType = null,
        CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        var sql = """
                  SELECT
                      id, content, created_at, updated_at, source, conversation_session_id, metadata, memory_type,
                      ts_rank(content_tsvector, plainto_tsquery('english', @Query)) as rank
                  FROM memories
                  WHERE content_tsvector @@ plainto_tsquery('english', @Query)
                  """ +
                  (memoryType != null ? " AND memory_type = @MemoryType" : "") +
                  """

                  ORDER BY rank DESC
                  LIMIT @Limit
                  """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Query = query, Limit = limit, MemoryType = memoryType },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToMemory).ToList();
    }

    #endregion

    #region Entity Operations

    public async Task<Guid> CreateEntityAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        // Unique constraint is now (tenant_id, name, entity_type)
        const string sql = """

                                       INSERT INTO entities (id, tenant_id, name, entity_type, canonical_name, first_seen_memory_id, metadata)
                                       VALUES (@Id, @TenantId, @Name, @EntityType, @CanonicalName, @FirstSeenMemoryId, @Metadata::jsonb)
                                       ON CONFLICT (tenant_id, name, entity_type) DO UPDATE SET
                                           canonical_name = COALESCE(EXCLUDED.canonical_name, entities.canonical_name),
                                           metadata = COALESCE(EXCLUDED.metadata, entities.metadata)
                                       RETURNING id
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var returnedId = await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                TenantId,
                entity.Name,
                entity.EntityType,
                entity.CanonicalName,
                entity.FirstSeenMemoryId,
                Metadata = entity.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(entity.Metadata) : null
            },
            cancellationToken: cancellationToken
        ));

        return returnedId;
    }

    public async Task<Entity?> GetEntityByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = "SELECT * FROM entities WHERE id = @Id";

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var result = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
            sql,
            new { Id = id },
            cancellationToken: cancellationToken
        ));

        return result != null ? MapToEntity(result) : null;
    }

    public async Task<List<Entity>> GetEntitiesForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT e.*, me.relevance
                                       FROM entities e
                                       JOIN memory_entities me ON e.id = me.entity_id
                                       WHERE me.memory_id = @MemoryId
                                       ORDER BY me.relevance DESC
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { MemoryId = memoryId },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToEntity).ToList();
    }

    public async Task<Dictionary<Guid, List<Entity>>> GetEntitiesForMemoriesAsync(List<Guid> memoryIds, CancellationToken cancellationToken = default)
    {
        var result = memoryIds.ToDictionary(id => id, _ => new List<Entity>());

        if (memoryIds.Count == 0)
            return result;

        const string sql = """
            SELECT e.*, me.memory_id, me.relevance
            FROM entities e
            JOIN memory_entities me ON e.id = me.entity_id
            WHERE me.memory_id = ANY(@MemoryIds)
            ORDER BY me.relevance DESC
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { MemoryIds = memoryIds },
            cancellationToken: cancellationToken
        ));

        foreach (var row in rows)
        {
            Guid memoryId = row.memory_id;
            if (result.TryGetValue(memoryId, out var entities))
            {
                entities.Add(MapToEntity(row));
            }
        }

        return result;
    }

    public async Task LinkMemoryToEntityAsync(
        Guid memoryId,
        Guid entityId,
        float relevance = 1.0f,
        CancellationToken cancellationToken = default)
    {
        const string sql = """

                                       INSERT INTO memory_entities (tenant_id, workspace_id, memory_id, entity_id, relevance)
                                       VALUES (@TenantId, @WorkspaceId, @MemoryId, @EntityId, @Relevance)
                                       ON CONFLICT (memory_id, entity_id) DO UPDATE SET relevance = EXCLUDED.relevance
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { TenantId, WorkspaceId, MemoryId = memoryId, EntityId = entityId, Relevance = relevance },
            cancellationToken: cancellationToken
        ));
    }

    #endregion

    #region Relationship Operations

    public async Task<Guid> CreateRelationshipAsync(EntityRelationship relationship, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        // Unique constraint is now (tenant_id, source_entity_id, target_entity_id, relationship_type)
        const string sql = """

                                       INSERT INTO entity_relationships
                                           (id, tenant_id, source_entity_id, target_entity_id, relationship_type, confidence, first_seen_memory_id, metadata)
                                       VALUES (@Id, @TenantId, @SourceEntityId, @TargetEntityId, @RelationshipType, @Confidence, @FirstSeenMemoryId, @Metadata::jsonb)
                                       ON CONFLICT (tenant_id, source_entity_id, target_entity_id, relationship_type) DO UPDATE SET
                                           confidence = GREATEST(entity_relationships.confidence, EXCLUDED.confidence),
                                           metadata = COALESCE(EXCLUDED.metadata, entity_relationships.metadata)
                                       RETURNING id
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var returnedId = await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                TenantId,
                relationship.SourceEntityId,
                relationship.TargetEntityId,
                relationship.RelationshipType,
                relationship.Confidence,
                relationship.FirstSeenMemoryId,
                Metadata = relationship.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(relationship.Metadata) : null
            },
            cancellationToken: cancellationToken
        ));

        return returnedId;
    }

    public async Task<List<EntityRelationship>> GetRelationshipsForEntityAsync(
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT
                                           er.*,
                                           source.name as source_name,
                                           source.entity_type as source_type,
                                           target.name as target_name,
                                           target.entity_type as target_type
                                       FROM entity_relationships er
                                       JOIN entities source ON er.source_entity_id = source.id
                                       JOIN entities target ON er.target_entity_id = target.id
                                       WHERE er.source_entity_id = @EntityId OR er.target_entity_id = @EntityId
                                       ORDER BY er.confidence DESC
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { EntityId = entityId },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToEntityRelationship).ToList();
    }

    public async Task<Dictionary<Guid, List<EntityRelationship>>> GetRelationshipsForEntitiesAsync(
        List<Guid> entityIds,
        CancellationToken cancellationToken = default)
    {
        var result = entityIds.ToDictionary(id => id, _ => new List<EntityRelationship>());

        if (entityIds.Count == 0)
            return result;

        const string sql = """
            SELECT
                er.*,
                source.name as source_name,
                source.entity_type as source_type,
                target.name as target_name,
                target.entity_type as target_type
            FROM entity_relationships er
            JOIN entities source ON er.source_entity_id = source.id
            JOIN entities target ON er.target_entity_id = target.id
            WHERE er.source_entity_id = ANY(@EntityIds) OR er.target_entity_id = ANY(@EntityIds)
            ORDER BY er.confidence DESC
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { EntityIds = entityIds },
            cancellationToken: cancellationToken
        ));

        foreach (var row in rows)
        {
            var rel = MapToEntityRelationship(row);
            Guid sourceId = row.source_entity_id;
            Guid targetId = row.target_entity_id;

            if (result.ContainsKey(sourceId))
                result[sourceId].Add(rel);
            if (result.ContainsKey(targetId) && sourceId != targetId)
                result[targetId].Add(rel);
        }

        return result;
    }

    public async Task<List<EntityRelationship>> GetAllRelationshipsAsync(
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT
                                           er.*,
                                           source.name as source_name,
                                           source.entity_type as source_type,
                                           target.name as target_name,
                                           target.entity_type as target_type
                                       FROM entity_relationships er
                                       JOIN entities source ON er.source_entity_id = source.id
                                       JOIN entities target ON er.target_entity_id = target.id
                                       ORDER BY er.created_at DESC
                                       LIMIT @Limit
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Limit = limit },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToEntityRelationship).ToList();
    }

    public async Task<List<Entity>> GetAllEntitiesAsync(
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT * FROM entities
                                       ORDER BY created_at DESC
                                       LIMIT @Limit
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Limit = limit },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToEntity).ToList();
    }

    #endregion

    #region User Persona Operations

    public async Task SetUserPersonaAttributeAsync(UserPersona persona, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        // Unique constraint is now (tenant_id, workspace_id, user_id, attribute_type, attribute_key)
        const string sql = """

                                       INSERT INTO user_personas
                                           (id, tenant_id, workspace_id, user_id, attribute_type, attribute_key, attribute_value, confidence, source_memory_id)
                                       VALUES (@Id, @TenantId, @WorkspaceId, @UserId, @AttributeType, @AttributeKey, @AttributeValue, @Confidence, @SourceMemoryId)
                                       ON CONFLICT (tenant_id, workspace_id, user_id, attribute_type, attribute_key) DO UPDATE SET
                                           attribute_value = EXCLUDED.attribute_value,
                                           confidence = EXCLUDED.confidence,
                                           updated_at = NOW()
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                TenantId,
                WorkspaceId,
                persona.UserId,
                persona.AttributeType,
                persona.AttributeKey,
                persona.AttributeValue,
                persona.Confidence,
                persona.SourceMemoryId
            },
            cancellationToken: cancellationToken
        ));
    }

    public async Task<Dictionary<string, Dictionary<string, object>>> GetUserPersonaAsync(
        string userId = "default_user",
        CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT attribute_type, attribute_key, attribute_value, confidence, updated_at
                                       FROM user_personas
                                       WHERE user_id = @UserId
                                       ORDER BY attribute_type, attribute_key
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { UserId = userId },
            cancellationToken: cancellationToken
        ));

        var persona = new Dictionary<string, Dictionary<string, object>>();
        foreach (var row in results)
        {
            string attrType = row.attribute_type;
            if (!persona.ContainsKey(attrType))
                persona[attrType] = new Dictionary<string, object>();

            persona[attrType][row.attribute_key] = new Dictionary<string, object>
            {
                ["value"] = row.attribute_value,
                ["confidence"] = (float)row.confidence,
                ["updated_at"] = ((DateTime)row.updated_at).ToString("O")
            };
        }

        return persona;
    }

    public async Task<List<UserPersona>> GetActiveGoalsAsync(string userId = "default_user", CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, user_id, attribute_type, attribute_key, attribute_value, confidence, source_memory_id, updated_at
            FROM user_personas
            WHERE user_id = @UserId AND attribute_type = 'goal' AND confidence > 0
            ORDER BY confidence DESC, updated_at DESC
        """;

        await using var conn = await OpenConnectionAsync(ct);

        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { UserId = userId },
            cancellationToken: ct
        ));

        return rows.Select(row => new UserPersona
        {
            Id = row.id,
            UserId = row.user_id,
            AttributeType = row.attribute_type,
            AttributeKey = row.attribute_key,
            AttributeValue = row.attribute_value,
            Confidence = (float)row.confidence,
            SourceMemoryId = row.source_memory_id,
            UpdatedAt = row.updated_at
        }).ToList();
    }

    #endregion

    #region Conversation Session Operations

    public async Task<Guid> CreateConversationSessionAsync(ConversationSession session, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        const string sql = """

                                       INSERT INTO conversation_sessions (id, tenant_id, workspace_id, session_name, client_type, metadata)
                                       VALUES (@Id, @TenantId, @WorkspaceId, @SessionName, @ClientType, @Metadata::jsonb)
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                TenantId,
                WorkspaceId,
                session.SessionName,
                session.ClientType,
                Metadata = session.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(session.Metadata) : null
            },
            cancellationToken: cancellationToken
        ));

        return id;
    }

    public async Task EndConversationSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = "UPDATE conversation_sessions SET ended_at = NOW() WHERE id = @SessionId";

        await using var conn = await OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { SessionId = sessionId },
            cancellationToken: cancellationToken
        ));
    }

    public async Task<List<ConversationSession>> GetRecentSessionsAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT * FROM conversation_sessions
                                       ORDER BY started_at DESC
                                       LIMIT @Limit
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Limit = limit },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToConversationSession).ToList();
    }

    #endregion

    #region Mapping Methods

    private static Memory MapToMemory(dynamic row)
    {
        // Handle both DapperRow (which implements IDictionary<string, object>) and anonymous types
        IDictionary<string, object>? rowDict = row as IDictionary<string, object>;

        var memory = new Memory
        {
            Id = row.id,
            Content = row.content,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at,
            Source = row.source,
            ConversationSessionId = row.conversation_session_id,
            Metadata = row.metadata != null ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString()) : null
        };

        // Map search scores and memory_type when available from query results
        if (rowDict != null)
        {
            // For DapperRow, use dictionary access for optional properties
            memory.Similarity = rowDict.TryGetValue("similarity", out var sim) && sim != null ? Convert.ToSingle(sim) : 0f;
            memory.Rank = rowDict.TryGetValue("rank", out var rank) && rank != null ? Convert.ToSingle(rank) : 0f;
            memory.MemoryType = rowDict.TryGetValue("memory_type", out var mt) && mt != null ? mt.ToString()! : "knowledge";
        }
        else
        {
            // For anonymous types or other dynamic objects, try direct property access with fallback
            try { memory.Similarity = (float)(row.similarity ?? 0f); } catch { memory.Similarity = 0f; }
            try { memory.Rank = (float)(row.rank ?? 0f); } catch { memory.Rank = 0f; }
            try { memory.MemoryType = row.memory_type ?? "knowledge"; } catch { memory.MemoryType = "knowledge"; }
        }

        return memory;
    }

    private static Entity MapToEntity(dynamic row)
    {
        return new Entity
        {
            Id = row.id,
            Name = row.name,
            EntityType = row.entity_type,
            CanonicalName = row.canonical_name,
            CreatedAt = row.created_at,
            FirstSeenMemoryId = row.first_seen_memory_id,
            Metadata = row.metadata != null ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString()) : null
        };
    }

    private static EntityRelationship MapToEntityRelationship(dynamic row)
    {
        // Handle both DapperRow (which implements IDictionary<string, object>) and anonymous types
        IDictionary<string, object>? rowDict = row as IDictionary<string, object>;

        var relationship = new EntityRelationship
        {
            Id = row.id,
            SourceEntityId = row.source_entity_id,
            TargetEntityId = row.target_entity_id,
            RelationshipType = row.relationship_type,
            Confidence = row.confidence,
            CreatedAt = row.created_at,
            FirstSeenMemoryId = row.first_seen_memory_id,
            Metadata = row.metadata != null ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString()) : null
        };

        // Populate navigation properties when joined entity data is available
        if (rowDict != null)
        {
            // For DapperRow, use dictionary access for optional joined properties
            if (rowDict.TryGetValue("source_name", out var sourceName) && sourceName != null)
            {
                relationship.SourceEntity = new Entity
                {
                    Id = row.source_entity_id,
                    Name = sourceName.ToString()!,
                    EntityType = rowDict.TryGetValue("source_type", out var sourceType) && sourceType != null
                        ? sourceType.ToString()!
                        : "UNKNOWN"
                };
            }

            if (rowDict.TryGetValue("target_name", out var targetName) && targetName != null)
            {
                relationship.TargetEntity = new Entity
                {
                    Id = row.target_entity_id,
                    Name = targetName.ToString()!,
                    EntityType = rowDict.TryGetValue("target_type", out var targetType) && targetType != null
                        ? targetType.ToString()!
                        : "UNKNOWN"
                };
            }
        }
        else
        {
            // For anonymous types, try direct property access with fallback
            try
            {
                string? sourceName = row.source_name;
                if (sourceName != null)
                {
                    string? sourceType = null;
                    try { sourceType = row.source_type; } catch { }
                    relationship.SourceEntity = new Entity
                    {
                        Id = row.source_entity_id,
                        Name = sourceName,
                        EntityType = sourceType ?? "UNKNOWN"
                    };
                }
            }
            catch { /* Property doesn't exist on anonymous type */ }

            try
            {
                string? targetName = row.target_name;
                if (targetName != null)
                {
                    string? targetType = null;
                    try { targetType = row.target_type; } catch { }
                    relationship.TargetEntity = new Entity
                    {
                        Id = row.target_entity_id,
                        Name = targetName,
                        EntityType = targetType ?? "UNKNOWN"
                    };
                }
            }
            catch { /* Property doesn't exist on anonymous type */ }
        }

        return relationship;
    }

    private static ConversationSession MapToConversationSession(dynamic row)
    {
        return new ConversationSession
        {
            Id = row.id,
            SessionName = row.session_name,
            StartedAt = row.started_at,
            EndedAt = row.ended_at,
            ClientType = row.client_type,
            Metadata = row.metadata != null ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString()) : null
        };
    }

    #endregion

    #region Batch Create Operations

    public async Task<Dictionary<string, Guid>> CreateEntitiesBatchAsync(List<Entity> entities, CancellationToken cancellationToken = default)
    {
        if (entities.Count == 0)
            return new Dictionary<string, Guid>();

        var tenantId = TenantId;
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);

        // Build multi-row INSERT with ON CONFLICT RETURNING
        var sql = """
            INSERT INTO entities (id, tenant_id, name, entity_type, canonical_name, first_seen_memory_id, metadata)
            SELECT * FROM UNNEST(@Ids, @TenantIds, @Names, @EntityTypes, @CanonicalNames, @FirstSeenMemoryIds, @Metadatas)
            ON CONFLICT (tenant_id, name, entity_type) DO UPDATE SET
                canonical_name = COALESCE(EXCLUDED.canonical_name, entities.canonical_name),
                metadata = COALESCE(EXCLUDED.metadata, entities.metadata)
            RETURNING id, name
            """;

        var ids = entities.Select(_ => Guid.CreateVersion7()).ToArray();
        var tenantIds = entities.Select(_ => tenantId).ToArray();
        var names = entities.Select(e => e.Name).ToArray();
        var entityTypes = entities.Select(e => e.EntityType).ToArray();
        var canonicalNames = entities.Select(e => e.CanonicalName).ToArray();
        var firstSeenMemoryIds = entities.Select(e => e.FirstSeenMemoryId).ToArray();
        var metadatas = entities.Select(e =>
            e.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(e.Metadata) : (string?)null).ToArray();

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Ids", ids);
        cmd.Parameters.AddWithValue("@TenantIds", tenantIds);
        cmd.Parameters.AddWithValue("@Names", names);
        cmd.Parameters.AddWithValue("@EntityTypes", entityTypes);
        cmd.Parameters.AddWithValue("@CanonicalNames", canonicalNames);
        cmd.Parameters.AddWithValue("@FirstSeenMemoryIds", firstSeenMemoryIds);
        cmd.Parameters.AddWithValue("@Metadatas", metadatas);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(1)] = reader.GetGuid(0);
        }

        return result;
    }

    public async Task LinkMemoryToEntitiesBatchAsync(Guid memoryId, Dictionary<Guid, float> entityRelevances, CancellationToken cancellationToken = default)
    {
        if (entityRelevances.Count == 0) return;

        var tenantId = TenantId;
        var workspaceId = WorkspaceId;

        const string sql = """
            INSERT INTO memory_entities (tenant_id, workspace_id, memory_id, entity_id, relevance)
            SELECT * FROM UNNEST(@TenantIds, @WorkspaceIds, @MemoryIds, @EntityIds, @Relevances)
            ON CONFLICT (memory_id, entity_id) DO UPDATE SET relevance = EXCLUDED.relevance
            """;

        var count = entityRelevances.Count;
        var tenantIds = Enumerable.Repeat(tenantId, count).ToArray();
        var workspaceIds = Enumerable.Repeat(workspaceId, count).ToArray();
        var memoryIds = Enumerable.Repeat(memoryId, count).ToArray();
        var entityIds = entityRelevances.Keys.ToArray();
        var relevances = entityRelevances.Values.ToArray();

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@TenantIds", tenantIds);
        cmd.Parameters.AddWithValue("@WorkspaceIds", workspaceIds);
        cmd.Parameters.AddWithValue("@MemoryIds", memoryIds);
        cmd.Parameters.AddWithValue("@EntityIds", entityIds);
        cmd.Parameters.AddWithValue("@Relevances", relevances);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<Guid>> CreateRelationshipsBatchAsync(List<EntityRelationship> relationships, CancellationToken cancellationToken = default)
    {
        if (relationships.Count == 0)
            return [];

        var tenantId = TenantId;

        const string sql = """
            INSERT INTO entity_relationships (id, tenant_id, source_entity_id, target_entity_id, relationship_type, confidence, first_seen_memory_id, metadata)
            SELECT * FROM UNNEST(@Ids, @TenantIds, @SourceEntityIds, @TargetEntityIds, @RelationshipTypes, @Confidences, @FirstSeenMemoryIds, @Metadatas)
            ON CONFLICT (tenant_id, source_entity_id, target_entity_id, relationship_type) DO UPDATE SET
                confidence = GREATEST(entity_relationships.confidence, EXCLUDED.confidence),
                metadata = COALESCE(EXCLUDED.metadata, entity_relationships.metadata)
            RETURNING id
            """;

        var ids = relationships.Select(_ => Guid.CreateVersion7()).ToArray();
        var tenantIds = relationships.Select(_ => tenantId).ToArray();
        var sourceIds = relationships.Select(r => r.SourceEntityId).ToArray();
        var targetIds = relationships.Select(r => r.TargetEntityId).ToArray();
        var relTypes = relationships.Select(r => r.RelationshipType).ToArray();
        var confidences = relationships.Select(r => r.Confidence).ToArray();
        var firstSeenIds = relationships.Select(r => r.FirstSeenMemoryId).ToArray();
        var metadatas = relationships.Select(r =>
            r.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(r.Metadata) : (string?)null).ToArray();

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Ids", ids);
        cmd.Parameters.AddWithValue("@TenantIds", tenantIds);
        cmd.Parameters.AddWithValue("@SourceEntityIds", sourceIds);
        cmd.Parameters.AddWithValue("@TargetEntityIds", targetIds);
        cmd.Parameters.AddWithValue("@RelationshipTypes", relTypes);
        cmd.Parameters.AddWithValue("@Confidences", confidences);
        cmd.Parameters.AddWithValue("@FirstSeenMemoryIds", firstSeenIds);
        cmd.Parameters.AddWithValue("@Metadatas", metadatas);

        var result = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    public async Task<(long Memories, long Entities, long Relationships)> GetGraphStatisticsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM memories) AS memory_count,
                (SELECT COUNT(*) FROM entities) AS entity_count,
                (SELECT COUNT(*) FROM entity_relationships) AS relationship_count
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var row = await conn.QuerySingleAsync<dynamic>(new CommandDefinition(sql, cancellationToken: cancellationToken));

        return ((long)row.memory_count, (long)row.entity_count, (long)row.relationship_count);
    }

    public async Task<Dictionary<string, int>> GetRelationshipTypeBreakdownAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT relationship_type, COUNT(*)::int AS cnt
            FROM entity_relationships
            GROUP BY relationship_type
            ORDER BY cnt DESC
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, cancellationToken: cancellationToken));

        return rows.ToDictionary(
            r => (string)r.relationship_type,
            r => (int)r.cnt);
    }

    #endregion

    #region Statistics Operations

    public async Task<long> GetMemoryCountAsync(CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = "SELECT COUNT(*) FROM memories";

        await using var conn = await OpenConnectionAsync(cancellationToken);

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<long> GetEntityCountAsync(CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = "SELECT COUNT(*) FROM entities";

        await using var conn = await OpenConnectionAsync(cancellationToken);

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<long> GetRelationshipCountAsync(CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = "SELECT COUNT(*) FROM entity_relationships";

        await using var conn = await OpenConnectionAsync(cancellationToken);

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    #endregion

    #region Batch Operations

    public async Task<List<Memory>> GetMemoriesWithoutEntitiesAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT m.id, m.content, m.created_at, m.updated_at, m.source,
                                              m.conversation_session_id, m.metadata::text
                                       FROM memories m
                                       WHERE NOT EXISTS (
                                           SELECT 1 FROM memory_entities me WHERE me.memory_id = m.id
                                       )
                                       ORDER BY m.created_at DESC
                                       LIMIT @Limit
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Limit = limit },
            cancellationToken: cancellationToken));

        return results.Select(row => new Memory
        {
            Id = row.id,
            Content = row.content,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at,
            Source = row.source,
            ConversationSessionId = row.conversation_session_id,
            Metadata = row.metadata != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString())
                : null
        }).ToList();
    }

    public async Task<List<Memory>> GetMemoriesWithNullEmbeddingsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT id, content, created_at, updated_at, source,
                                              conversation_session_id, metadata::text
                                       FROM memories
                                       WHERE embedding IS NULL
                                       ORDER BY created_at DESC
                                       LIMIT @Limit
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Limit = limit },
            cancellationToken: cancellationToken));

        return results.Select(row => new Memory
        {
            Id = row.id,
            Content = row.content,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at,
            Source = row.source,
            ConversationSessionId = row.conversation_session_id,
            Metadata = row.metadata != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString())
                : null
        }).ToList();
    }

    public async Task<List<Memory>> GetAllMemoriesAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                       SELECT id, content, created_at, updated_at, source,
                                              conversation_session_id, metadata::text
                                       FROM memories
                                       ORDER BY created_at DESC
                                       LIMIT @Limit OFFSET @Offset
                           """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Limit = limit, Offset = offset },
            cancellationToken: cancellationToken));

        return results.Select(row => new Memory
        {
            Id = row.id,
            Content = row.content,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at,
            Source = row.source,
            ConversationSessionId = row.conversation_session_id,
            Metadata = row.metadata != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString())
                : null
        }).ToList();
    }

    public async Task<List<Memory>> GetMemoriesByDateRangeAsync(DateTime fromUtc, DateTime toUtc, int limit = 100, CancellationToken cancellationToken = default)
    {
        // RLS policy will filter by tenant automatically
        const string sql = """

                                   SELECT id, content, created_at, updated_at, source,
                                          conversation_session_id, metadata::text
                                   FROM memories
                                   WHERE created_at >= @FromUtc AND created_at <= @ToUtc
                                   ORDER BY created_at DESC
                                   LIMIT @Limit
                       """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { FromUtc = fromUtc, ToUtc = toUtc, Limit = limit },
            cancellationToken: cancellationToken));

        return results.Select(row => new Memory
        {
            Id = row.id,
            Content = row.content,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at,
            Source = row.source,
            ConversationSessionId = row.conversation_session_id,
            Metadata = row.metadata != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString())
                : null
        }).ToList();
    }

    public async Task<List<Memory>> SearchMemoriesByEmbeddingInDateRangeAsync(
        float[] queryEmbedding,
        DateTime fromUtc,
        DateTime toUtc,
        float threshold = 0.3f,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                id, content, created_at, updated_at, source, conversation_session_id, metadata, memory_type,
                1 - (embedding <=> @QueryEmbedding) as similarity
            FROM memories
            WHERE created_at >= @FromUtc AND created_at <= @ToUtc
              AND embedding IS NOT NULL
              AND 1 - (embedding <=> @QueryEmbedding) > @Threshold
            ORDER BY embedding <=> @QueryEmbedding
            LIMIT @Limit
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@QueryEmbedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("@FromUtc", fromUtc);
        cmd.Parameters.AddWithValue("@ToUtc", toUtc);
        cmd.Parameters.AddWithValue("@Threshold", threshold);
        cmd.Parameters.AddWithValue("@Limit", limit);

        var results = new List<dynamic>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dynamic row = new ExpandoObject();
            var dict = (IDictionary<string, object?>)row;
            dict["id"] = reader.GetGuid(0);
            dict["content"] = reader.GetString(1);
            dict["created_at"] = reader.GetDateTime(2);
            dict["updated_at"] = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            dict["source"] = reader.IsDBNull(4) ? null : reader.GetString(4);
            dict["conversation_session_id"] = reader.IsDBNull(5) ? null : reader.GetGuid(5);
            dict["metadata"] = reader.IsDBNull(6) ? null : reader.GetString(6);
            dict["memory_type"] = reader.IsDBNull(7) ? "knowledge" : reader.GetString(7);
            dict["similarity"] = reader.GetFloat(8);
            results.Add(row);
        }

        return results.Select(MapToMemory).ToList();
    }

    public async Task<List<Memory>> GetMemoriesByTypeAsync(string memoryType, int limit = 50, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, content, created_at, updated_at, source,
                   conversation_session_id, metadata::text, memory_type
            FROM memories
            WHERE memory_type = @MemoryType
            ORDER BY created_at DESC
            LIMIT @Limit
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { MemoryType = memoryType, Limit = limit },
            cancellationToken: cancellationToken));

        return results.Select(MapToMemory).ToList();
    }

    public async Task<List<Memory>> GetMemoriesBySessionAsync(Guid sessionId, int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, content, created_at, updated_at, source,
                   conversation_session_id, metadata::text, memory_type
            FROM memories
            WHERE conversation_session_id = @SessionId
            ORDER BY created_at ASC
            LIMIT @Limit
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { SessionId = sessionId, Limit = limit },
            cancellationToken: cancellationToken));

        return results.Select(MapToMemory).ToList();
    }

    #endregion

    #region Workspace Operations

    public async Task<Guid> CreateWorkspaceAsync(Workspace workspace, CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();

        const string sql = """
            INSERT INTO workspaces (id, tenant_id, workspace_id, display_name, description, metadata)
            VALUES (@Id, @TenantId, @WorkspaceSlug, @DisplayName, @Description, @Metadata::jsonb)
            ON CONFLICT (tenant_id, workspace_id) DO UPDATE SET
                display_name = COALESCE(EXCLUDED.display_name, workspaces.display_name),
                description = COALESCE(EXCLUDED.description, workspaces.description),
                metadata = COALESCE(EXCLUDED.metadata, workspaces.metadata),
                updated_at = NOW()
            RETURNING id
            """;

        await using var conn = await OpenConnectionAsync(ct);

        return await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                TenantId,
                WorkspaceSlug = workspace.WorkspaceId,
                workspace.DisplayName,
                workspace.Description,
                Metadata = workspace.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(workspace.Metadata) : null
            },
            cancellationToken: ct
        ));
    }

    public async Task<List<Workspace>> GetWorkspacesAsync(int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, workspace_id, display_name, description, metadata::text, created_at, updated_at
            FROM workspaces
            ORDER BY created_at ASC
            LIMIT @Limit
            """;

        await using var conn = await OpenConnectionAsync(ct);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql, new { Limit = limit }, cancellationToken: ct));

        return results.Select(row => new Workspace
        {
            Id = row.id,
            WorkspaceId = row.workspace_id,
            DisplayName = row.display_name,
            Description = row.description,
            Metadata = row.metadata != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString())
                : null,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at
        }).ToList();
    }

    public async Task<Workspace?> GetWorkspaceBySlugAsync(string workspaceId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, workspace_id, display_name, description, metadata::text, created_at, updated_at
            FROM workspaces
            WHERE workspace_id = @WorkspaceSlug
            """;

        await using var conn = await OpenConnectionAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
            sql, new { WorkspaceSlug = workspaceId }, cancellationToken: ct));

        if (row == null) return null;

        return new Workspace
        {
            Id = row.id,
            WorkspaceId = row.workspace_id,
            DisplayName = row.display_name,
            Description = row.description,
            Metadata = row.metadata != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString())
                : null,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at
        };
    }

    #endregion

    #region Session Metadata Operations

    public async Task UpdateSessionMetadataAsync(Guid sessionId, Dictionary<string, object> metadata, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE conversation_sessions
            SET metadata = COALESCE(metadata, '{}'::jsonb) || @Metadata::jsonb
            WHERE id = @SessionId
            """;

        await using var conn = await OpenConnectionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                SessionId = sessionId,
                Metadata = System.Text.Json.JsonSerializer.Serialize(metadata)
            },
            cancellationToken: ct
        ));
    }

    #endregion

    #region Workspace Snapshot Operations

    public async Task<Guid> CreateSnapshotAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();

        const string sql = """
            INSERT INTO workspace_snapshots (id, tenant_id, workspace_id, snapshot_name, state_data)
            VALUES (@Id, @TenantId, @WorkspaceSlug, @SnapshotName, @StateData::jsonb)
            ON CONFLICT (tenant_id, workspace_id, snapshot_name) DO UPDATE SET
                state_data = EXCLUDED.state_data,
                created_at = NOW()
            RETURNING id
            """;

        await using var conn = await OpenConnectionAsync(ct);

        return await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                TenantId,
                WorkspaceSlug = snapshot.WorkspaceId,
                snapshot.SnapshotName,
                StateData = System.Text.Json.JsonSerializer.Serialize(snapshot.StateData)
            },
            cancellationToken: ct
        ));
    }

    public async Task<List<WorkspaceSnapshot>> GetSnapshotsAsync(string workspaceId, int limit = 20, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, workspace_id, snapshot_name, state_data::text, created_at
            FROM workspace_snapshots
            WHERE workspace_id = @WorkspaceSlug
            ORDER BY created_at DESC
            LIMIT @Limit
            """;

        await using var conn = await OpenConnectionAsync(ct);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql, new { WorkspaceSlug = workspaceId, Limit = limit }, cancellationToken: ct));

        return results.Select(row => new WorkspaceSnapshot
        {
            Id = row.id,
            WorkspaceId = row.workspace_id,
            SnapshotName = row.snapshot_name,
            StateData = System.Text.Json.JsonSerializer.Deserialize<WorkspaceStateData>(row.state_data.ToString())
                ?? new WorkspaceStateData(),
            CreatedAt = row.created_at
        }).ToList();
    }

    public async Task<WorkspaceSnapshot?> GetSnapshotByNameAsync(string workspaceId, string snapshotName, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, workspace_id, snapshot_name, state_data::text, created_at
            FROM workspace_snapshots
            WHERE workspace_id = @WorkspaceSlug AND snapshot_name = @SnapshotName
            """;

        await using var conn = await OpenConnectionAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
            sql, new { WorkspaceSlug = workspaceId, SnapshotName = snapshotName }, cancellationToken: ct));

        if (row == null) return null;

        return new WorkspaceSnapshot
        {
            Id = row.id,
            WorkspaceId = row.workspace_id,
            SnapshotName = row.snapshot_name,
            StateData = System.Text.Json.JsonSerializer.Deserialize<WorkspaceStateData>(row.state_data.ToString())
                ?? new WorkspaceStateData(),
            CreatedAt = row.created_at
        };
    }

    #endregion
}
