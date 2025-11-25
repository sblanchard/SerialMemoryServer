using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.EventSourcing.Events;

namespace SerialMemory.EventSourcing.Export;

/// <summary>
/// Exports memory data in various formats with support for:
/// - Full JSON export
/// - Resumable chunked export
/// - Optional AES-256 encryption
/// </summary>
public sealed class MemoryExporter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<MemoryExporter> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public MemoryExporter(string connectionString, ILogger<MemoryExporter> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Export all memories as a single JSON file.
    /// </summary>
    public async Task<ExportResult> ExportFullJsonAsync(
        string outputPath,
        ExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ExportOptions.Default;
        var result = new ExportResult { ExportId = Guid.CreateVersion7(), StartedAt = DateTimeOffset.UtcNow };

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            // Get all memories
            var memories = await GetMemoriesAsync(conn, options, cancellationToken);
            result.MemoriesExported = memories.Count;

            // Get entities if requested
            List<ExportEntity>? entities = null;
            if (options.IncludeEntities)
            {
                entities = await GetEntitiesAsync(conn, cancellationToken);
                result.EntitiesExported = entities.Count;
            }

            // Get relationships if requested
            List<ExportRelationship>? relationships = null;
            if (options.IncludeRelationships)
            {
                relationships = await GetRelationshipsAsync(conn, cancellationToken);
                result.RelationshipsExported = relationships.Count;
            }

            // Get events if requested
            List<ExportEvent>? events = null;
            if (options.IncludeEvents)
            {
                events = await GetEventsAsync(conn, options.EventsFromSequence, cancellationToken);
                result.EventsExported = events.Count;
            }

            var export = new FullExport
            {
                ExportId = result.ExportId,
                ExportedAt = result.StartedAt,
                Version = "2.0",
                Memories = memories,
                Entities = entities,
                Relationships = relationships,
                Events = events
            };

            // Serialize
            var json = JsonSerializer.Serialize(export, _jsonOptions);

            // Optionally encrypt
            byte[] data;
            if (!string.IsNullOrEmpty(options.EncryptionKey))
            {
                data = await EncryptAsync(Encoding.UTF8.GetBytes(json), options.EncryptionKey, cancellationToken);
                result.Encrypted = true;
            }
            else
            {
                data = Encoding.UTF8.GetBytes(json);
            }

            // Optionally compress
            if (options.Compress)
            {
                data = await CompressAsync(data, cancellationToken);
                result.Compressed = true;
            }

            // Write to file
            await File.WriteAllBytesAsync(outputPath, data, cancellationToken);
            result.OutputPath = outputPath;
            result.FileSizeBytes = data.Length;

            result.CompletedAt = DateTimeOffset.UtcNow;
            result.Success = true;

            _logger.LogInformation(
                "Export {ExportId} completed: {Memories} memories, {Entities} entities, {Relationships} relationships",
                result.ExportId, result.MemoriesExported, result.EntitiesExported, result.RelationshipsExported);

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.CompletedAt = DateTimeOffset.UtcNow;
            _logger.LogError(ex, "Export {ExportId} failed", result.ExportId);
            return result;
        }
    }

    /// <summary>
    /// Export memories in chunks for resumable export.
    /// </summary>
    public async IAsyncEnumerable<ExportChunk> ExportChunkedAsync(
        int chunkSize = 1000,
        long? fromSequence = null,
        ExportOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= ExportOptions.Default;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var currentSequence = fromSequence ?? 0;
        var chunkIndex = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var memories = await conn.QueryAsync<dynamic>(new CommandDefinition($@"
                SELECT
                    m.memory_id, m.content, m.layer, m.confidence_score, m.half_life_days,
                    m.last_reinforced_at, m.created_at, m.content_hash, m.causal_parents, m.tags,
                    e.global_sequence
                FROM memory_projections m
                JOIN memory_events e ON m.memory_id = e.stream_id AND e.event_version = 1
                WHERE e.global_sequence > @FromSequence
                ORDER BY e.global_sequence
                LIMIT @Limit",
                new { FromSequence = currentSequence, Limit = chunkSize },
                cancellationToken: cancellationToken));

            var memoryList = memories.ToList();

            if (memoryList.Count == 0)
                break;

            var chunk = new ExportChunk
            {
                ChunkIndex = chunkIndex,
                FromSequence = currentSequence,
                ToSequence = (long)memoryList.Last().global_sequence,
                Memories = memoryList.Select(m => new ExportMemory
                {
                    MemoryId = m.memory_id,
                    Content = m.content,
                    Layer = Enum.Parse<MemoryLayer>((string)m.layer),
                    ConfidenceScore = (float)(decimal)m.confidence_score,
                    HalfLifeDays = (int)m.half_life_days,
                    CreatedAt = m.created_at,
                    ContentHash = m.content_hash,
                    CausalParents = m.causal_parents ?? Array.Empty<Guid>(),
                    Tags = m.tags ?? Array.Empty<string>()
                }).ToList(),
                IsLastChunk = memoryList.Count < chunkSize
            };

            currentSequence = chunk.ToSequence;
            chunkIndex++;

            yield return chunk;
        }
    }

    /// <summary>
    /// Export events for event sourcing replay.
    /// </summary>
    public async Task<ExportResult> ExportEventsAsync(
        string outputPath,
        long? fromSequence = null,
        string? encryptionKey = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ExportResult { ExportId = Guid.CreateVersion7(), StartedAt = DateTimeOffset.UtcNow };

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            var events = await GetEventsAsync(conn, fromSequence, cancellationToken);
            result.EventsExported = events.Count;

            var export = new EventExport
            {
                ExportId = result.ExportId,
                ExportedAt = result.StartedAt,
                FromSequence = fromSequence ?? 0,
                ToSequence = events.LastOrDefault()?.GlobalSequence ?? 0,
                Events = events
            };

            var json = JsonSerializer.Serialize(export, _jsonOptions);

            byte[] data;
            if (!string.IsNullOrEmpty(encryptionKey))
            {
                data = await EncryptAsync(Encoding.UTF8.GetBytes(json), encryptionKey, cancellationToken);
                result.Encrypted = true;
            }
            else
            {
                data = Encoding.UTF8.GetBytes(json);
            }

            await File.WriteAllBytesAsync(outputPath, data, cancellationToken);
            result.OutputPath = outputPath;
            result.FileSizeBytes = data.Length;
            result.Success = true;
            result.CompletedAt = DateTimeOffset.UtcNow;

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.CompletedAt = DateTimeOffset.UtcNow;
            return result;
        }
    }

    private async Task<List<ExportMemory>> GetMemoriesAsync(
        NpgsqlConnection conn,
        ExportOptions options,
        CancellationToken cancellationToken)
    {
        var layerFilter = options.LayerFilter?.Length > 0
            ? "AND layer = ANY(@Layers::memory_layer[])"
            : "";

        var activeFilter = options.ActiveOnly ? "AND is_active = TRUE" : "";

        var sql = $@"
            SELECT
                memory_id, content, layer, confidence_score, half_life_days,
                last_reinforced_at, created_at, content_hash, causal_parents, tags,
                user_affinity_score, directive_relevance
            FROM memory_projections
            WHERE 1=1 {layerFilter} {activeFilter}
            ORDER BY created_at DESC";

        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
            sql,
            new { Layers = options.LayerFilter?.Select(l => l.ToString()).ToArray() },
            cancellationToken: cancellationToken));

        return rows.Select(r => new ExportMemory
        {
            MemoryId = r.memory_id,
            Content = r.content,
            Layer = Enum.Parse<MemoryLayer>((string)r.layer),
            ConfidenceScore = (float)(decimal)r.confidence_score,
            HalfLifeDays = (int)r.half_life_days,
            CreatedAt = r.created_at,
            ContentHash = r.content_hash,
            CausalParents = r.causal_parents ?? Array.Empty<Guid>(),
            Tags = r.tags ?? Array.Empty<string>()
        }).ToList();
    }

    private static async Task<List<ExportEntity>> GetEntitiesAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
            SELECT entity_id, name, entity_type, mention_count, created_at
            FROM entity_projections
            ORDER BY mention_count DESC",
            cancellationToken: cancellationToken));

        return rows.Select(r => new ExportEntity
        {
            EntityId = r.entity_id,
            Name = r.name,
            EntityType = r.entity_type,
            MentionCount = r.mention_count,
            CreatedAt = r.created_at
        }).ToList();
    }

    private static async Task<List<ExportRelationship>> GetRelationshipsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
            SELECT
                r.relationship_id, r.source_entity_id, r.target_entity_id,
                r.relationship_type, r.strength, r.created_at,
                s.name AS source_name, t.name AS target_name
            FROM relationship_projections r
            JOIN entity_projections s ON r.source_entity_id = s.entity_id
            JOIN entity_projections t ON r.target_entity_id = t.entity_id
            ORDER BY r.strength DESC",
            cancellationToken: cancellationToken));

        return rows.Select(r => new ExportRelationship
        {
            RelationshipId = r.relationship_id,
            SourceEntityId = r.source_entity_id,
            TargetEntityId = r.target_entity_id,
            SourceName = r.source_name,
            TargetName = r.target_name,
            RelationshipType = r.relationship_type,
            Strength = (float)(decimal)r.strength,
            CreatedAt = r.created_at
        }).ToList();
    }

    private static async Task<List<ExportEvent>> GetEventsAsync(
        NpgsqlConnection conn,
        long? fromSequence,
        CancellationToken cancellationToken)
    {
        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
            SELECT
                event_id, stream_id, event_type, event_version, global_sequence,
                event_data, metadata, created_at, created_by, content_hash
            FROM memory_events
            WHERE global_sequence > @FromSequence
            ORDER BY global_sequence",
            new { FromSequence = fromSequence ?? 0 },
            cancellationToken: cancellationToken));

        return rows.Select(r => new ExportEvent
        {
            EventId = r.event_id,
            StreamId = r.stream_id,
            EventType = Enum.Parse<MemoryEventType>((string)r.event_type),
            EventVersion = r.event_version,
            GlobalSequence = r.global_sequence,
            EventData = r.event_data,
            Metadata = r.metadata,
            CreatedAt = r.created_at,
            CreatedBy = r.created_by,
            ContentHash = r.content_hash
        }).ToList();
    }

    private static async Task<byte[]> EncryptAsync(byte[] data, string key, CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        aes.GenerateIV();

        using var ms = new MemoryStream();
        await ms.WriteAsync(aes.IV, cancellationToken);

        using (var cryptoStream = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
        {
            await cryptoStream.WriteAsync(data, cancellationToken);
        }

        return ms.ToArray();
    }

    private static async Task<byte[]> CompressAsync(byte[] data, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            await gzip.WriteAsync(data, cancellationToken);
        }
        return output.ToArray();
    }
}

