using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.EventSourcing.Events;
using SerialMemory.EventSourcing.Store;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// MCP tool handlers for memory export operations.
/// Supports workspace, memories, graph, and user profile exports.
/// </summary>
public sealed class MemoryExportTools
{
    private readonly IEventStore _eventStore;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public MemoryExportTools(
        IEventStore eventStore,
        string connectionString,
        ILogger logger)
    {
        _eventStore = eventStore;
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

        await using var conn = await _dataSource.OpenConnectionAsync();

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

        await using var conn = await _dataSource.OpenConnectionAsync();

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

        await using var conn = await _dataSource.OpenConnectionAsync();

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

        await using var conn = await _dataSource.OpenConnectionAsync();

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
