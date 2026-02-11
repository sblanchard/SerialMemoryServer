using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Json;
using SerialMemory.EventSourcing.Events;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.EventSourcing.Projections;

/// <summary>
/// Projects memory events into the memory_projections read model.
/// </summary>
public sealed class MemoryProjection : IProjection
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<MemoryProjection> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public string ProjectionName => "memory_projections";

    public MemoryProjection(string connectionString, ILogger<MemoryProjection> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
        _logger = logger;
        _jsonOptions = SerialMemoryJsonOptions.WithEnums;
    }

    public async Task ApplyAsync(StoredEvent storedEvent, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        switch (storedEvent.EventType)
        {
            case MemoryEventType.MemoryCreated:
                await ApplyMemoryCreated(conn, storedEvent, cancellationToken);
                break;
            case MemoryEventType.MemoryUpdated:
                await ApplyMemoryUpdated(conn, storedEvent, cancellationToken);
                break;
            case MemoryEventType.MemoryReinforced:
                await ApplyMemoryReinforced(conn, storedEvent, cancellationToken);
                break;
            case MemoryEventType.MemoryDecayed:
                await ApplyMemoryDecayed(conn, storedEvent, cancellationToken);
                break;
            case MemoryEventType.MemoryLayerTransitioned:
                await ApplyMemoryLayerTransitioned(conn, storedEvent, cancellationToken);
                break;
            case MemoryEventType.MemoryInvalidated:
                await ApplyMemoryInvalidated(conn, storedEvent, cancellationToken);
                break;
            case MemoryEventType.MemoryMerged:
                await ApplyMemoryMerged(conn, storedEvent, cancellationToken);
                break;

            // Classification events - acknowledged but don't modify projections
            case MemoryEventType.LayerGenerated:
            case MemoryEventType.LayerClassified:
                _logger.LogDebug("Skipping classification event {EventType} for stream {StreamId}",
                    storedEvent.EventType, storedEvent.StreamId);
                return;

            // Safety/export/unknown events - skip silently
            case MemoryEventType.ContradictionDetected:
            case MemoryEventType.HallucinationFlagged:
            case MemoryEventType.IntegrityCheckFailed:
            case MemoryEventType.ExportCompleted:
            case MemoryEventType.Unknown:
            default:
                _logger.LogDebug("Skipping event type {EventType} for stream {StreamId} (no projection handler)",
                    storedEvent.EventType, storedEvent.StreamId);
                return;
        }

        _logger.LogDebug("Applied {EventType} to projection for stream {StreamId}",
            storedEvent.EventType, storedEvent.StreamId);
    }

    private async Task ApplyMemoryCreated(NpgsqlConnection conn, StoredEvent storedEvent, CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<MemoryCreatedEvent>(storedEvent.EventData, _jsonOptions)!;

        // Skip events with empty content (legacy/malformed events)
        if (string.IsNullOrEmpty(@event.Content))
        {
            _logger.LogDebug("Skipping MemoryCreated event {EventId} with empty content for stream {StreamId}",
                storedEvent.EventId, storedEvent.StreamId);
            return;
        }

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO memory_projections
                (memory_id, content, content_hash, embedding, layer, confidence_score, half_life_days,
                 last_reinforced_at, causal_parents, source, session_id, user_id, tags,
                 current_version, last_event_id, created_at, updated_at)
            VALUES
                (@MemoryId, @Content, @ContentHash, @Embedding, @Layer::memory_layer, @Confidence, @HalfLife,
                 @LastReinforced, @CausalParents, @Source, @SessionId, @UserId, @Tags,
                 @Version, @EventId, @CreatedAt, @CreatedAt)
            ON CONFLICT (memory_id) DO NOTHING", conn);

        cmd.Parameters.AddWithValue("@MemoryId", storedEvent.StreamId);
        cmd.Parameters.AddWithValue("@Content", @event.Content);
        cmd.Parameters.AddWithValue("@ContentHash", ComputeHash(@event.Content));
        cmd.Parameters.AddWithValue("@Embedding",
            @event.Embedding.Length > 0 ? new Vector(@event.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@Layer", @event.Layer.ToString());
        cmd.Parameters.AddWithValue("@Confidence", (decimal)@event.ConfidenceScore);
        cmd.Parameters.AddWithValue("@HalfLife", @event.HalfLifeDays);
        cmd.Parameters.AddWithValue("@LastReinforced", storedEvent.CreatedAt);
        cmd.Parameters.AddWithValue("@CausalParents", @event.CausalParents);
        cmd.Parameters.AddWithValue("@Source", (object?)@event.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SessionId", (object?)@event.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UserId", @event.UserId);
        cmd.Parameters.AddWithValue("@Tags", @event.Tags);
        cmd.Parameters.AddWithValue("@Version", storedEvent.EventVersion);
        cmd.Parameters.AddWithValue("@EventId", storedEvent.EventId);
        cmd.Parameters.AddWithValue("@CreatedAt", storedEvent.CreatedAt);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ApplyMemoryUpdated(NpgsqlConnection conn, StoredEvent storedEvent, CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<MemoryUpdatedEvent>(storedEvent.EventData, _jsonOptions)!;

        // Skip events with empty content (legacy/malformed events)
        if (string.IsNullOrEmpty(@event.NewContent))
        {
            _logger.LogDebug("Skipping MemoryUpdated event {EventId} with empty content for stream {StreamId}",
                storedEvent.EventId, storedEvent.StreamId);
            return;
        }

        if (@event.NewEmbedding != null && @event.NewEmbedding.Length > 0)
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE memory_projections
                SET content = @Content, content_hash = @ContentHash, embedding = @Embedding,
                    current_version = @Version, last_event_id = @EventId, updated_at = @UpdatedAt
                WHERE memory_id = @MemoryId", conn);

            cmd.Parameters.AddWithValue("@MemoryId", storedEvent.StreamId);
            cmd.Parameters.AddWithValue("@Content", @event.NewContent);
            cmd.Parameters.AddWithValue("@ContentHash", ComputeHash(@event.NewContent));
            cmd.Parameters.AddWithValue("@Embedding", new Vector(@event.NewEmbedding));
            cmd.Parameters.AddWithValue("@Version", storedEvent.EventVersion);
            cmd.Parameters.AddWithValue("@EventId", storedEvent.EventId);
            cmd.Parameters.AddWithValue("@UpdatedAt", storedEvent.CreatedAt);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE memory_projections
                SET content = @Content, content_hash = @ContentHash,
                    current_version = @Version, last_event_id = @EventId, updated_at = @UpdatedAt
                WHERE memory_id = @MemoryId",
                new
                {
                    MemoryId = storedEvent.StreamId,
                    Content = @event.NewContent,
                    ContentHash = ComputeHash(@event.NewContent),
                    Version = storedEvent.EventVersion,
                    EventId = storedEvent.EventId,
                    UpdatedAt = storedEvent.CreatedAt
                },
                cancellationToken: ct));
        }
    }

    private async Task ApplyMemoryReinforced(NpgsqlConnection conn, StoredEvent storedEvent, CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<MemoryReinforcedEvent>(storedEvent.EventData, _jsonOptions)!;

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE memory_projections
            SET confidence_score = @Confidence, last_reinforced_at = @LastReinforced,
                validated_by = array_cat(validated_by, @ValidatedBy),
                current_version = @Version, last_event_id = @EventId, updated_at = @UpdatedAt
            WHERE memory_id = @MemoryId",
            new
            {
                MemoryId = storedEvent.StreamId,
                Confidence = (decimal)@event.NewConfidence,
                LastReinforced = storedEvent.CreatedAt,
                ValidatedBy = @event.ValidatedByIds,
                Version = storedEvent.EventVersion,
                EventId = storedEvent.EventId,
                UpdatedAt = storedEvent.CreatedAt
            },
            cancellationToken: ct));
    }

    private async Task ApplyMemoryDecayed(NpgsqlConnection conn, StoredEvent storedEvent, CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<MemoryDecayedEvent>(storedEvent.EventData, _jsonOptions)!;

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE memory_projections
            SET confidence_score = @Confidence,
                current_version = @Version, last_event_id = @EventId, updated_at = @UpdatedAt
            WHERE memory_id = @MemoryId",
            new
            {
                MemoryId = storedEvent.StreamId,
                Confidence = (decimal)@event.NewConfidence,
                Version = storedEvent.EventVersion,
                EventId = storedEvent.EventId,
                UpdatedAt = storedEvent.CreatedAt
            },
            cancellationToken: ct));
    }

    private async Task ApplyMemoryLayerTransitioned(NpgsqlConnection conn, StoredEvent storedEvent, CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<MemoryLayerTransitionedEvent>(storedEvent.EventData, _jsonOptions)!;

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE memory_projections
            SET layer = @Layer::memory_layer,
                current_version = @Version, last_event_id = @EventId, updated_at = @UpdatedAt
            WHERE memory_id = @MemoryId",
            new
            {
                MemoryId = storedEvent.StreamId,
                Layer = @event.NewLayer.ToString(),
                Version = storedEvent.EventVersion,
                EventId = storedEvent.EventId,
                UpdatedAt = storedEvent.CreatedAt
            },
            cancellationToken: ct));
    }

    private async Task ApplyMemoryInvalidated(NpgsqlConnection conn, StoredEvent storedEvent, CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<MemoryInvalidatedEvent>(storedEvent.EventData, _jsonOptions)!;

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE memory_projections
            SET is_active = FALSE, merged_into_id = @SupersededBy,
                contradiction_ids = array_cat(contradiction_ids, @ContradictedBy),
                current_version = @Version, last_event_id = @EventId, updated_at = @UpdatedAt
            WHERE memory_id = @MemoryId",
            new
            {
                MemoryId = storedEvent.StreamId,
                SupersededBy = @event.SupersededById,
                ContradictedBy = @event.ContradictedByIds,
                Version = storedEvent.EventVersion,
                EventId = storedEvent.EventId,
                UpdatedAt = storedEvent.CreatedAt
            },
            cancellationToken: ct));
    }

    private async Task ApplyMemoryMerged(NpgsqlConnection conn, StoredEvent storedEvent, CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<MemoryMergedEvent>(storedEvent.EventData, _jsonOptions)!;

        if (@event.MergedEmbedding != null && @event.MergedEmbedding.Length > 0)
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE memory_projections
                SET content = @Content, content_hash = @ContentHash, embedding = @Embedding,
                    causal_parents = array_cat(causal_parents, @SourceIds),
                    current_version = @Version, last_event_id = @EventId, updated_at = @UpdatedAt
                WHERE memory_id = @MemoryId", conn);

            cmd.Parameters.AddWithValue("@MemoryId", storedEvent.StreamId);
            cmd.Parameters.AddWithValue("@Content", @event.MergedContent);
            cmd.Parameters.AddWithValue("@ContentHash", ComputeHash(@event.MergedContent));
            cmd.Parameters.AddWithValue("@Embedding", new Vector(@event.MergedEmbedding));
            cmd.Parameters.AddWithValue("@SourceIds", @event.SourceMemoryIds);
            cmd.Parameters.AddWithValue("@Version", storedEvent.EventVersion);
            cmd.Parameters.AddWithValue("@EventId", storedEvent.EventId);
            cmd.Parameters.AddWithValue("@UpdatedAt", storedEvent.CreatedAt);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE memory_projections
                SET content = @Content, content_hash = @ContentHash,
                    causal_parents = array_cat(causal_parents, @SourceIds),
                    current_version = @Version, last_event_id = @EventId, updated_at = @UpdatedAt
                WHERE memory_id = @MemoryId",
                new
                {
                    MemoryId = storedEvent.StreamId,
                    Content = @event.MergedContent,
                    ContentHash = ComputeHash(@event.MergedContent),
                    SourceIds = @event.SourceMemoryIds,
                    Version = storedEvent.EventVersion,
                    EventId = storedEvent.EventId,
                    UpdatedAt = storedEvent.CreatedAt
                },
                cancellationToken: ct));
        }
    }

    public async Task<long> GetCheckpointAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT last_processed_sequence FROM projection_checkpoints WHERE projection_name = @Name",
            new { Name = ProjectionName },
            cancellationToken: cancellationToken));
    }

    public async Task SaveCheckpointAsync(long globalSequence, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE projection_checkpoints
            SET last_processed_sequence = @Sequence, last_processed_at = NOW()
            WHERE projection_name = @Name",
            new { Name = ProjectionName, Sequence = globalSequence },
            cancellationToken: cancellationToken));
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