#region Export Models

public sealed class ExportOptions
{
    public bool IncludeEntities { get; set; } = true;
    public bool IncludeRelationships { get; set; } = true;
    public bool IncludeEvents { get; set; } = false;
    public long? EventsFromSequence { get; set; }
    public MemoryLayer[]? LayerFilter { get; set; }
    public bool ActiveOnly { get; set; } = true;
    public string? EncryptionKey { get; set; }
    public bool Compress { get; set; } = false;

    public static ExportOptions Default => new();
}

public sealed class ExportResult
{
    public Guid ExportId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }
    public long FileSizeBytes { get; set; }
    public int MemoriesExported { get; set; }
    public int EntitiesExported { get; set; }
    public int RelationshipsExported { get; set; }
    public int EventsExported { get; set; }
    public bool Encrypted { get; set; }
    public bool Compressed { get; set; }
}

public sealed class FullExport
{
    public Guid ExportId { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public string Version { get; set; } = "2.0";
    public List<ExportMemory> Memories { get; set; } = [];
    public List<ExportEntity>? Entities { get; set; }
    public List<ExportRelationship>? Relationships { get; set; }
    public List<ExportEvent>? Events { get; set; }
}

public sealed class EventExport
{
    public Guid ExportId { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public long FromSequence { get; set; }
    public long ToSequence { get; set; }
    public List<ExportEvent> Events { get; set; } = [];
}

public sealed class ExportChunk
{
    public int ChunkIndex { get; set; }
    public long FromSequence { get; set; }
    public long ToSequence { get; set; }
    public List<ExportMemory> Memories { get; set; } = [];
    public bool IsLastChunk { get; set; }
}

public sealed class ExportMemory
{
    public Guid MemoryId { get; set; }
    public required string Content { get; set; }
    public MemoryLayer Layer { get; set; }
    public float ConfidenceScore { get; set; }
    public int HalfLifeDays { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ContentHash { get; set; }
    public Guid[] CausalParents { get; set; } = [];
    public string[] Tags { get; set; } = [];
}

public sealed class ExportEntity
{
    public Guid EntityId { get; set; }
    public required string Name { get; set; }
    public required string EntityType { get; set; }
    public int MentionCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ExportRelationship
{
    public Guid RelationshipId { get; set; }
    public Guid SourceEntityId { get; set; }
    public Guid TargetEntityId { get; set; }
    public required string SourceName { get; set; }
    public required string TargetName { get; set; }
    public required string RelationshipType { get; set; }
    public float Strength { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ExportEvent
{
    public Guid EventId { get; set; }
    public Guid StreamId { get; set; }
    public MemoryEventType EventType { get; set; }
    public long EventVersion { get; set; }
    public long GlobalSequence { get; set; }
    public required string EventData { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? ContentHash { get; set; }
}

#endregion
