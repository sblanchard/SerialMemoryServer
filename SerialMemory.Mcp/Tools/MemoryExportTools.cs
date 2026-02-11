using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Json;
using SerialMemory.EventSourcing.Store;
using SerialMemory.Infrastructure;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// MCP tool handlers for memory export operations.
/// Supports workspace, memories, graph, and user profile exports.
/// </summary>
public sealed class MemoryExportTools
{
    private const string ExportSchemaNotAvailable =
        "Export schema not available. Some tables required for export may not exist. " +
        "Run eventsourcing_schema.sql and migrate_workspace_scoping.sql migrations.";

    private readonly IEventStore _eventStore;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public MemoryExportTools(
        IEventStore eventStore,
        NpgsqlDataSource dataSource,
        ILogger logger)
    {
        _eventStore = eventStore;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger;
        _jsonOptions = SerialMemoryJsonOptions.Indented;
    }

    /// <summary>
    /// export_workspace - Export entire workspace (memories, entities, relationships, events).
    /// </summary>
    public async Task<object> HandleExportWorkspace(JsonNode? arguments)
    {
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(outputPath))
            outputPath = $"workspace_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";

        var includeEvents = arguments?["include_events"]?.GetValue<bool>() ?? false;
        var activeOnly = arguments?["active_only"]?.GetValue<bool>() ?? true;
        var encrypt = arguments?["encrypt"]?.GetValue<bool>() ?? false;
        var encryptionKey = arguments?["encryption_key"]?.GetValue<string>()?.Trim();
        var compress = arguments?["compress"]?.GetValue<bool>() ?? false;

        if (encrypt && string.IsNullOrEmpty(encryptionKey))
            throw new ArgumentException("encryption_key required when encrypt is true");

