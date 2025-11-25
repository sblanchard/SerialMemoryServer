using Dapper;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// PostgreSQL implementation of the knowledge graph store using pgvector for semantic search
/// </summary>
public class PostgresKnowledgeGraphStore : IKnowledgeGraphStore
{
    private readonly string _connectionString;

    public PostgresKnowledgeGraphStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

        // Register pgvector types
        NpgsqlConnection.GlobalTypeMapper.UseVector();
    }

    private NpgsqlConnection CreateConnection() => new(_connectionString);

    #region Memory Operations

    public async Task<Guid> CreateMemoryAsync(Memory memory, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        const string sql = @"
            INSERT INTO memories (id, content, embedding, source, conversation_session_id, metadata)
            VALUES (@Id, @Content, @Embedding, @Source, @SessionId, @Metadata::jsonb)";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                memory.Content,
                Embedding = memory.Embedding != null ? new Vector(memory.Embedding) : null,
                memory.Source,
                SessionId = memory.ConversationSessionId,
                Metadata = memory.Metadata != null ? System.Text.Json.JsonSerializer.Serialize(memory.Metadata) : null
            },
            cancellationToken: cancellationToken
        ));

        return id;
    }

    public async Task<Memory?> GetMemoryByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT * FROM memories WHERE id = @Id";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var result = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
            sql,
            new { Id = id },
            cancellationToken: cancellationToken
        ));

        return result != null ? MapToMemory(result) : null;
    }

    public async Task<List<Memory>> SearchMemoriesByEmbeddingAsync(
        float[] queryEmbedding,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT
                id, content, created_at, updated_at, source, conversation_session_id, metadata,
                1 - (embedding <=> @QueryEmbedding) as similarity
            FROM memories
            WHERE 1 - (embedding <=> @QueryEmbedding) > @Threshold
            ORDER BY embedding <=> @QueryEmbedding
            LIMIT @Limit";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new
            {
                QueryEmbedding = new Vector(queryEmbedding),
                Threshold = threshold,
                Limit = limit
            },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToMemory).ToList();
    }

    public async Task<List<Memory>> SearchMemoriesByTextAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT
                id, content, created_at, updated_at, source, conversation_session_id, metadata,
                ts_rank(content_tsvector, plainto_tsquery('english', @Query)) as rank
            FROM memories
            WHERE content_tsvector @@ plainto_tsquery('english', @Query)
            ORDER BY rank DESC
            LIMIT @Limit";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Query = query, Limit = limit },
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

        const string sql = @"
            INSERT INTO entities (id, name, entity_type, canonical_name, first_seen_memory_id, metadata)
            VALUES (@Id, @Name, @EntityType, @CanonicalName, @FirstSeenMemoryId, @Metadata::jsonb)
            ON CONFLICT (name, entity_type) DO UPDATE SET
                canonical_name = COALESCE(EXCLUDED.canonical_name, entities.canonical_name),
                metadata = COALESCE(EXCLUDED.metadata, entities.metadata)
            RETURNING id";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var returnedId = await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                Id = id,
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
        const string sql = "SELECT * FROM entities WHERE id = @Id";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var result = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
            sql,
            new { Id = id },
            cancellationToken: cancellationToken
        ));

        return result != null ? MapToEntity(result) : null;
    }

    public async Task<List<Entity>> GetEntitiesForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT e.*, me.relevance
            FROM entities e
            JOIN memory_entities me ON e.id = me.entity_id
            WHERE me.memory_id = @MemoryId
            ORDER BY me.relevance DESC";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { MemoryId = memoryId },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToEntity).ToList();
    }

    public async Task LinkMemoryToEntityAsync(
        Guid memoryId,
        Guid entityId,
        float relevance = 1.0f,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            INSERT INTO memory_entities (memory_id, entity_id, relevance)
            VALUES (@MemoryId, @EntityId, @Relevance)
            ON CONFLICT (memory_id, entity_id) DO UPDATE SET relevance = EXCLUDED.relevance";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { MemoryId = memoryId, EntityId = entityId, Relevance = relevance },
            cancellationToken: cancellationToken
        ));
    }

    #endregion

    #region Relationship Operations

    public async Task<Guid> CreateRelationshipAsync(EntityRelationship relationship, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        const string sql = @"
            INSERT INTO entity_relationships
                (id, source_entity_id, target_entity_id, relationship_type, confidence, first_seen_memory_id, metadata)
            VALUES (@Id, @SourceEntityId, @TargetEntityId, @RelationshipType, @Confidence, @FirstSeenMemoryId, @Metadata::jsonb)
            ON CONFLICT (source_entity_id, target_entity_id, relationship_type) DO UPDATE SET
                confidence = GREATEST(entity_relationships.confidence, EXCLUDED.confidence),
                metadata = COALESCE(EXCLUDED.metadata, entity_relationships.metadata)
            RETURNING id";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var returnedId = await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                Id = id,
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
        const string sql = @"
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
            ORDER BY er.confidence DESC";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        var results = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { EntityId = entityId },
            cancellationToken: cancellationToken
        ));

        return results.Select(MapToEntityRelationship).ToList();
    }

    #endregion

    #region User Persona Operations

    public async Task SetUserPersonaAttributeAsync(UserPersona persona, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        const string sql = @"
            INSERT INTO user_personas
                (id, user_id, attribute_type, attribute_key, attribute_value, confidence, source_memory_id)
            VALUES (@Id, @UserId, @AttributeType, @AttributeKey, @AttributeValue, @Confidence, @SourceMemoryId)
            ON CONFLICT (user_id, attribute_type, attribute_key) DO UPDATE SET
                attribute_value = EXCLUDED.attribute_value,
                confidence = EXCLUDED.confidence,
                updated_at = NOW()";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
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
        const string sql = @"
            SELECT attribute_type, attribute_key, attribute_value, confidence, updated_at
            FROM user_personas
            WHERE user_id = @UserId
            ORDER BY attribute_type, attribute_key";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

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

    #endregion

    #region Conversation Session Operations

    public async Task<Guid> CreateConversationSessionAsync(ConversationSession session, CancellationToken cancellationToken = default)
    {
        // Use Version 7 GUID (time-ordered) for better indexing and sorting
        var id = Guid.CreateVersion7();

        const string sql = @"
            INSERT INTO conversation_sessions (id, session_name, client_type, metadata)
            VALUES (@Id, @SessionName, @ClientType, @Metadata::jsonb)";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
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
        const string sql = "UPDATE conversation_sessions SET ended_at = NOW() WHERE id = @SessionId";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { SessionId = sessionId },
            cancellationToken: cancellationToken
        ));
    }

    public async Task<List<ConversationSession>> GetRecentSessionsAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM conversation_sessions
            ORDER BY started_at DESC
            LIMIT @Limit";

        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);

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
        return new Memory
        {
            Id = row.id,
            Content = row.content,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at,
            Source = row.source,
            ConversationSessionId = row.conversation_session_id,
            Metadata = row.metadata != null ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(row.metadata.ToString()) : null
        };
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
        return new EntityRelationship
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
}
