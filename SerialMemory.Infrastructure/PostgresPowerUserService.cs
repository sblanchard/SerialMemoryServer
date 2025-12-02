using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Power-user service for direct memory manipulation.
/// NO GUARD RAILS. NO SAFE MODE.
/// </summary>
public sealed class PostgresPowerUserService(string connectionString, ILogger<PostgresPowerUserService> logger)
    : IPowerUserService
{
    // ==========================================
    // RAW MEMORY EDITING
    // ==========================================

    public async Task<RawEditResult> EditMemoryContentAsync(
        Guid memoryId,
        string newContent,
        bool skipHashUpdate = false,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var previousHash = await conn.ExecuteScalarAsync<string?>(
            "SELECT content_hash FROM memories WHERE id = @Id", new { Id = memoryId });

        var newHash = skipHashUpdate ? previousHash : ComputeHash(newContent);

        var rows = await conn.ExecuteAsync("""
            UPDATE memories
            SET content = @Content,
                content_hash = @Hash,
                updated_at = NOW()
            WHERE id = @Id
            """, new { Id = memoryId, Content = newContent, Hash = newHash });

        logger.LogWarning("[POWER-USER] Direct content edit on {MemoryId}, skipHash={Skip}", memoryId, skipHashUpdate);

        return new RawEditResult
        {
            MemoryId = memoryId,
            Success = rows > 0,
            PreviousHash = previousHash,
            NewHash = newHash,
            Changes = new() { ["content"] = newContent.Length > 100 ? newContent[..100] + "..." : newContent }
        };
    }

    public async Task<RawEditResult> EditMemoryMetadataAsync(
        Guid memoryId,
        MemoryMetadataUpdate update,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var changes = new Dictionary<string, object>();
        var setClauses = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("Id", memoryId);

        if (update.Layer != null)
        {
            setClauses.Add("layer = @Layer::memory_layer");
            parameters.Add("Layer", update.Layer);
            changes["layer"] = update.Layer;
        }

        if (update.Source != null)
        {
            setClauses.Add("source = @Source");
            parameters.Add("Source", update.Source);
            changes["source"] = update.Source;
        }

        if (update.IsActive.HasValue)
        {
            setClauses.Add("is_active = @IsActive");
            parameters.Add("IsActive", update.IsActive.Value);
            changes["is_active"] = update.IsActive.Value;
        }

        if (update.CausalParents != null)
        {
            setClauses.Add("causal_parents = @CausalParents");
            parameters.Add("CausalParents", update.CausalParents.ToArray());
            changes["causal_parents"] = update.CausalParents;
        }

        if (update.HalfLifeDays.HasValue)
        {
            setClauses.Add("half_life_days = @HalfLifeDays");
            parameters.Add("HalfLifeDays", update.HalfLifeDays.Value);
            changes["half_life_days"] = update.HalfLifeDays.Value;
        }

        if (update.LastReinforcedAt.HasValue)
        {
            setClauses.Add("last_reinforced_at = @LastReinforcedAt");
            parameters.Add("LastReinforcedAt", update.LastReinforcedAt.Value);
            changes["last_reinforced_at"] = update.LastReinforcedAt.Value;
        }

        if (setClauses.Count == 0)
        {
            return new RawEditResult { MemoryId = memoryId, Success = false, Error = "No changes specified" };
        }

        setClauses.Add("updated_at = NOW()");
        var sql = $"UPDATE memories SET {string.Join(", ", setClauses)} WHERE id = @Id";

        var rows = await conn.ExecuteAsync(sql, parameters);

        logger.LogWarning("[POWER-USER] Metadata edit on {MemoryId}: {Changes}", memoryId, string.Join(", ", changes.Keys));

        return new RawEditResult
        {
            MemoryId = memoryId,
            Success = rows > 0,
            Changes = changes
        };
    }

    public async Task<RawEditResult> ForceConfidenceAsync(
        Guid memoryId,
        float confidence,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var previousConfidence = await conn.ExecuteScalarAsync<float?>(
            "SELECT confidence_score FROM memories WHERE id = @Id", new { Id = memoryId });

        var rows = await conn.ExecuteAsync("""
            UPDATE memories
            SET confidence_score = @Confidence,
                last_reinforced_at = NOW(),
                updated_at = NOW()
            WHERE id = @Id
            """, new { Id = memoryId, Confidence = confidence });

        logger.LogWarning("[POWER-USER] Force confidence on {MemoryId}: {Prev} -> {New}",
            memoryId, previousConfidence, confidence);

        return new RawEditResult
        {
            MemoryId = memoryId,
            Success = rows > 0,
            Changes = new()
            {
                ["previous_confidence"] = previousConfidence ?? 0f,
                ["new_confidence"] = confidence
            }
        };
    }

    public async Task<RawEditResult> ReplaceEmbeddingAsync(
        Guid memoryId,
        float[] embedding,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var vectorString = $"[{string.Join(",", embedding)}]";

        var rows = await conn.ExecuteAsync("""
            UPDATE memories
            SET embedding = @Embedding::vector,
                updated_at = NOW()
            WHERE id = @Id
            """, new { Id = memoryId, Embedding = vectorString });

        logger.LogWarning("[POWER-USER] Embedding replacement on {MemoryId}, dim={Dim}", memoryId, embedding.Length);

        return new RawEditResult
        {
            MemoryId = memoryId,
            Success = rows > 0,
            Changes = new()
            {
                ["embedding_dimension"] = embedding.Length,
                ["embedding_preview"] = embedding.Take(5).ToArray()
            }
        };
    }

    public async Task<RawEditResult> HardDeleteMemoryAsync(
        Guid memoryId,
        bool cascade = false,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var deletedRows = new Dictionary<string, int>();

            if (cascade)
            {
                // Delete related data
                deletedRows["memory_entities"] = await conn.ExecuteAsync(
                    "DELETE FROM memory_entities WHERE memory_id = @Id", new { Id = memoryId }, tx);

                deletedRows["memory_events"] = await conn.ExecuteAsync(
                    "DELETE FROM memory_events WHERE memory_id = @Id", new { Id = memoryId }, tx);

                deletedRows["security_events"] = await conn.ExecuteAsync(
                    "DELETE FROM security_events WHERE memory_id = @Id", new { Id = memoryId }, tx);
            }

            deletedRows["memories"] = await conn.ExecuteAsync(
                "DELETE FROM memories WHERE id = @Id", new { Id = memoryId }, tx);

            await tx.CommitAsync(ct);

            logger.LogWarning("[POWER-USER] HARD DELETE on {MemoryId}, cascade={Cascade}, deleted={Rows}",
                memoryId, cascade, JsonSerializer.Serialize(deletedRows));

            return new RawEditResult
            {
                MemoryId = memoryId,
                Success = deletedRows["memories"] > 0,
                Changes = deletedRows.ToDictionary(x => x.Key, x => (object)x.Value)
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new RawEditResult { MemoryId = memoryId, Success = false, Error = ex.Message };
        }
    }

    public async Task<BatchEditResult> BatchEditAsync(
        List<RawMemoryEdit> edits,
        CancellationToken ct = default)
    {
        var results = new List<RawEditResult>();

        foreach (var edit in edits)
        {
            RawEditResult result;

            if (edit.HardDelete)
            {
                result = await HardDeleteMemoryAsync(edit.MemoryId, cascade: true, ct);
            }
            else if (edit.NewContent != null)
            {
                result = await EditMemoryContentAsync(edit.MemoryId, edit.NewContent, ct: ct);
            }
            else if (edit.NewConfidence.HasValue)
            {
                result = await ForceConfidenceAsync(edit.MemoryId, edit.NewConfidence.Value, ct);
            }
            else if (edit.MetadataUpdate != null)
            {
                result = await EditMemoryMetadataAsync(edit.MemoryId, edit.MetadataUpdate, ct);
            }
            else
            {
                result = new RawEditResult { MemoryId = edit.MemoryId, Success = false, Error = "No operation specified" };
            }

            results.Add(result);
        }

        return new BatchEditResult
        {
            TotalRequested = edits.Count,
            Succeeded = results.Count(r => r.Success),
            Failed = results.Count(r => !r.Success),
            Results = results
        };
    }

    // ==========================================
    // CONFLICT OVERLAYS
    // ==========================================

    public async Task<ConflictOverlay> GetConflictOverlayAsync(
        int? limit = null,
        float? minSeverity = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var conflicts = await conn.QueryAsync<ConflictRow>("""
            SELECT
                c.id AS conflict_id,
                c.memory_a_id,
                c.memory_b_id,
                ma.content AS content_a,
                mb.content AS content_b,
                c.severity,
                c.conflict_type,
                c.reason,
                c.semantic_similarity,
                c.detected_at,
                c.is_resolved,
                c.resolved_by,
                c.resolution
            FROM memory_conflicts c
            LEFT JOIN memories ma ON c.memory_a_id = ma.id
            LEFT JOIN memories mb ON c.memory_b_id = mb.id
            WHERE (@MinSeverity IS NULL OR c.severity >= @MinSeverity)
            ORDER BY c.severity DESC, c.detected_at DESC
            LIMIT @Limit
            """, new { Limit = limit ?? 100, MinSeverity = minSeverity });

        var conflictList = conflicts.Select(c => new MemoryConflict
        {
            ConflictId = c.conflict_id,
            MemoryA = c.memory_a_id,
            MemoryB = c.memory_b_id,
            ContentA = c.content_a,
            ContentB = c.content_b,
            Severity = c.severity,
            ConflictType = c.conflict_type ?? "contradiction",
            Reason = c.reason,
            SemanticSimilarity = c.semantic_similarity,
            DetectedAt = c.detected_at,
            IsResolved = c.is_resolved,
            ResolvedBy = c.resolved_by,
            Resolution = c.resolution
        }).ToList();

        var counts = await conn.QueryFirstAsync<(int total, int critical, int high, int medium, int low)>("""
            SELECT
                COUNT(*)::int AS total,
                COUNT(*) FILTER (WHERE severity >= 0.9)::int AS critical,
                COUNT(*) FILTER (WHERE severity >= 0.7 AND severity < 0.9)::int AS high,
                COUNT(*) FILTER (WHERE severity >= 0.4 AND severity < 0.7)::int AS medium,
                COUNT(*) FILTER (WHERE severity < 0.4)::int AS low
            FROM memory_conflicts
            WHERE NOT is_resolved
            """);

        var byType = await conn.QueryAsync<(string type, int count)>("""
            SELECT conflict_type AS type, COUNT(*)::int AS count
            FROM memory_conflicts
            WHERE NOT is_resolved
            GROUP BY conflict_type
            """);

        return new ConflictOverlay
        {
            Conflicts = conflictList,
            TotalConflicts = counts.total,
            CriticalCount = counts.critical,
            HighCount = counts.high,
            MediumCount = counts.medium,
            LowCount = counts.low,
            ConflictsByType = byType.ToDictionary(x => x.type ?? "unknown", x => x.count),
            Clusters = await GetConflictClustersAsync(conn, ct)
        };
    }

    public async Task<List<MemoryConflict>> GetConflictsAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var conflicts = await conn.QueryAsync<ConflictRow>("""
            SELECT
                c.id AS conflict_id,
                c.memory_a_id,
                c.memory_b_id,
                ma.content AS content_a,
                mb.content AS content_b,
                c.severity,
                c.conflict_type,
                c.reason,
                c.semantic_similarity,
                c.detected_at,
                c.is_resolved,
                c.resolved_by,
                c.resolution
            FROM memory_conflicts c
            LEFT JOIN memories ma ON c.memory_a_id = ma.id
            LEFT JOIN memories mb ON c.memory_b_id = mb.id
            ORDER BY c.severity DESC, c.detected_at DESC
            LIMIT @Limit
            """, new { Limit = limit });

        return conflicts.Select(c => new MemoryConflict
        {
            ConflictId = c.conflict_id,
            MemoryA = c.memory_a_id,
            MemoryB = c.memory_b_id,
            ContentA = c.content_a,
            ContentB = c.content_b,
            Severity = c.severity,
            ConflictType = c.conflict_type ?? "contradiction",
            Reason = c.reason,
            SemanticSimilarity = c.semantic_similarity,
            DetectedAt = c.detected_at,
            IsResolved = c.is_resolved,
            ResolvedBy = c.resolved_by,
            Resolution = c.resolution
        }).ToList();
    }

    public async Task ResolveConflictAsync(
        Guid conflictId,
        string resolution,
        Guid? winnerId = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync("""
            UPDATE memory_conflicts
            SET is_resolved = TRUE,
                resolved_by = @WinnerId,
                resolution = @Resolution,
                resolved_at = NOW()
            WHERE id = @Id
            """, new { Id = conflictId, WinnerId = winnerId, Resolution = resolution });

        logger.LogWarning("[POWER-USER] Resolved conflict {ConflictId}: resolution={Resolution}, winner={Winner}",
            conflictId, resolution, winnerId);
    }

    private async Task<List<ConflictCluster>> GetConflictClustersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Find memories that appear in multiple conflicts
        var clusters = await conn.QueryAsync<(Guid memory_id, int conflict_count, float max_severity)>("""
            SELECT
                memory_id,
                COUNT(*)::int AS conflict_count,
                MAX(severity) AS max_severity
            FROM (
                SELECT memory_a_id AS memory_id, severity FROM memory_conflicts WHERE NOT is_resolved
                UNION ALL
                SELECT memory_b_id AS memory_id, severity FROM memory_conflicts WHERE NOT is_resolved
            ) AS all_conflicts
            GROUP BY memory_id
            HAVING COUNT(*) > 1
            ORDER BY max_severity DESC, conflict_count DESC
            LIMIT 10
            """);

        return clusters.Select(c => new ConflictCluster
        {
            MemoryIds = [c.memory_id],
            ConflictCount = c.conflict_count,
            MaxSeverity = c.max_severity
        }).ToList();
    }

    public async Task<List<MemoryConflict>> GetMemoryConflictsAsync(
        Guid memoryId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var conflicts = await conn.QueryAsync<ConflictRow>("""
            SELECT
                c.id AS conflict_id,
                c.memory_a_id,
                c.memory_b_id,
                ma.content AS content_a,
                mb.content AS content_b,
                c.severity,
                c.conflict_type,
                c.reason,
                c.semantic_similarity,
                c.detected_at,
                c.is_resolved,
                c.resolved_by,
                c.resolution
            FROM memory_conflicts c
            LEFT JOIN memories ma ON c.memory_a_id = ma.id
            LEFT JOIN memories mb ON c.memory_b_id = mb.id
            WHERE c.memory_a_id = @Id OR c.memory_b_id = @Id
            ORDER BY c.severity DESC
            """, new { Id = memoryId });

        return conflicts.Select(c => new MemoryConflict
        {
            ConflictId = c.conflict_id,
            MemoryA = c.memory_a_id,
            MemoryB = c.memory_b_id,
            ContentA = c.content_a,
            ContentB = c.content_b,
            Severity = c.severity,
            ConflictType = c.conflict_type ?? "contradiction",
            Reason = c.reason,
            SemanticSimilarity = c.semantic_similarity,
            DetectedAt = c.detected_at,
            IsResolved = c.is_resolved,
            ResolvedBy = c.resolved_by,
            Resolution = c.resolution
        }).ToList();
    }

    public async Task<ConflictResolution> ForceResolveConflictAsync(
        Guid conflictId,
        Guid winnerId,
        ConflictResolutionAction action,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var conflict = await conn.QueryFirstOrDefaultAsync<(Guid a, Guid b)>(
                "SELECT memory_a_id AS a, memory_b_id AS b FROM memory_conflicts WHERE id = @Id",
                new { Id = conflictId }, tx);

            if (conflict == default)
            {
                return new ConflictResolution { ConflictId = conflictId, Success = false, Error = "Conflict not found" };
            }

            var loserId = winnerId == conflict.a ? conflict.b : conflict.a;

            switch (action)
            {
                case ConflictResolutionAction.InvalidateLoser:
                    await conn.ExecuteAsync(
                        "UPDATE memories SET is_active = FALSE WHERE id = @Id",
                        new { Id = loserId }, tx);
                    break;

                case ConflictResolutionAction.DeleteBoth:
                    await conn.ExecuteAsync(
                        "DELETE FROM memories WHERE id IN (@A, @B)",
                        new { A = conflict.a, B = conflict.b }, tx);
                    break;

                case ConflictResolutionAction.MarkAsAlternatives:
                    await conn.ExecuteAsync("""
                        UPDATE memories SET
                            metadata = COALESCE(metadata, '{}'::jsonb) ||
                                       jsonb_build_object('alternative_to', @AltId::text)
                        WHERE id = @Id
                        """, new { Id = loserId, AltId = winnerId }, tx);
                    break;
            }

            await conn.ExecuteAsync("""
                UPDATE memory_conflicts
                SET is_resolved = TRUE,
                    resolved_by = @WinnerId,
                    resolution = @Resolution,
                    resolved_at = NOW()
                WHERE id = @Id
                """, new { Id = conflictId, WinnerId = winnerId, Resolution = action.ToString() }, tx);

            await tx.CommitAsync(ct);

            logger.LogWarning("[POWER-USER] Force resolved conflict {ConflictId}: winner={Winner}, action={Action}",
                conflictId, winnerId, action);

            return new ConflictResolution
            {
                ConflictId = conflictId,
                WinnerId = winnerId,
                Action = action,
                Success = true
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new ConflictResolution { ConflictId = conflictId, Success = false, Error = ex.Message };
        }
    }

    public async Task<MemoryConflict> FlagContradictionAsync(
        Guid memoryA,
        Guid memoryB,
        float severity,
        string? reason = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var conflictId = Guid.CreateVersion7();

        await conn.ExecuteAsync("""
            INSERT INTO memory_conflicts (id, memory_a_id, memory_b_id, severity, conflict_type, reason, detected_at)
            VALUES (@Id, @A, @B, @Severity, 'manual_flag', @Reason, NOW())
            ON CONFLICT (memory_a_id, memory_b_id) DO UPDATE SET
                severity = GREATEST(memory_conflicts.severity, @Severity),
                reason = COALESCE(@Reason, memory_conflicts.reason)
            """, new { Id = conflictId, A = memoryA, B = memoryB, Severity = severity, Reason = reason });

        logger.LogWarning("[POWER-USER] Manual contradiction flag: {A} <-> {B}, severity={Severity}",
            memoryA, memoryB, severity);

        return new MemoryConflict
        {
            ConflictId = conflictId,
            MemoryA = memoryA,
            MemoryB = memoryB,
            Severity = severity,
            ConflictType = "manual_flag",
            Reason = reason
        };
    }

    // ==========================================
    // RAW TRACE VIEWERS
    // ==========================================

    public async Task<RawEventTrace> GetRawTraceAsync(
        Guid memoryId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Use correct column names for memory_events table
        var events = await conn.QueryAsync<EventRow>("""
            SELECT
                event_id,
                global_sequence AS sequence_number,
                event_type::text AS event_type,
                stream_id AS memory_id,
                created_at AS timestamp,
                event_data::text AS raw_payload,
                created_by AS actor_id,
                metadata->>'correlation_id' AS correlation_id
            FROM memory_events
            WHERE stream_id = @Id
            ORDER BY global_sequence
            """, new { Id = memoryId });

        var eventList = events.Select(e => new RawEvent
        {
            EventId = e.event_id,
            SequenceNumber = e.sequence_number,
            EventType = e.event_type ?? "",
            MemoryId = e.memory_id,
            Timestamp = e.timestamp,
            RawPayload = e.raw_payload ?? "{}",
            ParsedPayload = TryParseJson(e.raw_payload),
            ActorId = e.actor_id,
            CorrelationId = e.correlation_id
        }).ToList();

        var typeCounts = eventList
            .GroupBy(e => e.EventType)
            .ToDictionary(g => g.Key, g => g.Count());

        return new RawEventTrace
        {
            MemoryId = memoryId,
            Events = eventList,
            TotalEvents = eventList.Count,
            FirstEvent = eventList.FirstOrDefault()?.Timestamp,
            LastEvent = eventList.LastOrDefault()?.Timestamp,
            EventTypeCounts = typeCounts
        };
    }

    public async Task<FullTraceDetail> GetFullTraceAsync(
        Guid memoryId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // 1. Get memory details
        var memory = await conn.QueryFirstOrDefaultAsync<MemoryRow>("""
            SELECT id, content, layer, source, content_hash,
                   parent_memory_ids, created_at, updated_at, metadata
            FROM memories
            WHERE id = @Id
            """, new { Id = memoryId });

        if (memory == null)
        {
            return new FullTraceDetail
            {
                MemoryId = memoryId,
                Content = "[Memory not found]",
                RawJson = "{\"error\": \"Memory not found\"}"
            };
        }

        // 2. Get events from memory_events
        var events = await conn.QueryAsync<EventRow>("""
            SELECT
                event_id,
                global_sequence AS sequence_number,
                event_type::text AS event_type,
                stream_id AS memory_id,
                created_at AS timestamp,
                event_data::text AS raw_payload,
                created_by AS actor_id,
                metadata->>'reason' AS correlation_id
            FROM memory_events
            WHERE stream_id = @Id
            ORDER BY global_sequence
            """, new { Id = memoryId });

        var eventList = events.Select(e => new TraceEventDetail
        {
            EventId = e.event_id,
            SequenceNumber = e.sequence_number,
            EventType = e.event_type ?? "",
            Timestamp = e.timestamp,
            ActorId = e.actor_id,
            Reason = e.correlation_id,
            PayloadJson = e.raw_payload ?? "{}"
        }).ToList();

        // 3. Get descendants (memories that have this memory as parent)
        var descendants = await conn.QueryAsync<Guid>("""
            SELECT id FROM memories
            WHERE @Id = ANY(parent_memory_ids)
            """, new { Id = memoryId });

        // 4. Get timeline snapshots
        var timeline = await conn.QueryAsync<TimelineRow>("""
            SELECT id, snapshot_at, event_type, layer, confidence_score, content
            FROM memory_timeline
            WHERE memory_id = @Id
            ORDER BY snapshot_at DESC
            LIMIT 20
            """, new { Id = memoryId });

        var timelineList = timeline.Select(t => new TimelineSnapshot
        {
            Id = t.id,
            SnapshotAt = t.snapshot_at,
            EventType = t.event_type ?? "",
            Layer = t.layer ?? "",
            Confidence = t.confidence_score,
            ContentPreview = t.content?.Length > 200 ? t.content[..200] + "..." : t.content
        }).ToList();

        // 5. Get conflicts involving this memory
        var conflicts = await conn.QueryAsync<ConflictRow>("""
            SELECT id AS conflict_id, memory_a_id, memory_b_id, severity, conflict_type, is_resolved
            FROM memory_conflicts
            WHERE memory_a_id = @Id OR memory_b_id = @Id
            ORDER BY severity DESC
            LIMIT 10
            """, new { Id = memoryId });

        var conflictList = conflicts.Select(c => new ConflictSummary
        {
            ConflictId = c.conflict_id,
            OtherMemoryId = c.memory_a_id == memoryId ? c.memory_b_id : c.memory_a_id,
            Severity = c.severity,
            ConflictType = c.conflict_type ?? "unknown",
            IsResolved = c.is_resolved
        }).ToList();

        // Build raw JSON
        var rawJson = JsonSerializer.Serialize(new
        {
            id = memoryId,
            content = memory.content,
            layer = memory.layer,
            source = memory.source,
            content_hash = memory.content_hash,
            parent_memory_ids = memory.parent_memory_ids,
            created_at = memory.created_at,
            updated_at = memory.updated_at,
            metadata = memory.metadata
        }, new JsonSerializerOptions { WriteIndented = true });

        return new FullTraceDetail
        {
            MemoryId = memoryId,
            Content = memory.content ?? "",
            Layer = memory.layer ?? "L0_RAW",
            Confidence = 1.0, // Default if not available
            IsActive = true,
            ContentHash = memory.content_hash,
            Source = memory.source,
            CreatedAt = memory.created_at,
            UpdatedAt = memory.updated_at,
            Events = eventList,
            CausalParents = memory.parent_memory_ids?.ToList() ?? [],
            Descendants = descendants.ToList(),
            Timeline = timelineList,
            Conflicts = conflictList,
            RawJson = rawJson
        };
    }

    private sealed class MemoryRow
    {
        public Guid id { get; init; }
        public string? content { get; init; }
        public string? layer { get; init; }
        public string? source { get; init; }
        public string? content_hash { get; init; }
        public Guid[]? parent_memory_ids { get; init; }
        public DateTimeOffset created_at { get; init; }
        public DateTimeOffset? updated_at { get; init; }
        public string? metadata { get; init; }
    }

    private sealed class TimelineRow
    {
        public Guid id { get; init; }
        public DateTimeOffset snapshot_at { get; init; }
        public string? event_type { get; init; }
        public string? layer { get; init; }
        public decimal confidence_score { get; init; }
        public string? content { get; init; }
    }

    public async Task<List<RawEvent>> GetRawEventsByTypeAsync(
        string eventType,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Use correct column names for memory_events table
        var events = await conn.QueryAsync<EventRow>("""
            SELECT
                event_id,
                global_sequence AS sequence_number,
                event_type::text AS event_type,
                stream_id AS memory_id,
                created_at AS timestamp,
                event_data::text AS raw_payload,
                created_by AS actor_id,
                metadata->>'correlation_id' AS correlation_id
            FROM memory_events
            WHERE event_type::text = @Type
            ORDER BY global_sequence DESC
            LIMIT @Limit
            """, new { Type = eventType, Limit = limit });

        return events.Select(e => new RawEvent
        {
            EventId = e.event_id,
            SequenceNumber = e.sequence_number,
            EventType = e.event_type ?? "",
            MemoryId = e.memory_id,
            Timestamp = e.timestamp,
            RawPayload = e.raw_payload ?? "{}",
            ParsedPayload = TryParseJson(e.raw_payload),
            ActorId = e.actor_id,
            CorrelationId = e.correlation_id
        }).ToList();
    }

    public async Task<List<RawEvent>> GetEventStreamAsync(
        long fromSequence = 0,
        int limit = 1000,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var events = await conn.QueryAsync<EventRow>("""
            SELECT
                event_id,
                global_sequence AS sequence_number,
                event_type::text AS event_type,
                stream_id AS memory_id,
                created_at AS timestamp,
                event_data::text AS raw_payload,
                created_by AS actor_id,
                metadata->>'correlation_id' AS correlation_id
            FROM memory_events
            WHERE global_sequence > @From
            ORDER BY global_sequence
            LIMIT @Limit
            """, new { From = fromSequence, Limit = limit });

        return events.Select(e => new RawEvent
        {
            EventId = e.event_id,
            SequenceNumber = e.sequence_number,
            EventType = e.event_type ?? "",
            MemoryId = e.memory_id,
            Timestamp = e.timestamp,
            RawPayload = e.raw_payload ?? "{}",
            ParsedPayload = TryParseJson(e.raw_payload),
            ActorId = e.actor_id,
            CorrelationId = e.correlation_id
        }).ToList();
    }

    public async Task<RawQueryResult> ExecuteRawQueryAsync(
        string sql,
        Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        logger.LogWarning("[POWER-USER] RAW SQL EXECUTION: {Sql}", sql);

        try
        {
            var dynamicParams = new DynamicParameters();
            if (parameters != null)
            {
                foreach (var (key, value) in parameters)
                {
                    dynamicParams.Add(key, value);
                }
            }

            // Try as query first
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var rows = (await conn.QueryAsync(sql, dynamicParams)).ToList();
                sw.Stop();

                var resultRows = rows.Select(r => ((IDictionary<string, object>)r)
                    .ToDictionary(x => x.Key, x => x.Value)).ToList();

                var columns = resultRows.FirstOrDefault()?.Keys.ToList() ?? [];

                return new RawQueryResult
                {
                    Success = true,
                    Rows = resultRows,
                    Columns = columns,
                    RowsAffected = rows.Count,
                    ExecutionTimeMs = sw.ElapsedMilliseconds,
                    Query = sql
                };
            }
            else
            {
                // Execute as command
                var affected = await conn.ExecuteAsync(sql, dynamicParams);
                sw.Stop();

                return new RawQueryResult
                {
                    Success = true,
                    RowsAffected = affected,
                    ExecutionTimeMs = sw.ElapsedMilliseconds,
                    Query = sql
                };
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RawQueryResult
            {
                Success = false,
                Error = ex.Message,
                ExecutionTimeMs = sw.ElapsedMilliseconds,
                Query = sql
            };
        }
    }

    // ==========================================
    // LIVE GRAPH MUTATIONS
    // ==========================================

    public async Task<MutationFeed> GetMutationFeedAsync(
        DateTimeOffset? since = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var mutations = await conn.QueryAsync<MutationRow>("""
            SELECT
                event_id AS mutation_id,
                0::bigint AS sequence_number,
                event_type AS mutation_type,
                node_id,
                edge_id,
                source_node_id,
                target_node_id,
                node_name,
                edge_type,
                occurred_at AS timestamp,
                previous_state::text,
                new_state::text,
                triggered_by
            FROM graph_events
            WHERE (@Since IS NULL OR occurred_at > @Since)
            ORDER BY occurred_at DESC
            LIMIT @Limit
            """, new { Since = since, Limit = limit });

        var mutationList = mutations.Select(m => new GraphMutation
        {
            MutationId = m.mutation_id,
            SequenceNumber = m.sequence_number,
            MutationType = m.mutation_type ?? "",
            NodeId = m.node_id,
            EdgeId = m.edge_id,
            SourceNodeId = m.source_node_id,
            TargetNodeId = m.target_node_id,
            NodeName = m.node_name,
            EdgeType = m.edge_type,
            Timestamp = m.timestamp,
            PreviousState = TryParseJson(m.previous_state),
            NewState = TryParseJson(m.new_state),
            TriggeredBy = m.triggered_by
        }).ToList();

        var byType = mutationList
            .GroupBy(m => m.MutationType)
            .ToDictionary(g => g.Key, g => g.Count());

        return new MutationFeed
        {
            Mutations = mutationList,
            FromSequence = mutationList.LastOrDefault()?.SequenceNumber ?? 0,
            ToSequence = mutationList.FirstOrDefault()?.SequenceNumber ?? 0,
            TotalMutations = mutationList.Count,
            MutationsByType = byType
        };
    }

    public async Task<GraphMutation> ForceCreateEdgeAsync(
        Guid sourceId,
        Guid targetId,
        string edgeType,
        float confidence = 1.0f,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var edgeId = Guid.CreateVersion7();

        await conn.ExecuteAsync("""
            INSERT INTO entity_relationships (id, source_entity_id, target_entity_id, relationship_type, confidence, created_at)
            VALUES (@Id, @Source, @Target, @Type, @Confidence, NOW())
            """, new { Id = edgeId, Source = sourceId, Target = targetId, Type = edgeType, Confidence = confidence });

        logger.LogWarning("[POWER-USER] Force edge creation: {Source} --[{Type}]--> {Target}", sourceId, edgeType, targetId);

        return new GraphMutation
        {
            EdgeId = edgeId,
            SourceNodeId = sourceId,
            TargetNodeId = targetId,
            EdgeType = edgeType,
            MutationType = "EdgeCreated",
            NewState = new() { ["confidence"] = confidence }
        };
    }

    public async Task<GraphMutation> ForceDeleteNodeAsync(
        Guid nodeId,
        bool cascadeEdges = true,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var previousState = new Dictionary<string, object>();

            if (cascadeEdges)
            {
                var deletedEdges = await conn.ExecuteAsync("""
                    DELETE FROM entity_relationships
                    WHERE source_entity_id = @Id OR target_entity_id = @Id
                    """, new { Id = nodeId }, tx);
                previousState["deleted_edges"] = deletedEdges;
            }

            var deletedNodes = await conn.ExecuteAsync(
                "DELETE FROM entities WHERE id = @Id", new { Id = nodeId }, tx);

            await tx.CommitAsync(ct);

            logger.LogWarning("[POWER-USER] Force node deletion: {NodeId}, cascade={Cascade}", nodeId, cascadeEdges);

            return new GraphMutation
            {
                NodeId = nodeId,
                MutationType = "NodeDeleted",
                PreviousState = previousState,
                NewState = new() { ["deleted"] = true }
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            throw new InvalidOperationException($"Failed to delete node: {ex.Message}", ex);
        }
    }

    public async Task<List<GraphMutation>> ReplayMutationsAsync(
        long fromSequence,
        long toSequence,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var mutations = await conn.QueryAsync<MutationRow>("""
            SELECT
                event_id AS mutation_id,
                sequence_number,
                event_type AS mutation_type,
                node_id,
                edge_id,
                source_node_id,
                target_node_id,
                node_name,
                edge_type,
                occurred_at AS timestamp,
                previous_state::text,
                new_state::text,
                triggered_by
            FROM graph_events
            WHERE sequence_number >= @From AND sequence_number <= @To
            ORDER BY sequence_number
            """, new { From = fromSequence, To = toSequence });

        return mutations.Select(m => new GraphMutation
        {
            MutationId = m.mutation_id,
            SequenceNumber = m.sequence_number,
            MutationType = m.mutation_type ?? "",
            NodeId = m.node_id,
            EdgeId = m.edge_id,
            SourceNodeId = m.source_node_id,
            TargetNodeId = m.target_node_id,
            NodeName = m.node_name,
            EdgeType = m.edge_type,
            Timestamp = m.timestamp,
            PreviousState = TryParseJson(m.previous_state),
            NewState = TryParseJson(m.new_state),
            TriggeredBy = m.triggered_by
        }).ToList();
    }

    // ==========================================
    // HELPERS
    // ==========================================

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private static Dictionary<string, object> TryParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? [];
        }
        catch
        {
            return new() { ["raw"] = json };
        }
    }

    // Row types
    private sealed class ConflictRow
    {
        public Guid conflict_id { get; init; }
        public Guid memory_a_id { get; init; }
        public Guid memory_b_id { get; init; }
        public string? content_a { get; init; }
        public string? content_b { get; init; }
        public float severity { get; init; }
        public string? conflict_type { get; init; }
        public string? reason { get; init; }
        public float semantic_similarity { get; init; }
        public DateTimeOffset detected_at { get; init; }
        public bool is_resolved { get; init; }
        public Guid? resolved_by { get; init; }
        public string? resolution { get; init; }
    }

    private sealed class EventRow
    {
        public Guid event_id { get; init; }
        public long sequence_number { get; init; }
        public string? event_type { get; init; }
        public Guid? memory_id { get; init; }
        public DateTimeOffset timestamp { get; init; }
        public string? raw_payload { get; init; }
        public string? actor_id { get; init; }
        public string? correlation_id { get; init; }
    }

    private sealed class MutationRow
    {
        public Guid mutation_id { get; init; }
        public long sequence_number { get; init; }
        public string? mutation_type { get; init; }
        public Guid? node_id { get; init; }
        public Guid? edge_id { get; init; }
        public Guid? source_node_id { get; init; }
        public Guid? target_node_id { get; init; }
        public string? node_name { get; init; }
        public string? edge_type { get; init; }
        public DateTimeOffset timestamp { get; init; }
        public string? previous_state { get; init; }
        public string? new_state { get; init; }
        public string? triggered_by { get; init; }
    }
}