        try
        {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.SetInternalAdminWithTenantAsync(Guid.Parse("00000000-0000-0000-0000-000000000000"));

        var export = new WorkspaceExport
        {
            ExportId = Guid.CreateVersion7(),
            ExportedAt = DateTimeOffset.UtcNow,
            Version = "2.0"
        };

        // Export memories
        var memorySql = activeOnly
            ? @"SELECT memory_id, content, layer, confidence_score, half_life_days,
                       last_reinforced_at, created_at, content_hash, causal_parents, tags,
                       source, user_id, is_active, is_archived, is_expired, is_split
                FROM memory_projections WHERE is_active = TRUE ORDER BY created_at DESC"
            : @"SELECT memory_id, content, layer, confidence_score, half_life_days,
                       last_reinforced_at, created_at, content_hash, causal_parents, tags,
                       source, user_id, is_active, is_archived, is_expired, is_split
                FROM memory_projections ORDER BY created_at DESC";

        var memories = await conn.QueryAsync<dynamic>(memorySql);
        export.Memories = memories.Select(m => new ExportedMemory
        {
            MemoryId = m.memory_id,
            Content = m.content,
            Layer = m.layer,
            ConfidenceScore = (float)(decimal)m.confidence_score,
            HalfLifeDays = m.half_life_days,
            LastReinforcedAt = m.last_reinforced_at,
            CreatedAt = m.created_at,
            ContentHash = m.content_hash,
            CausalParents = m.causal_parents ?? Array.Empty<Guid>(),
            Tags = m.tags ?? Array.Empty<string>(),
            Source = m.source,
            UserId = m.user_id,
            IsActive = m.is_active,
            IsArchived = m.is_archived,
            IsExpired = m.is_expired ?? false,
            IsSplit = m.is_split ?? false
        }).ToList();

        // Export entities
        var entities = await conn.QueryAsync<dynamic>(@"
            SELECT entity_id, name, entity_type, canonical_name, confidence_score,
                   memory_count, first_seen_at, last_seen_at, is_active
            FROM entity_projections
            ORDER BY memory_count DESC");

        export.Entities = entities.Select(e => new ExportedEntity
        {
            EntityId = e.entity_id,
            Name = e.name,
            EntityType = e.entity_type,
            CanonicalName = e.canonical_name,
            ConfidenceScore = (float)(decimal)(e.confidence_score ?? 1.0m),
            MemoryCount = e.memory_count ?? 0,
            FirstSeenAt = e.first_seen_at,
            LastSeenAt = e.last_seen_at,
            IsActive = e.is_active ?? true
        }).ToList();

        // Export relationships
        var relationships = await conn.QueryAsync<dynamic>(@"
            SELECT r.relationship_id, r.source_entity_id, r.target_entity_id,
                   r.relationship_type, r.confidence, r.created_at,
                   s.name AS source_name, t.name AS target_name
            FROM entity_relationship_projections r
            JOIN entity_projections s ON r.source_entity_id = s.entity_id
            JOIN entity_projections t ON r.target_entity_id = t.entity_id
            ORDER BY r.confidence DESC");

        export.Relationships = relationships.Select(r => new ExportedRelationship
        {
            RelationshipId = r.relationship_id,
            SourceEntityId = r.source_entity_id,
            TargetEntityId = r.target_entity_id,
            SourceName = r.source_name,
            TargetName = r.target_name,
            RelationshipType = r.relationship_type,
            Confidence = (float)(decimal)(r.confidence ?? 1.0m),
            CreatedAt = r.created_at
        }).ToList();

        // Export user personas
        var personas = await conn.QueryAsync<dynamic>(@"
            SELECT id, user_id, attribute_type, attribute_key, attribute_value,
                   confidence, created_at, updated_at
            FROM user_personas
            ORDER BY user_id, attribute_type");

        export.UserPersonas = personas.Select(p => new ExportedUserPersona
        {
            Id = p.id,
            UserId = p.user_id,
            AttributeType = p.attribute_type,
            AttributeKey = p.attribute_key,
            AttributeValue = p.attribute_value,
            Confidence = (float)(decimal)(p.confidence ?? 1.0m),
            CreatedAt = p.created_at,
            UpdatedAt = p.updated_at
        }).ToList();

        // Export events if requested
        if (includeEvents)
        {
            var events = await conn.QueryAsync<dynamic>(@"
                SELECT event_id, stream_id, event_type, event_version, global_sequence,
                       event_data, metadata, created_at, created_by, content_hash
                FROM memory_events
                ORDER BY global_sequence");

            export.Events = events.Select(e => new ExportedEvent
            {
                EventId = e.event_id,
                StreamId = e.stream_id,
                EventType = e.event_type,
                EventVersion = e.event_version,
                GlobalSequence = e.global_sequence,
                EventData = e.event_data,
                Metadata = e.metadata,
                CreatedAt = e.created_at,
                CreatedBy = e.created_by,
                ContentHash = e.content_hash
            }).ToList();
        }

        // Serialize
        var json = JsonSerializer.Serialize(export, _jsonOptions);
        byte[] data = Encoding.UTF8.GetBytes(json);

        // Encrypt if requested
        if (encrypt && !string.IsNullOrEmpty(encryptionKey))
        {
            data = await EncryptAsync(data, encryptionKey);
            export.Encrypted = true;
        }

        // Compress if requested
        if (compress)
        {
            data = await CompressAsync(data);
            export.Compressed = true;
            if (!outputPath.EndsWith(".gz"))
                outputPath += ".gz";
        }

        // Write to file
        await File.WriteAllBytesAsync(outputPath, data);

        _logger.LogInformation("Workspace exported: {Memories} memories, {Entities} entities, {Relationships} relationships",
            export.Memories.Count, export.Entities.Count, export.Relationships.Count);

        return CreateTextResponse(
            $"Workspace exported successfully!\n\n" +
            $"## Export Summary\n" +
            $"- **Export ID**: {export.ExportId}\n" +
            $"- **Output Path**: {outputPath}\n" +
            $"- **File Size**: {data.Length:N0} bytes\n" +
            $"- **Encrypted**: {encrypt}\n" +
            $"- **Compressed**: {compress}\n\n" +
            $"## Contents\n" +
            $"- **Memories**: {export.Memories.Count}\n" +
            $"- **Entities**: {export.Entities.Count}\n" +
            $"- **Relationships**: {export.Relationships.Count}\n" +
            $"- **User Personas**: {export.UserPersonas.Count}\n" +
            $"- **Events**: {export.Events?.Count ?? 0}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("export_workspace skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(ExportSchemaNotAvailable);
        }
    }

    /// <summary>
    /// export_memories - Export only memories with filters.
    /// </summary>
    public async Task<object> HandleExportMemories(JsonNode? arguments)
    {
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(outputPath))
            outputPath = $"memories_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";

        var layerFilter = arguments?["layer"]?.GetValue<string>()?.Trim();
        var minConfidence = arguments?["min_confidence"]?.GetValue<float>() ?? 0f;
        var fromDate = arguments?["from_date"]?.GetValue<string>()?.Trim();
        var toDate = arguments?["to_date"]?.GetValue<string>()?.Trim();
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 10000, 1, 100000);
        var format = arguments?["format"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "json";

        try
        {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.SetInternalAdminWithTenantAsync(Guid.Parse("00000000-0000-0000-0000-000000000000"));

        var sql = @"
            SELECT memory_id, content, layer, confidence_score, half_life_days,
                   last_reinforced_at, created_at, content_hash, causal_parents, tags,
                   source, user_id
            FROM memory_projections
            WHERE is_active = TRUE";

        var parameters = new DynamicParameters();
        parameters.Add("Limit", limit);

        if (!string.IsNullOrEmpty(layerFilter))
        {
            sql += " AND layer = @Layer::memory_layer";
            parameters.Add("Layer", layerFilter);
        }

        if (minConfidence > 0)
        {
            sql += " AND confidence_score >= @MinConfidence";
            parameters.Add("MinConfidence", minConfidence);
        }

        if (DateTimeOffset.TryParse(fromDate, out var from))
        {
            sql += " AND created_at >= @FromDate";
            parameters.Add("FromDate", from);
        }

        if (DateTimeOffset.TryParse(toDate, out var to))
        {
            sql += " AND created_at <= @ToDate";
            parameters.Add("ToDate", to);
        }

        sql += " ORDER BY created_at DESC LIMIT @Limit";

        var memories = await conn.QueryAsync<dynamic>(sql, parameters);
        var memoryList = memories.ToList();

        string output;
        if (format == "csv")
        {
            var csv = new StringBuilder();
            csv.AppendLine("memory_id,content,layer,confidence_score,created_at,source,user_id");
            foreach (var m in memoryList)
            {
                var content = ((string)m.content).Replace("\"", "\"\"").Replace("\n", " ");
                csv.AppendLine($"\"{m.memory_id}\",\"{content}\",\"{m.layer}\",{m.confidence_score},{m.created_at:O},\"{m.source}\",\"{m.user_id}\"");
            }
            output = csv.ToString();
            if (!outputPath.EndsWith(".csv"))
                outputPath = outputPath.Replace(".json", ".csv");
        }
        else
        {
            var export = new
            {
                exportId = Guid.CreateVersion7(),
                exportedAt = DateTimeOffset.UtcNow,
                memories = memoryList.Select(m => new
                {
                    memoryId = m.memory_id,
                    content = m.content,
                    layer = m.layer,
                    confidenceScore = (float)(decimal)m.confidence_score,
                    createdAt = m.created_at,
                    source = m.source,
                    userId = m.user_id,
                    tags = m.tags ?? Array.Empty<string>()
                })
            };
            output = JsonSerializer.Serialize(export, _jsonOptions);
        }

        await File.WriteAllTextAsync(outputPath, output);

        _logger.LogInformation("Exported {Count} memories to {Path}", memoryList.Count, outputPath);

        return CreateTextResponse(
            $"Memories exported successfully!\n\n" +
            $"- **Output Path**: {outputPath}\n" +
            $"- **Format**: {format.ToUpperInvariant()}\n" +
            $"- **Memories Exported**: {memoryList.Count}\n" +
            $"- **Filters Applied**:\n" +
            (layerFilter != null ? $"  - Layer: {layerFilter}\n" : "") +
            (minConfidence > 0 ? $"  - Min Confidence: {minConfidence:F2}\n" : "") +
            (fromDate != null ? $"  - From Date: {fromDate}\n" : "") +
            (toDate != null ? $"  - To Date: {toDate}\n" : ""));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("export_memories skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(ExportSchemaNotAvailable);
        }
    }

    /// <summary>
    /// export_graph - Export knowledge graph (entities and relationships).
    /// </summary>
    public async Task<object> HandleExportGraph(JsonNode? arguments)
    {
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(outputPath))
            outputPath = $"graph_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";

        var format = arguments?["format"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "json";
        var includeIsolated = arguments?["include_isolated"]?.GetValue<bool>() ?? false;

        try
        {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.SetInternalAdminWithTenantAsync(Guid.Parse("00000000-0000-0000-0000-000000000000"));

        // Get entities
        var entitySql = includeIsolated
            ? @"SELECT entity_id, name, entity_type, canonical_name, memory_count FROM entity_projections WHERE is_active = TRUE"
            : @"SELECT e.entity_id, e.name, e.entity_type, e.canonical_name, e.memory_count
                FROM entity_projections e
                WHERE e.is_active = TRUE
                AND (
                    EXISTS (SELECT 1 FROM entity_relationship_projections WHERE source_entity_id = e.entity_id)
                    OR EXISTS (SELECT 1 FROM entity_relationship_projections WHERE target_entity_id = e.entity_id)
                )";

        var entities = (await conn.QueryAsync<dynamic>(entitySql)).ToList();

        // Get relationships
        var relationships = (await conn.QueryAsync<dynamic>(@"
            SELECT r.relationship_id, r.source_entity_id, r.target_entity_id,
                   r.relationship_type, r.confidence,
                   s.name AS source_name, t.name AS target_name
            FROM entity_relationship_projections r
            JOIN entity_projections s ON r.source_entity_id = s.entity_id
            JOIN entity_projections t ON r.target_entity_id = t.entity_id")).ToList();

        string output;

        if (format == "graphml")
        {
            // GraphML format for visualization tools
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">");
            sb.AppendLine("  <key id=\"name\" for=\"node\" attr.name=\"name\" attr.type=\"string\"/>");
            sb.AppendLine("  <key id=\"type\" for=\"node\" attr.name=\"type\" attr.type=\"string\"/>");
            sb.AppendLine("  <key id=\"reltype\" for=\"edge\" attr.name=\"reltype\" attr.type=\"string\"/>");
            sb.AppendLine("  <graph id=\"G\" edgedefault=\"directed\">");

            foreach (var e in entities)
            {
                sb.AppendLine($"    <node id=\"{e.entity_id}\">");
                sb.AppendLine($"      <data key=\"name\">{EscapeXml((string)e.name)}</data>");
                sb.AppendLine($"      <data key=\"type\">{e.entity_type}</data>");
                sb.AppendLine("    </node>");
            }

            foreach (var r in relationships)
            {
                sb.AppendLine($"    <edge source=\"{r.source_entity_id}\" target=\"{r.target_entity_id}\">");
                sb.AppendLine($"      <data key=\"reltype\">{r.relationship_type}</data>");
                sb.AppendLine("    </edge>");
            }

            sb.AppendLine("  </graph>");
            sb.AppendLine("</graphml>");
            output = sb.ToString();

            if (!outputPath.EndsWith(".graphml"))
                outputPath = outputPath.Replace(".json", ".graphml");
        }
        else if (format == "cytoscape")
        {
            // Cytoscape.js format
            var cytoscape = new
            {
                elements = new
                {
                    nodes = entities.Select(e => new
                    {
                        data = new
                        {
                            id = e.entity_id.ToString(),
                            label = e.name,
                            type = e.entity_type,
                            memoryCount = e.memory_count ?? 0
                        }
                    }),
                    edges = relationships.Select(r => new
                    {
                        data = new
                        {
                            id = r.relationship_id.ToString(),
                            source = r.source_entity_id.ToString(),
                            target = r.target_entity_id.ToString(),
                            label = r.relationship_type,
                            confidence = (float)(decimal)(r.confidence ?? 1.0m)
                        }
                    })
                }
            };
            output = JsonSerializer.Serialize(cytoscape, _jsonOptions);
        }
        else
        {
            // Standard JSON format
            var export = new
            {
                exportId = Guid.CreateVersion7(),
                exportedAt = DateTimeOffset.UtcNow,
                nodes = entities.Select(e => new
                {
                    id = e.entity_id,
                    name = e.name,
                    type = e.entity_type,
                    memoryCount = e.memory_count ?? 0
                }),
                edges = relationships.Select(r => new
                {
                    id = r.relationship_id,
                    source = r.source_entity_id,
                    target = r.target_entity_id,
                    sourceName = r.source_name,
                    targetName = r.target_name,
                    type = r.relationship_type,
                    confidence = (float)(decimal)(r.confidence ?? 1.0m)
                })
            };
            output = JsonSerializer.Serialize(export, _jsonOptions);
        }

        await File.WriteAllTextAsync(outputPath, output);

        _logger.LogInformation("Exported graph: {Nodes} nodes, {Edges} edges to {Path}",
            entities.Count, relationships.Count, outputPath);

        return CreateTextResponse(
            $"Graph exported successfully!\n\n" +
            $"- **Output Path**: {outputPath}\n" +
            $"- **Format**: {format.ToUpperInvariant()}\n" +
            $"- **Nodes (Entities)**: {entities.Count}\n" +
            $"- **Edges (Relationships)**: {relationships.Count}\n" +
            $"- **Include Isolated**: {includeIsolated}");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("export_graph skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(ExportSchemaNotAvailable);
        }
    }

    /// <summary>
    /// export_user_profile - Export user persona and preferences.
    /// </summary>
    public async Task<object> HandleExportUserProfile(JsonNode? arguments)
    {
        var userId = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(outputPath))
            outputPath = $"user_profile_{userId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";

        var includeInteractions = arguments?["include_interactions"]?.GetValue<bool>() ?? false;

        try
        {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.SetInternalAdminWithTenantAsync(Guid.Parse("00000000-0000-0000-0000-000000000000"));

        // Get user persona attributes
        var personas = await conn.QueryAsync<dynamic>(@"
            SELECT attribute_type, attribute_key, attribute_value, confidence, created_at, updated_at
            FROM user_personas
            WHERE user_id = @UserId
            ORDER BY attribute_type, confidence DESC",
            new { UserId = userId });

        var personaList = personas.ToList();

        // Group by type
        var grouped = personaList
            .GroupBy(p => (string)p.attribute_type)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => new
                {
                    key = p.attribute_key,
                    value = p.attribute_value,
                    confidence = (float)(decimal)(p.confidence ?? 1.0m),
                    updatedAt = p.updated_at
                }).ToList());

        // Get memory statistics for user
        var memoryStats = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT
                COUNT(*) AS total_memories,
                COUNT(*) FILTER (WHERE is_active = TRUE) AS active_memories,
                AVG(confidence_score) AS avg_confidence,
                SUM(recall_count) AS total_recalls,
                MIN(created_at) AS first_memory,
                MAX(created_at) AS last_memory
            FROM memory_projections
            WHERE user_id = @UserId",
            new { UserId = userId });

        var export = new
        {
            exportId = Guid.CreateVersion7(),
            exportedAt = DateTimeOffset.UtcNow,
            userId,
            persona = grouped,
            memoryStatistics = new
            {
                totalMemories = memoryStats?.total_memories ?? 0,
                activeMemories = memoryStats?.active_memories ?? 0,
                averageConfidence = memoryStats?.avg_confidence != null ? (float)(decimal)memoryStats.avg_confidence : 0f,
                totalRecalls = memoryStats?.total_recalls ?? 0,
                firstMemory = memoryStats?.first_memory,
                lastMemory = memoryStats?.last_memory
            },
            interactions = includeInteractions ? await GetUserInteractions(conn, userId) : null
        };

        var json = JsonSerializer.Serialize(export, _jsonOptions);
        await File.WriteAllTextAsync(outputPath, json);

        _logger.LogInformation("Exported user profile for {UserId}: {Attributes} attributes",
            userId, personaList.Count);

        return CreateTextResponse(
            $"User profile exported successfully!\n\n" +
            $"- **User ID**: {userId}\n" +
            $"- **Output Path**: {outputPath}\n" +
            $"- **Persona Attributes**: {personaList.Count}\n" +
            $"- **Memory Statistics**:\n" +
            $"  - Total Memories: {memoryStats?.total_memories ?? 0}\n" +
            $"  - Active Memories: {memoryStats?.active_memories ?? 0}\n" +
            $"  - Average Confidence: {(memoryStats?.avg_confidence != null ? ((decimal)memoryStats.avg_confidence).ToString("F3") : "N/A")}\n" +
            $"  - Total Recalls: {memoryStats?.total_recalls ?? 0}\n" +
            (includeInteractions ? "- **Interactions**: Included\n" : ""));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("export_user_profile skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(ExportSchemaNotAvailable);
        }
    }

    /// <summary>
    /// export_markdown - Export as Obsidian-compatible Markdown vault.
    /// </summary>
    public async Task<object> HandleExportMarkdown(JsonNode? arguments)
    {
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(outputPath))
            outputPath = "serial_memory_vault";

        var activeOnly = arguments?["active_only"]?.GetValue<bool>() ?? true;
        var includeEntities = arguments?["include_entities"]?.GetValue<bool>() ?? true;
        var includeSessions = arguments?["include_sessions"]?.GetValue<bool>() ?? true;
        var minConfidence = arguments?["min_confidence"]?.GetValue<float>() ?? 0f;
        var groupBy = arguments?["group_by"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "month";

        try
        {
        await using var conn = await _dataSource.OpenConnectionAsync();

        // Set tenant context for RLS (use self-hosted default tenant)
        await conn.SetInternalAdminWithTenantAsync(Guid.Parse("00000000-0000-0000-0000-000000000000"));

        // Query memories
        var memorySql = @"
            SELECT memory_id, content, layer, confidence_score, half_life_days,
                   created_at, content_hash, causal_parents, tags, source, user_id, is_active
            FROM memory_projections
            WHERE 1=1";

        var parameters = new DynamicParameters();

        if (activeOnly)
            memorySql += " AND is_active = TRUE";

        if (minConfidence > 0)
        {
            memorySql += " AND confidence_score >= @MinConfidence";
            parameters.Add("MinConfidence", minConfidence);
        }

        memorySql += " ORDER BY created_at DESC LIMIT 50000";

        var memories = (await conn.QueryAsync<dynamic>(memorySql, parameters)).ToList();

        // Query memory-entity links
        var memoryEntityLinks = new Dictionary<Guid, List<(string Name, string EntityType, Guid EntityId)>>();
        if (includeEntities)
        {
            var links = await conn.QueryAsync<dynamic>(@"
                SELECT mel.memory_id, e.entity_id, e.name, e.entity_type
                FROM memory_entity_links mel
                JOIN entity_projections e ON mel.entity_id = e.entity_id");

            foreach (var link in links)
            {
                Guid memId = link.memory_id;
                if (!memoryEntityLinks.TryGetValue(memId, out var list))
                {
                    list = [];
                    memoryEntityLinks[memId] = list;
                }
                list.Add((link.name, link.entity_type, link.entity_id));
            }
        }

        // Query entities
        var entities = new List<dynamic>();
        var entityMemoryCounts = new Dictionary<Guid, int>();
        if (includeEntities)
        {
            entities = (await conn.QueryAsync<dynamic>(@"
                SELECT entity_id, name, entity_type, canonical_name, confidence_score,
                       memory_count, first_seen_at, last_seen_at
                FROM entity_projections
                WHERE is_active = TRUE
                ORDER BY memory_count DESC")).ToList();

            foreach (var e in entities)
                entityMemoryCounts[(Guid)e.entity_id] = (int)(e.memory_count ?? 0);
        }

        // Query relationships
        var relationships = new List<dynamic>();
        if (includeEntities)
        {
            relationships = (await conn.QueryAsync<dynamic>(@"
                SELECT r.source_entity_id, r.target_entity_id, r.relationship_type, r.confidence,
                       s.name AS source_name, s.entity_type AS source_type,
                       t.name AS target_name, t.entity_type AS target_type
                FROM entity_relationship_projections r
                JOIN entity_projections s ON r.source_entity_id = s.entity_id
                JOIN entity_projections t ON r.target_entity_id = t.entity_id")).ToList();
        }

        // Build relationship lookup by entity
        var entityRelationships = new Dictionary<Guid, List<dynamic>>();
        foreach (var r in relationships)
        {
            Guid srcId = r.source_entity_id;
            Guid tgtId = r.target_entity_id;
            if (!entityRelationships.TryGetValue(srcId, out var srcList))
            {
                srcList = [];
                entityRelationships[srcId] = srcList;
            }
            srcList.Add(r);
            if (!entityRelationships.TryGetValue(tgtId, out var tgtList))
            {
                tgtList = [];
                entityRelationships[tgtId] = tgtList;
            }
            tgtList.Add(r);
        }

        // Build entity memories lookup (entity -> list of memories mentioning it)
        var entityMemories = new Dictionary<Guid, List<(Guid MemoryId, DateTimeOffset CreatedAt, string ContentPreview)>>();
        foreach (var kvp in memoryEntityLinks)
        {
            var memId = kvp.Key;
            var mem = memories.FirstOrDefault(m => (Guid)m.memory_id == memId);
            if (mem == null) continue;

            foreach (var (_, _, entityId) in kvp.Value)
            {
                if (!entityMemories.TryGetValue(entityId, out var list))
                {
                    list = [];
                    entityMemories[entityId] = list;
                }
                var preview = ((string)mem.content).Length > 60
                    ? ((string)mem.content)[..60] + "..."
                    : (string)mem.content;
                list.Add((memId, (DateTimeOffset)mem.created_at, preview));
            }
        }

        // Query sessions
        var sessions = new List<dynamic>();
        if (includeSessions)
        {
            sessions = (await conn.QueryAsync<dynamic>(@"
                SELECT id, session_name, started_at, ended_at, client_type
                FROM conversation_sessions
                ORDER BY started_at DESC
                LIMIT 100")).ToList();
        }

        // Create directory structure
        Directory.CreateDirectory(outputPath);
        Directory.CreateDirectory(Path.Combine(outputPath, "memories"));

        if (includeEntities)
            Directory.CreateDirectory(Path.Combine(outputPath, "entities"));

        if (includeSessions)
            Directory.CreateDirectory(Path.Combine(outputPath, "sessions"));

        var memoryCount = 0;
        var entityCount = 0;
        var sessionCount = 0;

        // Write memory files
        foreach (var m in memories)
        {
            Guid memoryId = m.memory_id;
            DateTimeOffset createdAt = m.created_at;
            var shortId = memoryId.ToString()[..12];

            var subFolder = groupBy switch
            {
                "layer" => (string)(m.layer ?? "unknown"),
                "source" => SanitizeFileName((string)(m.source ?? "unknown")),
                _ => createdAt.ToString("yyyy-MM")
            };

            var memDir = Path.Combine(outputPath, "memories", subFolder);
            Directory.CreateDirectory(memDir);

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"id: {memoryId}");
            sb.AppendLine("type: memory");
            sb.AppendLine($"layer: {m.layer ?? "unknown"}");
            sb.AppendLine($"confidence: {SafeFloat(m.confidence_score):F3}");
            sb.AppendLine($"created: {createdAt:O}");
            if (m.source != null) sb.AppendLine($"source: {m.source}");

            string[]? tags = m.tags;
            if (tags is { Length: > 0 })
                sb.AppendLine($"tags: [{string.Join(", ", tags)}]");

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine((string)m.content);

            // Entity links
            if (memoryEntityLinks.TryGetValue(memoryId, out var linkedEntities) && linkedEntities.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Entities");
                foreach (var (name, entityType, _) in linkedEntities)
                {
                    var slug = SanitizeFileName(name);
                    sb.AppendLine($"- [[entities/{entityType}/{slug}|{name}]] ({entityType})");
                }
            }

            // Causal parents
            Guid[]? causalParents = m.causal_parents;
            if (causalParents is { Length: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("## Causal Parents");
                foreach (var parentId in causalParents)
                {
                    var parentMem = memories.FirstOrDefault(p => (Guid)p.memory_id == parentId);
                    if (parentMem != null)
                    {
                        var parentCreated = (DateTimeOffset)parentMem.created_at;
                        var parentSubFolder = groupBy switch
                        {
                            "layer" => (string)(parentMem.layer ?? "unknown"),
                            "source" => SanitizeFileName((string)(parentMem.source ?? "unknown")),
                            _ => parentCreated.ToString("yyyy-MM")
                        };
                        sb.AppendLine($"- [[memories/{parentSubFolder}/{parentId.ToString()[..12]}]]");
                    }
                    else
                    {
                        sb.AppendLine($"- {parentId}");
                    }
                }
            }

            await File.WriteAllTextAsync(Path.Combine(memDir, $"{shortId}.md"), sb.ToString());
            memoryCount++;
        }

        // Write entity files
        if (includeEntities)
        {
            foreach (var e in entities)
            {
                Guid entityId = e.entity_id;
                string entityType = e.entity_type;
                string name = e.name;
                var slug = SanitizeFileName(name);

                var typeDir = Path.Combine(outputPath, "entities", entityType);
                Directory.CreateDirectory(typeDir);

                var sb = new StringBuilder();
                sb.AppendLine("---");
                sb.AppendLine($"id: {entityId}");
                sb.AppendLine("type: entity");
                sb.AppendLine($"entity_type: {entityType}");
                sb.AppendLine($"memory_count: {e.memory_count ?? 0}");
                if (e.first_seen_at != null) sb.AppendLine($"first_seen: {((DateTimeOffset)e.first_seen_at):yyyy-MM-dd}");
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine($"# {name}");

                // Relationships
                if (entityRelationships.TryGetValue(entityId, out var rels) && rels.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("## Relationships");
                    foreach (var r in rels)
                    {
                        Guid srcId = r.source_entity_id;
                        string relType = r.relationship_type;
                        float conf = SafeFloat(r.confidence, 1.0f);

                        if (srcId == entityId)
                        {
                            var tgtSlug = SanitizeFileName((string)r.target_name);
                            sb.AppendLine($"- [[entities/{r.target_type}/{tgtSlug}|{r.target_name}]] -- {relType} (confidence: {conf:F1})");
                        }
                        else
                        {
                            var srcSlug = SanitizeFileName((string)r.source_name);
                            sb.AppendLine($"- [[entities/{r.source_type}/{srcSlug}|{r.source_name}]] -- {relType} (confidence: {conf:F1})");
                        }
                    }
                }

                // Memories mentioning this entity
                if (entityMemories.TryGetValue(entityId, out var mems) && mems.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("## Memories");
                    foreach (var (memId, createdAt, preview) in mems.OrderByDescending(x => x.CreatedAt).Take(20))
                    {
                        var memShort = memId.ToString()[..12];
                        var subFolder = groupBy switch
                        {
                            "layer" => memories.FirstOrDefault(mm => (Guid)mm.memory_id == memId)?.layer ?? "unknown",
                            "source" => SanitizeFileName((string)(memories.FirstOrDefault(mm => (Guid)mm.memory_id == memId)?.source ?? "unknown")),
                            _ => createdAt.ToString("yyyy-MM")
                        };
                        sb.AppendLine($"- [[memories/{subFolder}/{memShort}|{createdAt:yyyy-MM-dd} - {preview}]]");
                    }
                }

                await File.WriteAllTextAsync(Path.Combine(typeDir, $"{slug}.md"), sb.ToString());
                entityCount++;
            }
        }

        // Write session files
        if (includeSessions)
        {
            foreach (var s in sessions)
            {
                Guid sessionId = s.id;
                DateTimeOffset startedAt = s.started_at;
                string sessionName = s.session_name ?? "Untitled";
                var fileName = $"{startedAt:yyyy-MM-dd}_{SanitizeFileName(sessionName)}.md";

                var sb = new StringBuilder();
                sb.AppendLine("---");
                sb.AppendLine($"id: {sessionId}");
                sb.AppendLine("type: session");
                sb.AppendLine($"started: {startedAt:O}");
                if (s.ended_at != null) sb.AppendLine($"ended: {((DateTimeOffset)s.ended_at):O}");
                if (s.client_type != null) sb.AppendLine($"client: {s.client_type}");
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine($"# {sessionName}");
                sb.AppendLine();
                sb.AppendLine($"**Started:** {startedAt:yyyy-MM-dd HH:mm}");
                if (s.ended_at != null)
                    sb.AppendLine($"**Ended:** {((DateTimeOffset)s.ended_at):yyyy-MM-dd HH:mm}");
                if (s.client_type != null)
                    sb.AppendLine($"**Client:** {s.client_type}");

                await File.WriteAllTextAsync(
                    Path.Combine(outputPath, "sessions", fileName),
                    sb.ToString());
                sessionCount++;
            }
        }

        // Write index.md dashboard
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("type: index");
            sb.AppendLine($"exported: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# SerialMemory Vault");
            sb.AppendLine();
            sb.AppendLine("## Statistics");
            sb.AppendLine($"- **Memories:** {memoryCount}");
            sb.AppendLine($"- **Entities:** {entityCount}");
            sb.AppendLine($"- **Relationships:** {relationships.Count}");
            sb.AppendLine($"- **Sessions:** {sessionCount}");
            sb.AppendLine();

            // Entity type breakdown
            if (entities.Count > 0)
            {
                sb.AppendLine("## Entity Types");
                var typeGroups = entities.GroupBy(e => (string)e.entity_type)
                    .OrderByDescending(g => g.Count());
                foreach (var g in typeGroups)
                {
                    sb.AppendLine($"- **{g.Key}:** {g.Count()}");
                }
                sb.AppendLine();
            }

            // Recent memories
            sb.AppendLine("## Recent Memories");
            foreach (var m in memories.Take(10))
            {
                var shortId = ((Guid)m.memory_id).ToString()[..12];
                var createdAt = (DateTimeOffset)m.created_at;
                var subFolder = groupBy switch
                {
                    "layer" => (string)(m.layer ?? "unknown"),
                    "source" => SanitizeFileName((string)(m.source ?? "unknown")),
                    _ => createdAt.ToString("yyyy-MM")
                };
                var preview = ((string)m.content).Length > 80
                    ? ((string)m.content)[..80] + "..."
                    : (string)m.content;
                sb.AppendLine($"- [[memories/{subFolder}/{shortId}|{createdAt:yyyy-MM-dd} - {preview}]]");
            }

            await File.WriteAllTextAsync(Path.Combine(outputPath, "index.md"), sb.ToString());
        }

        _logger.LogInformation("Markdown vault exported: {Memories} memories, {Entities} entities, {Sessions} sessions to {Path}",
            memoryCount, entityCount, sessionCount, outputPath);

        return CreateTextResponse(
            $"Markdown vault exported successfully!\n\n" +
            $"- **Output Path**: {outputPath}\n" +
            $"- **Memories**: {memoryCount}\n" +
            $"- **Entities**: {entityCount}\n" +
            $"- **Sessions**: {sessionCount}\n" +
            $"- **Relationships**: {relationships.Count}\n" +
            $"- **Group By**: {groupBy}\n\n" +
            $"Open `{outputPath}` in Obsidian to browse with graph view and wikilinks.");
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning("export_markdown skipped: {Message}", ex.MessageText);
            return CreateErrorResponse(ExportSchemaNotAvailable);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = string.Join("_", name.Split(Path.GetInvalidFileNameChars()))
            .Replace(" ", "-")
            .ToLowerInvariant()
            .Trim('-', '_');

        // Guard against empty result or excessively long names
        if (string.IsNullOrEmpty(sanitized))
            sanitized = "unnamed";
        if (sanitized.Length > 200)
            sanitized = sanitized[..200];

        return sanitized;
    }

    /// <summary>
    /// Safely converts a dynamic value (float, decimal, double, or null) to float.
    /// Npgsql may return different numeric types depending on column definition.
    /// </summary>
    private static float SafeFloat(dynamic? value, float defaultValue = 0f)
    {
        if (value == null) return defaultValue;
        return value switch
        {
            float f => f,
            double d => (float)d,
            decimal m => (float)m,
            int i => i,
            long l => l,
            _ => Convert.ToSingle(value)
        };
    }

    private async Task<object?> GetUserInteractions(NpgsqlConnection conn, string userId)
    {
        var interactions = await conn.QueryAsync<dynamic>(@"
            SELECT interaction_type, COUNT(*) AS count, MAX(created_at) AS last_interaction
            FROM user_memory_interactions
            WHERE user_id = @UserId
            GROUP BY interaction_type",
            new { UserId = userId });

        return interactions.Select(i => new
        {
            type = i.interaction_type,
            count = i.count,
            lastInteraction = i.last_interaction
        }).ToList();
    }

    private static async Task<byte[]> EncryptAsync(byte[] data, string key)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        aes.GenerateIV();

        using var ms = new MemoryStream();
        await ms.WriteAsync(aes.IV);

        await using (var cryptoStream = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
        {
            await cryptoStream.WriteAsync(data);
        }

        return ms.ToArray();
    }

    private static async Task<byte[]> CompressAsync(byte[] data)
    {
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            await gzip.WriteAsync(data);
        }
        return output.ToArray();
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static object CreateTextResponse(string text) =>
        new
        {
            content = new[]
            {
                new { type = "text", text }
            }
        };

    private static object CreateErrorResponse(string message) =>
        new
        {
            content = new[]
            {
                new { type = "text", text = $"Error: {message}" }
            },
            isError = true
        };

    #region Export Models

    private sealed class WorkspaceExport
    {
        public Guid ExportId { get; set; }
        public DateTimeOffset ExportedAt { get; set; }
        public string Version { get; set; } = "2.0";
        public bool Encrypted { get; set; }
        public bool Compressed { get; set; }
        public List<ExportedMemory> Memories { get; set; } = [];
        public List<ExportedEntity> Entities { get; set; } = [];
        public List<ExportedRelationship> Relationships { get; set; } = [];
        public List<ExportedUserPersona> UserPersonas { get; set; } = [];
        public List<ExportedEvent>? Events { get; set; }
    }

    private sealed class ExportedMemory
    {
        public Guid MemoryId { get; set; }
        public string Content { get; set; } = "";
        public string Layer { get; set; } = "";
        public float ConfidenceScore { get; set; }
        public int HalfLifeDays { get; set; }
        public DateTimeOffset LastReinforcedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string ContentHash { get; set; } = "";
        public Guid[] CausalParents { get; set; } = [];
        public string[] Tags { get; set; } = [];
        public string? Source { get; set; }
        public string UserId { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsArchived { get; set; }
        public bool IsExpired { get; set; }
        public bool IsSplit { get; set; }
    }

    private sealed class ExportedEntity
    {
        public Guid EntityId { get; set; }
        public string Name { get; set; } = "";
        public string EntityType { get; set; } = "";
        public string? CanonicalName { get; set; }
        public float ConfidenceScore { get; set; }
        public int MemoryCount { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public bool IsActive { get; set; }
    }

    private sealed class ExportedRelationship
    {
        public Guid RelationshipId { get; set; }
        public Guid SourceEntityId { get; set; }
        public Guid TargetEntityId { get; set; }
        public string SourceName { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string RelationshipType { get; set; } = "";
        public float Confidence { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class ExportedUserPersona
    {
        public Guid Id { get; set; }
        public string UserId { get; set; } = "";
        public string AttributeType { get; set; } = "";
        public string AttributeKey { get; set; } = "";
        public string AttributeValue { get; set; } = "";
        public float Confidence { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ExportedEvent
    {
        public Guid EventId { get; set; }
        public Guid StreamId { get; set; }
        public string EventType { get; set; } = "";
        public long EventVersion { get; set; }
        public long GlobalSequence { get; set; }
        public string EventData { get; set; } = "";
        public string? Metadata { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? ContentHash { get; set; }
    }

    #endregion
}
