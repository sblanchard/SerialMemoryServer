using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Debugging;

public sealed class TimeTravelDebugger(
    NpgsqlDataSource dataSource,
    ILogger<TimeTravelDebugger> logger)
    : ITimeTravelDebugger
{
    public async Task<MemorySnapshot> CreateSnapshotAsync(
        string snapshotType,
        CancellationToken cancellationToken = default)
    {
        var snapshotId = Guid.CreateVersion7();
        var snapshotTimestamp = DateTime.UtcNow;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Gather current state statistics
        const string countsSql = @"
            SELECT
                (SELECT COUNT(*) FROM memories) as memory_count,
                (SELECT COUNT(*) FROM entities) as entity_count,
                (SELECT COUNT(*) FROM entity_relationships) as relationship_count,
                (SELECT COUNT(*) FROM memory_clusters WHERE is_stable = TRUE) as cluster_count";

        var counts = await connection.QuerySingleAsync<(long memory_count, long entity_count, long relationship_count, long cluster_count)>(countsSql);

        // Create checkpoint hash from current state
        var checkpointData = $"{snapshotTimestamp:O}:{counts.memory_count}:{counts.entity_count}:{counts.relationship_count}:{counts.cluster_count}";
        var checkpointHash = ComputeHash(checkpointData);

        // Store snapshot record
        const string insertSql = @"
            INSERT INTO memory_snapshots (
                snapshot_id, snapshot_timestamp, snapshot_type,
                memory_count, entity_count, relationship_count, cluster_count,
                checkpoint_hash, metadata
            ) VALUES (
                @SnapshotId, @SnapshotTimestamp, @SnapshotType,
                @MemoryCount, @EntityCount, @RelationshipCount, @ClusterCount,
                @CheckpointHash, '{}'::jsonb
            )";

        await connection.ExecuteAsync(insertSql, new
        {
            SnapshotId = snapshotId,
            SnapshotTimestamp = snapshotTimestamp,
            SnapshotType = snapshotType,
            MemoryCount = (int)counts.memory_count,
            EntityCount = (int)counts.entity_count,
            RelationshipCount = (int)counts.relationship_count,
            ClusterCount = (int)counts.cluster_count,
            CheckpointHash = checkpointHash
        });

        // Capture memory state timeline entries for all active memories
        await CaptureMemoryStatesAsync(connection, snapshotTimestamp, cancellationToken);

        logger.LogInformation(
            "Created {SnapshotType} snapshot {SnapshotId} at {Timestamp}: {Memories} memories, {Entities} entities, {Relationships} relationships",
            snapshotType, snapshotId, snapshotTimestamp,
            counts.memory_count, counts.entity_count, counts.relationship_count);

        return new MemorySnapshot(
            snapshotId,
            snapshotTimestamp,
            snapshotType,
            (int)counts.memory_count,
            (int)counts.entity_count,
            (int)counts.relationship_count,
            (int)counts.cluster_count,
            checkpointHash,
            DateTime.UtcNow,
            null);
    }

    private async Task CaptureMemoryStatesAsync(
        NpgsqlConnection connection,
        DateTime timestamp,
        CancellationToken cancellationToken)
    {
        // Capture current state of all memories into timeline
        const string captureSql = @"
            INSERT INTO memory_state_timeline (
                memory_id, state_at, content, embedding, confidence, layer, is_active
            )
            SELECT
                m.id,
                @Timestamp,
                m.content,
                m.embedding,
                COALESCE(mp.confidence_score, 1.0),
                COALESCE(mp.layer, 'L0_RAW'),
                COALESCE(mp.is_active, TRUE)
            FROM memories m
            LEFT JOIN memory_projections mp ON m.id = mp.memory_id
            ON CONFLICT (memory_id, state_at) DO NOTHING";

        await connection.ExecuteAsync(captureSql, new { Timestamp = timestamp });
    }

    public async Task<MemoryStateAtTime> GetStateAtAsync(
        Guid memoryId,
        DateTime timestamp,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Use the stored function to get state at timestamp
        const string sql = @"
            SELECT memory_id, state_at, content, embedding, confidence, layer, is_active, source_event_id
            FROM memory_state_timeline
            WHERE memory_id = @MemoryId AND state_at <= @Timestamp
            ORDER BY state_at DESC
            LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("MemoryId", memoryId);
        cmd.Parameters.AddWithValue("Timestamp", timestamp);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            // No state found before timestamp, try to get initial state from memories table
            await reader.CloseAsync();

            const string fallbackSql = @"
                SELECT id, created_at, content, embedding
                FROM memories
                WHERE id = @MemoryId AND created_at <= @Timestamp";

            await using var fallbackCmd = new NpgsqlCommand(fallbackSql, connection);
            fallbackCmd.Parameters.AddWithValue("MemoryId", memoryId);
            fallbackCmd.Parameters.AddWithValue("Timestamp", timestamp);

            await using var fallbackReader = await fallbackCmd.ExecuteReaderAsync(cancellationToken);

            if (!await fallbackReader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Memory {memoryId} did not exist at {timestamp:O}");
            }

            var embeddingVector = fallbackReader.IsDBNull(3) ? null : fallbackReader.GetFieldValue<Vector>(3);

            return new MemoryStateAtTime(
                fallbackReader.GetGuid(0),
                fallbackReader.GetDateTime(1),
                fallbackReader.GetString(2),
                embeddingVector?.ToArray(),
                1.0f,
                "L0_RAW",
                true,
                null);
        }

        var stateEmbedding = reader.IsDBNull(3) ? null : reader.GetFieldValue<Vector>(3);

        return new MemoryStateAtTime(
            reader.GetGuid(0),
            reader.GetDateTime(1),
            reader.GetString(2),
            stateEmbedding?.ToArray(),
            reader.GetFloat(4),
            reader.GetString(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7));
    }

    public async Task<GraphStateAtTime> GetGraphStateAtAsync(
        DateTime timestamp,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Get all active memory states at the given timestamp
        const string sql = @"
            WITH latest_states AS (
                SELECT DISTINCT ON (memory_id)
                    memory_id,
                    state_at,
                    content,
                    confidence,
                    layer,
                    is_active
                FROM memory_state_timeline
                WHERE state_at <= @Timestamp
                ORDER BY memory_id, state_at DESC
            )
            SELECT memory_id, state_at, content, confidence, layer
            FROM latest_states
            WHERE is_active = TRUE
            ORDER BY state_at DESC
            LIMIT @Limit";

        var states = new List<MemoryStateAtTime>();

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("Timestamp", timestamp);
        cmd.Parameters.AddWithValue("Limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(new MemoryStateAtTime(
                reader.GetGuid(0),
                reader.GetDateTime(1),
                reader.GetString(2),
                null, // Embedding not loaded for performance
                reader.GetFloat(3),
                reader.GetString(4),
                true,
                null));
        }

        logger.LogDebug(
            "Retrieved graph state at {Timestamp}: {Count} active memories",
            timestamp, states.Count);

        return new GraphStateAtTime(timestamp, states, states.Count);
    }

    public async Task<IReadOnlyList<MemorySnapshot>> GetSnapshotsAsync(
        DateTime? since = null,
        DateTime? until = null,
        string? snapshotType = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT snapshot_id, snapshot_timestamp, snapshot_type,
                   memory_count, entity_count, relationship_count, cluster_count,
                   checkpoint_hash, created_at, metadata::text
            FROM memory_snapshots
            WHERE 1=1";

        var parameters = new DynamicParameters();

        if (since.HasValue)
        {
            sql += " AND snapshot_timestamp >= @Since";
            parameters.Add("Since", since.Value);
        }

        if (until.HasValue)
        {
            sql += " AND snapshot_timestamp <= @Until";
            parameters.Add("Until", until.Value);
        }

        if (!string.IsNullOrEmpty(snapshotType))
        {
            sql += " AND snapshot_type = @SnapshotType";
            parameters.Add("SnapshotType", snapshotType);
        }

        sql += " ORDER BY snapshot_timestamp DESC";

        var rows = await connection.QueryAsync<SnapshotRow>(sql, parameters);

        return rows.Select(r => new MemorySnapshot(
            r.snapshot_id,
            r.snapshot_timestamp,
            r.snapshot_type,
            r.memory_count,
            r.entity_count,
            r.relationship_count,
            r.cluster_count,
            r.checkpoint_hash,
            r.created_at,
            string.IsNullOrEmpty(r.metadata) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(r.metadata))).ToList();
    }

    public async Task<ReasoningReplay> ReplayReasoningAsync(
        Guid sessionId,
        DateTime? untilTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var sql = @"
            SELECT step_id, session_id, step_sequence, step_type, input_hash, output_hash,
                   input_data::text as input_data, output_data::text as output_data,
                   embedding_cache_key, duration_ms, created_at
            FROM reasoning_steps
            WHERE session_id = @SessionId";

        var parameters = new DynamicParameters();
        parameters.Add("SessionId", sessionId);

        if (untilTimestamp.HasValue)
        {
            sql += " AND created_at <= @Until";
            parameters.Add("Until", untilTimestamp.Value);
        }

        sql += " ORDER BY step_sequence";

        var rows = await connection.QueryAsync<dynamic>(sql, parameters);

        var steps = rows.Select(row => new Core.Interfaces.ReasoningStep(
            (Guid)row.step_id,
            (Guid)row.session_id,
            (int)row.step_sequence,
            (string)row.step_type,
            (string)row.input_hash,
            (string)row.output_hash,
            JsonSerializer.Deserialize<object>(row.input_data)!,
            JsonSerializer.Deserialize<object>(row.output_data)!,
            row.embedding_cache_key as string,
            (int)row.duration_ms,
            (DateTime)row.created_at)).ToList();

        // Build final state from steps
        var finalState = new Dictionary<string, object>();
        foreach (var step in steps)
        {
            finalState[$"step_{step.StepSequence}"] = new
            {
                type = step.StepType,
                output = step.OutputData,
                duration_ms = step.DurationMs
            };
        }

        var replayUntil = untilTimestamp ?? (steps.Count > 0 ? steps.Last().CreatedAt : DateTime.UtcNow);

        logger.LogDebug(
            "Replayed reasoning session {SessionId}: {StepCount} steps until {Until}",
            sessionId, steps.Count, replayUntil);

        return new ReasoningReplay(sessionId, replayUntil, steps, finalState);
    }

    /// <summary>
    /// Compare two snapshots to identify changes.
    /// </summary>
    public async Task<SnapshotDiff> CompareSnapshotsAsync(
        Guid snapshotIdA,
        Guid snapshotIdB,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await GetSnapshotsAsync(cancellationToken: cancellationToken);
        var snapshotA = snapshots.FirstOrDefault(s => s.SnapshotId == snapshotIdA);
        var snapshotB = snapshots.FirstOrDefault(s => s.SnapshotId == snapshotIdB);

        if (snapshotA == null || snapshotB == null)
        {
            throw new InvalidOperationException("One or both snapshots not found");
        }

        // Ensure A is before B chronologically
        if (snapshotA.SnapshotTimestamp > snapshotB.SnapshotTimestamp)
        {
            (snapshotA, snapshotB) = (snapshotB, snapshotA);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Find memories added between snapshots
        const string addedMemoriesSql = @"
            SELECT id, content, created_at
            FROM memories
            WHERE created_at > @TimestampA AND created_at <= @TimestampB
            ORDER BY created_at";

        var addedMemories = (await connection.QueryAsync<(Guid id, string content, DateTime created_at)>(
            addedMemoriesSql,
            new { TimestampA = snapshotA.SnapshotTimestamp, TimestampB = snapshotB.SnapshotTimestamp })).ToList();

        // Find memories whose state changed
        const string changedMemoriesSql = @"
            SELECT DISTINCT t1.memory_id,
                   t1.content as old_content, t2.content as new_content,
                   t1.confidence as old_confidence, t2.confidence as new_confidence,
                   t1.layer as old_layer, t2.layer as new_layer,
                   t1.is_active as old_active, t2.is_active as new_active
            FROM memory_state_timeline t1
            JOIN memory_state_timeline t2 ON t1.memory_id = t2.memory_id
            WHERE t1.state_at <= @TimestampA
              AND t2.state_at > @TimestampA AND t2.state_at <= @TimestampB
              AND (t1.content != t2.content
                   OR t1.confidence != t2.confidence
                   OR t1.layer != t2.layer
                   OR t1.is_active != t2.is_active)";

        var changedMemories = (await connection.QueryAsync<dynamic>(
            changedMemoriesSql,
            new { TimestampA = snapshotA.SnapshotTimestamp, TimestampB = snapshotB.SnapshotTimestamp })).ToList();

        return new SnapshotDiff(
            snapshotA.SnapshotId,
            snapshotB.SnapshotId,
            snapshotA.SnapshotTimestamp,
            snapshotB.SnapshotTimestamp,
            snapshotB.MemoryCount - snapshotA.MemoryCount,
            snapshotB.EntityCount - snapshotA.EntityCount,
            snapshotB.RelationshipCount - snapshotA.RelationshipCount,
            addedMemories.Select(m => m.id).ToList(),
            changedMemories.Select(m => (Guid)m.memory_id).ToList());
    }

    /// <summary>
    /// Restore memory state to a specific snapshot.
    /// WARNING: This is a destructive operation.
    /// </summary>
    public async Task<RestoreResult> RestoreToSnapshotAsync(
        Guid snapshotId,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await GetSnapshotsAsync(cancellationToken: cancellationToken);
        var snapshot = snapshots.FirstOrDefault(s => s.SnapshotId == snapshotId);

        if (snapshot == null)
        {
            throw new InvalidOperationException($"Snapshot {snapshotId} not found");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Get state at snapshot time
        var stateAtSnapshot = await GetGraphStateAtAsync(
            snapshot.SnapshotTimestamp,
            10000,
            cancellationToken);

        // Get current state
        var currentState = await GetGraphStateAtAsync(
            DateTime.UtcNow,
            10000,
            cancellationToken);

        // Calculate changes needed
        var memoriesToRestore = stateAtSnapshot.Memories
            .Where(m => !currentState.Memories.Any(c => c.MemoryId == m.MemoryId))
            .ToList();

        var memoriesToArchive = currentState.Memories
            .Where(m => !stateAtSnapshot.Memories.Any(s => s.MemoryId == m.MemoryId))
            .Select(m => m.MemoryId)
            .ToList();

        if (dryRun)
        {
            logger.LogInformation(
                "Dry run restore to snapshot {SnapshotId}: would restore {RestoreCount}, archive {ArchiveCount}",
                snapshotId, memoriesToRestore.Count, memoriesToArchive.Count);

            return new RestoreResult(
                snapshotId,
                snapshot.SnapshotTimestamp,
                memoriesToRestore.Count,
                memoriesToArchive.Count,
                true,
                "Dry run completed - no changes made");
        }

        // Actually perform the restore
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Archive memories created after snapshot
            if (memoriesToArchive.Count > 0)
            {
                const string archiveSql = @"
                    UPDATE memory_projections
                    SET is_archived = TRUE, is_active = FALSE
                    WHERE memory_id = ANY(@MemoryIds)";

                await connection.ExecuteAsync(
                    archiveSql,
                    new { MemoryIds = memoriesToArchive.ToArray() },
                    transaction);
            }

            // Restore confidence/layer for changed memories
            foreach (var state in stateAtSnapshot.Memories)
            {
                const string restoreSql = @"
                    UPDATE memory_projections
                    SET confidence_score = @Confidence,
                        layer = @Layer::memory_layer,
                        is_active = TRUE,
                        is_archived = FALSE
                    WHERE memory_id = @MemoryId";

                await connection.ExecuteAsync(restoreSql, new
                {
                    state.MemoryId,
                    state.Confidence,
                    Layer = state.Layer
                }, transaction);
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Restored to snapshot {SnapshotId}: restored {RestoreCount}, archived {ArchiveCount}",
                snapshotId, memoriesToRestore.Count, memoriesToArchive.Count);

            return new RestoreResult(
                snapshotId,
                snapshot.SnapshotTimestamp,
                memoriesToRestore.Count,
                memoriesToArchive.Count,
                false,
                "Restore completed successfully");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Query memory events to understand what happened to a memory.
    /// </summary>
    public async Task<MemoryHistory> GetMemoryHistoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Get all timeline states for this memory
        const string timelineSql = @"
            SELECT state_at, content, confidence, layer, is_active, source_event_id
            FROM memory_state_timeline
            WHERE memory_id = @MemoryId
            ORDER BY state_at";

        var states = (await connection.QueryAsync<TimelineStateRow>(
            timelineSql,
            new { MemoryId = memoryId })).ToList();

        // Get all events for this memory (if using event sourcing)
        const string eventsSql = @"
            SELECT event_id, event_type::text, event_version, event_data::text, created_at
            FROM memory_events
            WHERE stream_id = @MemoryId
            ORDER BY event_version";

        var events = (await connection.QueryAsync<EventRow>(
            eventsSql,
            new { MemoryId = memoryId })).ToList();

        // Build history
        var historyEntries = new List<MemoryHistoryEntry>();

        foreach (var state in states)
        {
            historyEntries.Add(new MemoryHistoryEntry(
                state.state_at,
                "state_change",
                $"Layer: {state.layer}, Confidence: {state.confidence:F2}, Active: {state.is_active}",
                state.source_event_id));
        }

        foreach (var evt in events)
        {
            var eventData = JsonSerializer.Deserialize<Dictionary<string, object>>(evt.event_data);
            historyEntries.Add(new MemoryHistoryEntry(
                evt.created_at,
                evt.event_type,
                JsonSerializer.Serialize(eventData),
                evt.event_id));
        }

        return new MemoryHistory(
            memoryId,
            historyEntries.OrderBy(e => e.Timestamp).ToList(),
            states.Count,
            events.Count);
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private record SnapshotRow(
        Guid snapshot_id,
        DateTime snapshot_timestamp,
        string snapshot_type,
        int memory_count,
        int entity_count,
        int relationship_count,
        int cluster_count,
        string checkpoint_hash,
        DateTime created_at,
        string? metadata);

    private record TimelineStateRow(
        DateTime state_at,
        string content,
        float confidence,
        string layer,
        bool is_active,
        Guid? source_event_id);

    private record EventRow(
        Guid event_id,
        string event_type,
        long event_version,
        string event_data,
        DateTime created_at);
}

public record SnapshotDiff(
    Guid SnapshotIdA,
    Guid SnapshotIdB,
    DateTime TimestampA,
    DateTime TimestampB,
    int MemoryCountChange,
    int EntityCountChange,
    int RelationshipCountChange,
    IReadOnlyList<Guid> AddedMemoryIds,
    IReadOnlyList<Guid> ChangedMemoryIds);

public record RestoreResult(
    Guid SnapshotId,
    DateTime RestoredToTimestamp,
    int MemoriesRestored,
    int MemoriesArchived,
    bool IsDryRun,
    string Message);

public record MemoryHistory(
    Guid MemoryId,
    IReadOnlyList<MemoryHistoryEntry> Entries,
    int StateCount,
    int EventCount);

public record MemoryHistoryEntry(
    DateTime Timestamp,
    string EntryType,
    string Description,
    Guid? RelatedEventId);
