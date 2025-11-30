using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// PostgreSQL implementation of shadow memory store.
/// Completely isolated from main memory - never pollutes until explicit promotion.
/// </summary>
public sealed class PostgresShadowMemoryStore : IShadowMemoryStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresShadowMemoryStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PostgresShadowMemoryStore(string connectionString, ILogger<PostgresShadowMemoryStore> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    /// <summary>
    /// Opens a connection with internal admin role to bypass RLS for shadow operations.
    /// Shadow memory operations are trusted internal operations.
    /// </summary>
    private async Task<NpgsqlConnection> OpenInternalConnectionAsync(CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        return conn;
    }

    /// <summary>
    /// Opens a connection with tenant context for RLS.
    /// </summary>
    private async Task<NpgsqlConnection> OpenTenantConnectionAsync(string tenantId, CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            """
            SELECT set_config('app.tenant_id', @TenantId, false);
            SELECT set_config('app.current_tenant_id', @TenantId, false);
            """,
            new { TenantId = tenantId });
        return conn;
    }

    #region Branch Operations

    public async Task<Guid> CreateBranchAsync(ShadowBranch branch, CancellationToken ct = default)
    {
        await using var conn = await OpenTenantConnectionAsync(branch.TenantId, ct);

        await conn.ExecuteAsync(
            """
            INSERT INTO shadow_branches (
                id, tenant_id, name, description, branch_type, status,
                parent_branch_id, source_memory_id, confidence, hypothesis,
                reasoning_context, ttl_hours, auto_discard_threshold, expires_at,
                created_at, metadata
            ) VALUES (
                @Id, @TenantId, @Name, @Description, @BranchType::shadow_branch_type, @Status::shadow_branch_status,
                @ParentBranchId, @SourceMemoryId, @Confidence, @Hypothesis,
                @ReasoningContext, @TtlHours, @AutoDiscardThreshold, @ExpiresAt,
                @CreatedAt, @Metadata::jsonb
            )
            """,
            new
            {
                branch.Id,
                branch.TenantId,
                branch.Name,
                branch.Description,
                BranchType = branch.BranchType.ToString().ToLowerInvariant(),
                Status = branch.Status.ToString().ToLowerInvariant(),
                branch.ParentBranchId,
                branch.SourceMemoryId,
                branch.Confidence,
                branch.Hypothesis,
                branch.ReasoningContext,
                branch.TtlHours,
                branch.AutoDiscardThreshold,
                ExpiresAt = branch.TtlHours.HasValue
                    ? DateTimeOffset.UtcNow.AddHours(branch.TtlHours.Value)
                    : branch.ExpiresAt,
                branch.CreatedAt,
                Metadata = branch.Metadata != null ? JsonSerializer.Serialize(branch.Metadata, JsonOptions) : null
            });

        _logger.LogDebug("Created shadow branch {BranchId} ({BranchType}): {Name}",
            branch.Id, branch.BranchType, branch.Name);

        return branch.Id;
    }

    public async Task<ShadowBranch?> GetBranchAsync(Guid branchId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
            """
            SELECT id, tenant_id, name, description, branch_type, status,
                   parent_branch_id, source_memory_id, confidence, hypothesis,
                   reasoning_context, ttl_hours, auto_discard_threshold, expires_at,
                   created_at, frozen_at, promoted_at, discarded_at, metadata
            FROM shadow_branches
            WHERE id = @BranchId
            """,
            new { BranchId = branchId });

        return row == null ? null : MapToBranch(row);
    }

    public async Task<List<ShadowBranch>> GetActiveBranchesAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenTenantConnectionAsync(tenantId, ct);

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, tenant_id, name, description, branch_type, status,
                   parent_branch_id, source_memory_id, confidence, hypothesis,
                   reasoning_context, ttl_hours, auto_discard_threshold, expires_at,
                   created_at, frozen_at, promoted_at, discarded_at, metadata
            FROM shadow_branches
            WHERE tenant_id = @TenantId AND status = 'active'
            ORDER BY created_at DESC
            """,
            new { TenantId = tenantId });

        return rows.Select(MapToBranch).ToList();
    }

    public async Task<List<ShadowBranch>> GetChildBranchesAsync(Guid parentBranchId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, tenant_id, name, description, branch_type, status,
                   parent_branch_id, source_memory_id, confidence, hypothesis,
                   reasoning_context, ttl_hours, auto_discard_threshold, expires_at,
                   created_at, frozen_at, promoted_at, discarded_at, metadata
            FROM shadow_branches
            WHERE parent_branch_id = @ParentBranchId
            ORDER BY created_at DESC
            """,
            new { ParentBranchId = parentBranchId });

        return rows.Select(MapToBranch).ToList();
    }

    public async Task UpdateBranchStatusAsync(Guid branchId, ShadowBranchStatus status, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var timestampColumn = status switch
        {
            ShadowBranchStatus.Frozen => "frozen_at",
            ShadowBranchStatus.Promoted => "promoted_at",
            ShadowBranchStatus.Discarded => "discarded_at",
            _ => null
        };

        var sql = timestampColumn != null
            ? $"UPDATE shadow_branches SET status = @Status::shadow_branch_status, {timestampColumn} = NOW() WHERE id = @BranchId"
            : "UPDATE shadow_branches SET status = @Status::shadow_branch_status WHERE id = @BranchId";

        await conn.ExecuteAsync(sql, new { BranchId = branchId, Status = status.ToString().ToLowerInvariant() });
    }

    public async Task UpdateBranchConfidenceAsync(Guid branchId, float confidence, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        await conn.ExecuteAsync(
            "UPDATE shadow_branches SET confidence = @Confidence WHERE id = @BranchId",
            new { BranchId = branchId, Confidence = confidence });
    }

    public async Task FreezeBranchAsync(Guid branchId, CancellationToken ct = default)
    {
        await UpdateBranchStatusAsync(branchId, ShadowBranchStatus.Frozen, ct);
    }

    public async Task<ShadowBranchStats> GetBranchStatsAsync(Guid branchId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM get_shadow_branch_stats(@BranchId)",
            new { BranchId = branchId });

        return new ShadowBranchStats
        {
            BranchId = branchId,
            MemoryCount = (int)(row?.memory_count ?? 0),
            EntityCount = (int)(row?.entity_count ?? 0),
            RelationshipCount = (int)(row?.relationship_count ?? 0),
            AverageConfidence = row?.avg_confidence ?? 0f,
            ChildBranchCount = (int)(row?.child_branch_count ?? 0),
            OldestMemory = row?.oldest_memory,
            NewestMemory = row?.newest_memory
        };
    }

    #endregion

    #region Shadow Memory Operations

    public async Task<Guid> CreateShadowMemoryAsync(ShadowMemory memory, CancellationToken ct = default)
    {
        await using var conn = await OpenTenantConnectionAsync(memory.TenantId, ct);

        // Verify branch is active
        var branchStatus = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT status FROM shadow_branches WHERE id = @BranchId",
            new { memory.BranchId });

        if (branchStatus != "active")
        {
            throw new InvalidOperationException($"Cannot add memory to branch with status: {branchStatus}");
        }

        var embeddingString = memory.Embedding != null
            ? "[" + string.Join(",", memory.Embedding) + "]"
            : null;

        await conn.ExecuteAsync(
            """
            INSERT INTO shadow_memories (
                id, branch_id, tenant_id, content, embedding, confidence,
                shadows_memory_id, reasoning_step, supporting_memory_ids,
                contradicting_memory_ids, source, created_at, metadata
            ) VALUES (
                @Id, @BranchId, @TenantId, @Content, @Embedding::vector, @Confidence,
                @ShadowsMemoryId, @ReasoningStep, @SupportingMemoryIds,
                @ContradictingMemoryIds, @Source, @CreatedAt, @Metadata::jsonb
            )
            """,
            new
            {
                memory.Id,
                memory.BranchId,
                memory.TenantId,
                memory.Content,
                Embedding = embeddingString,
                memory.Confidence,
                memory.ShadowsMemoryId,
                memory.ReasoningStep,
                SupportingMemoryIds = memory.SupportingMemoryIds?.ToArray(),
                ContradictingMemoryIds = memory.ContradictingMemoryIds?.ToArray(),
                memory.Source,
                memory.CreatedAt,
                Metadata = memory.Metadata != null ? JsonSerializer.Serialize(memory.Metadata, JsonOptions) : null
            });

        return memory.Id;
    }

    public async Task<ShadowMemory?> GetShadowMemoryAsync(Guid memoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
            """
            SELECT id, branch_id, tenant_id, content, confidence,
                   shadows_memory_id, reasoning_step, supporting_memory_ids,
                   contradicting_memory_ids, source, created_at, metadata
            FROM shadow_memories
            WHERE id = @MemoryId
            """,
            new { MemoryId = memoryId });

        return row == null ? null : MapToShadowMemory(row);
    }

    public async Task<List<ShadowMemory>> GetBranchMemoriesAsync(Guid branchId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, branch_id, tenant_id, content, confidence,
                   shadows_memory_id, reasoning_step, supporting_memory_ids,
                   contradicting_memory_ids, source, created_at, metadata
            FROM shadow_memories
            WHERE branch_id = @BranchId
            ORDER BY created_at DESC
            """,
            new { BranchId = branchId });

        return rows.Select(MapToShadowMemory).ToList();
    }

    public async Task<List<ShadowMemory>> SearchShadowMemoriesAsync(
        Guid branchId,
        float[] queryEmbedding,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, branch_id, tenant_id, content, confidence,
                   shadows_memory_id, reasoning_step, supporting_memory_ids,
                   contradicting_memory_ids, source, created_at, metadata,
                   1 - (embedding <=> @Embedding::vector) AS similarity
            FROM shadow_memories
            WHERE branch_id = @BranchId
                AND embedding IS NOT NULL
                AND 1 - (embedding <=> @Embedding::vector) >= @Threshold
            ORDER BY embedding <=> @Embedding::vector
            LIMIT @Limit
            """,
            new { BranchId = branchId, Embedding = embeddingString, Threshold = threshold, Limit = limit });

        return rows.Select(MapToShadowMemory).ToList();
    }

    public async Task UpdateShadowMemoryConfidenceAsync(Guid memoryId, float confidence, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        await conn.ExecuteAsync(
            "UPDATE shadow_memories SET confidence = @Confidence WHERE id = @MemoryId",
            new { MemoryId = memoryId, Confidence = confidence });
    }

    #endregion

    #region Shadow Entity Operations

    public async Task<Guid> CreateShadowEntityAsync(ShadowEntity entity, CancellationToken ct = default)
    {
        await using var conn = await OpenTenantConnectionAsync(entity.TenantId, ct);

        await conn.ExecuteAsync(
            """
            INSERT INTO shadow_entities (
                id, branch_id, tenant_id, name, entity_type, canonical_name,
                shadows_entity_id, confidence, created_at
            ) VALUES (
                @Id, @BranchId, @TenantId, @Name, @EntityType, @CanonicalName,
                @ShadowsEntityId, @Confidence, @CreatedAt
            )
            """,
            new
            {
                entity.Id,
                entity.BranchId,
                entity.TenantId,
                entity.Name,
                entity.EntityType,
                CanonicalName = entity.CanonicalName ?? entity.Name.ToLowerInvariant(),
                entity.ShadowsEntityId,
                entity.Confidence,
                entity.CreatedAt
            });

        return entity.Id;
    }

    public async Task<List<ShadowEntity>> GetBranchEntitiesAsync(Guid branchId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, branch_id, tenant_id, name, entity_type, canonical_name,
                   shadows_entity_id, confidence, created_at
            FROM shadow_entities
            WHERE branch_id = @BranchId
            """,
            new { BranchId = branchId });

        return rows.Select(r => new ShadowEntity
        {
            Id = r.id,
            BranchId = r.branch_id,
            TenantId = r.tenant_id,
            Name = r.name,
            EntityType = r.entity_type,
            CanonicalName = r.canonical_name,
            ShadowsEntityId = r.shadows_entity_id,
            Confidence = r.confidence,
            CreatedAt = r.created_at
        }).ToList();
    }

    #endregion

    #region Shadow Relationship Operations

    public async Task<Guid> CreateShadowRelationshipAsync(ShadowRelationship relationship, CancellationToken ct = default)
    {
        await using var conn = await OpenTenantConnectionAsync(relationship.TenantId, ct);

        await conn.ExecuteAsync(
            """
            INSERT INTO shadow_relationships (
                id, branch_id, tenant_id, source_entity_id, target_entity_id,
                relationship_type, confidence, created_at
            ) VALUES (
                @Id, @BranchId, @TenantId, @SourceEntityId, @TargetEntityId,
                @RelationshipType, @Confidence, @CreatedAt
            )
            """,
            new
            {
                relationship.Id,
                relationship.BranchId,
                relationship.TenantId,
                relationship.SourceEntityId,
                relationship.TargetEntityId,
                relationship.RelationshipType,
                relationship.Confidence,
                relationship.CreatedAt
            });

        return relationship.Id;
    }

    public async Task<List<ShadowRelationship>> GetBranchRelationshipsAsync(Guid branchId, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, branch_id, tenant_id, source_entity_id, target_entity_id,
                   relationship_type, confidence, created_at
            FROM shadow_relationships
            WHERE branch_id = @BranchId
            """,
            new { BranchId = branchId });

        return rows.Select(r => new ShadowRelationship
        {
            Id = r.id,
            BranchId = r.branch_id,
            TenantId = r.tenant_id,
            SourceEntityId = r.source_entity_id,
            TargetEntityId = r.target_entity_id,
            RelationshipType = r.relationship_type,
            Confidence = r.confidence,
            CreatedAt = r.created_at
        }).ToList();
    }

    #endregion

    #region Promote/Discard Operations

    public async Task<PromotionResult> PromoteBranchAsync(
        Guid branchId,
        IKnowledgeGraphStore mainStore,
        CancellationToken ct = default)
    {
        var branch = await GetBranchAsync(branchId, ct);
        if (branch == null)
        {
            return new PromotionResult { BranchId = branchId, Success = false, ErrorMessage = "Branch not found" };
        }

        if (branch.Status != ShadowBranchStatus.Active && branch.Status != ShadowBranchStatus.Frozen)
        {
            return new PromotionResult { BranchId = branchId, Success = false, ErrorMessage = $"Cannot promote branch with status: {branch.Status}" };
        }

        var memories = await GetBranchMemoriesAsync(branchId, ct);
        var entities = await GetBranchEntitiesAsync(branchId, ct);
        var relationships = await GetBranchRelationshipsAsync(branchId, ct);

        var promotedMemoryIds = new List<Guid>();
        var promotedEntityIds = new List<Guid>();
        var promotedRelationshipIds = new List<Guid>();
        var entityIdMap = new Dictionary<Guid, Guid>(); // Shadow ID -> Main ID

        await using var conn = await OpenTenantConnectionAsync(branch.TenantId, ct);

        try
        {
            // Promote entities first (needed for relationship references)
            foreach (var shadowEntity in entities)
            {
                var mainEntity = new Entity
                {
                    Id = Guid.CreateVersion7(),
                    Name = shadowEntity.Name,
                    EntityType = shadowEntity.EntityType,
                    CanonicalName = shadowEntity.CanonicalName
                };

                var mainEntityId = await mainStore.CreateEntityAsync(mainEntity, ct);
                entityIdMap[shadowEntity.Id] = mainEntityId;
                promotedEntityIds.Add(mainEntityId);

                await LogPromotionAsync(conn, branchId, branch.TenantId, shadowEntity.Id, null, null, null, mainEntityId, null, ct);
            }

            // Promote memories
            foreach (var shadowMemory in memories)
            {
                var mainMemory = new Memory
                {
                    Id = Guid.CreateVersion7(),
                    Content = shadowMemory.Content,
                    Embedding = shadowMemory.Embedding,
                    Source = $"shadow:{branch.Name}",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                var mainMemoryId = await mainStore.CreateMemoryAsync(mainMemory, ct);
                promotedMemoryIds.Add(mainMemoryId);

                await LogPromotionAsync(conn, branchId, branch.TenantId, null, shadowMemory.Id, null, mainMemoryId, null, null, ct);
            }

            // Promote relationships (with remapped entity IDs)
            foreach (var shadowRel in relationships)
            {
                if (entityIdMap.TryGetValue(shadowRel.SourceEntityId, out var mainSourceId) &&
                    entityIdMap.TryGetValue(shadowRel.TargetEntityId, out var mainTargetId))
                {
                    var mainRel = new EntityRelationship
                    {
                        Id = Guid.CreateVersion7(),
                        SourceEntityId = mainSourceId,
                        TargetEntityId = mainTargetId,
                        RelationshipType = shadowRel.RelationshipType,
                        Confidence = shadowRel.Confidence
                    };

                    var mainRelId = await mainStore.CreateRelationshipAsync(mainRel, ct);
                    promotedRelationshipIds.Add(mainRelId);

                    await LogPromotionAsync(conn, branchId, branch.TenantId, null, null, shadowRel.Id, null, null, mainRelId, ct);
                }
            }

            // Mark branch as promoted
            await UpdateBranchStatusAsync(branchId, ShadowBranchStatus.Promoted, ct);

            _logger.LogInformation(
                "Promoted shadow branch {BranchId}: {Memories} memories, {Entities} entities, {Relationships} relationships",
                branchId, promotedMemoryIds.Count, promotedEntityIds.Count, promotedRelationshipIds.Count);

            return new PromotionResult
            {
                BranchId = branchId,
                Success = true,
                MemoriesPromoted = promotedMemoryIds.Count,
                EntitiesPromoted = promotedEntityIds.Count,
                RelationshipsPromoted = promotedRelationshipIds.Count,
                PromotedMemoryIds = promotedMemoryIds,
                PromotedEntityIds = promotedEntityIds,
                PromotedRelationshipIds = promotedRelationshipIds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to promote shadow branch {BranchId}", branchId);
            return new PromotionResult { BranchId = branchId, Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<PromotionResult> PromoteMemoriesAsync(
        Guid branchId,
        List<Guid> memoryIds,
        IKnowledgeGraphStore mainStore,
        CancellationToken ct = default)
    {
        var branch = await GetBranchAsync(branchId, ct);
        if (branch == null)
        {
            return new PromotionResult { BranchId = branchId, Success = false, ErrorMessage = "Branch not found" };
        }

        var promotedMemoryIds = new List<Guid>();
        await using var conn = await OpenTenantConnectionAsync(branch.TenantId, ct);

        foreach (var shadowMemoryId in memoryIds)
        {
            var shadowMemory = await GetShadowMemoryAsync(shadowMemoryId, ct);
            if (shadowMemory == null || shadowMemory.BranchId != branchId) continue;

            var mainMemory = new Memory
            {
                Id = Guid.CreateVersion7(),
                Content = shadowMemory.Content,
                Embedding = shadowMemory.Embedding,
                Source = $"shadow:{branch.Name}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var mainMemoryId = await mainStore.CreateMemoryAsync(mainMemory, ct);
            promotedMemoryIds.Add(mainMemoryId);

            await LogPromotionAsync(conn, branchId, branch.TenantId, null, shadowMemoryId, null, mainMemoryId, null, null, ct);
        }

        return new PromotionResult
        {
            BranchId = branchId,
            Success = true,
            MemoriesPromoted = promotedMemoryIds.Count,
            PromotedMemoryIds = promotedMemoryIds
        };
    }

    public async Task<DiscardResult> DiscardBranchAsync(Guid branchId, string? reason = null, CancellationToken ct = default)
    {
        var stats = await GetBranchStatsAsync(branchId, ct);

        await UpdateBranchStatusAsync(branchId, ShadowBranchStatus.Discarded, ct);

        _logger.LogInformation(
            "Discarded shadow branch {BranchId}: {Memories} memories, {Entities} entities. Reason: {Reason}",
            branchId, stats.MemoryCount, stats.EntityCount, reason ?? "none");

        return new DiscardResult
        {
            BranchId = branchId,
            Success = true,
            MemoriesDiscarded = stats.MemoryCount,
            EntitiesDiscarded = stats.EntityCount,
            RelationshipsDiscarded = stats.RelationshipCount,
            Reason = reason
        };
    }

    public async Task DiscardMemoriesAsync(Guid branchId, List<Guid> memoryIds, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM shadow_memories WHERE branch_id = @BranchId AND id = ANY(@MemoryIds)",
            new { BranchId = branchId, MemoryIds = memoryIds.ToArray() });
    }

    #endregion

    #region Maintenance Operations

    public async Task<List<ShadowBranch>> GetExpiredBranchesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, tenant_id, name, description, branch_type, status,
                   parent_branch_id, source_memory_id, confidence, hypothesis,
                   reasoning_context, ttl_hours, auto_discard_threshold, expires_at,
                   created_at, frozen_at, promoted_at, discarded_at, metadata
            FROM shadow_branches
            WHERE status = 'active' AND expires_at IS NOT NULL AND expires_at < NOW()
            """);

        return rows.Select(MapToBranch).ToList();
    }

    public async Task<List<ShadowBranch>> GetLowConfidenceBranchesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT id, tenant_id, name, description, branch_type, status,
                   parent_branch_id, source_memory_id, confidence, hypothesis,
                   reasoning_context, ttl_hours, auto_discard_threshold, expires_at,
                   created_at, frozen_at, promoted_at, discarded_at, metadata
            FROM shadow_branches
            WHERE status = 'active'
                AND auto_discard_threshold IS NOT NULL
                AND confidence < auto_discard_threshold
            """);

        return rows.Select(MapToBranch).ToList();
    }

    public async Task<int> CleanupDiscardedBranchesAsync(int olderThanDays = 7, CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);

        var result = await conn.ExecuteAsync(
            """
            DELETE FROM shadow_branches
            WHERE status = 'discarded'
                AND discarded_at < NOW() - @Days * INTERVAL '1 day'
            """,
            new { Days = olderThanDays });

        return result;
    }

    #endregion

    #region Private Helpers

    private static ShadowBranch MapToBranch(dynamic row) => new()
    {
        Id = row.id,
        TenantId = row.tenant_id,
        Name = row.name,
        Description = row.description,
        BranchType = Enum.Parse<ShadowBranchType>(row.branch_type, ignoreCase: true),
        Status = Enum.Parse<ShadowBranchStatus>(row.status, ignoreCase: true),
        ParentBranchId = row.parent_branch_id,
        SourceMemoryId = row.source_memory_id,
        Confidence = row.confidence,
        Hypothesis = row.hypothesis,
        ReasoningContext = row.reasoning_context,
        TtlHours = row.ttl_hours,
        AutoDiscardThreshold = row.auto_discard_threshold,
        ExpiresAt = row.expires_at,
        CreatedAt = row.created_at,
        FrozenAt = row.frozen_at,
        PromotedAt = row.promoted_at,
        DiscardedAt = row.discarded_at
    };

    private static ShadowMemory MapToShadowMemory(dynamic row) => new()
    {
        Id = row.id,
        BranchId = row.branch_id,
        TenantId = row.tenant_id,
        Content = row.content,
        Confidence = row.confidence,
        ShadowsMemoryId = row.shadows_memory_id,
        ReasoningStep = row.reasoning_step,
        SupportingMemoryIds = row.supporting_memory_ids != null ? ((Guid[])row.supporting_memory_ids).ToList() : null,
        ContradictingMemoryIds = row.contradicting_memory_ids != null ? ((Guid[])row.contradicting_memory_ids).ToList() : null,
        Source = row.source,
        CreatedAt = row.created_at
    };

    private static async Task LogPromotionAsync(
        NpgsqlConnection conn,
        Guid branchId,
        string tenantId,
        Guid? shadowEntityId,
        Guid? shadowMemoryId,
        Guid? shadowRelId,
        Guid? mainMemoryId,
        Guid? mainEntityId,
        Guid? mainRelId,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO shadow_promotion_log (
                id, branch_id, tenant_id, shadow_entity_id, shadow_memory_id,
                shadow_relationship_id, main_memory_id, main_entity_id, main_relationship_id
            ) VALUES (
                @Id, @BranchId, @TenantId, @ShadowEntityId, @ShadowMemoryId,
                @ShadowRelId, @MainMemoryId, @MainEntityId, @MainRelId
            )
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                BranchId = branchId,
                TenantId = tenantId,
                ShadowEntityId = shadowEntityId,
                ShadowMemoryId = shadowMemoryId,
                ShadowRelId = shadowRelId,
                MainMemoryId = mainMemoryId,
                MainEntityId = mainEntityId,
                MainRelId = mainRelId
            });
    }

    #endregion
}
