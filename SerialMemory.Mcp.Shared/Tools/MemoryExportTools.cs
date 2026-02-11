using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SerialMemory.Mcp.Shared.Tools;

/// <summary>
/// Memory export tools: workspace, memories, graph, user profile.
/// </summary>
public sealed class MemoryExportTools(
    NpgsqlDataSource dataSource,
    ILogger logger)
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<object> HandleExportWorkspace(JsonNode? arguments)
    {
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim() ?? $"workspace_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
        _ = arguments?["include_events"]?.GetValue<bool>() ?? false;
        var activeOnly = arguments?["active_only"]?.GetValue<bool>() ?? true;
        var encrypt = arguments?["encrypt"]?.GetValue<bool>() ?? false;
        var encryptionKey = arguments?["encryption_key"]?.GetValue<string>()?.Trim();
        var compress = arguments?["compress"]?.GetValue<bool>() ?? false;

        if (encrypt && string.IsNullOrEmpty(encryptionKey))
            throw new ArgumentException("encryption_key required when encrypt is true");

        await using var conn = await _dataSource.OpenConnectionAsync();

        var memorySql = activeOnly
            ? "SELECT memory_id, content, layer, confidence_score, created_at, source, user_id FROM memory_projections WHERE is_active = TRUE ORDER BY created_at DESC"
            : "SELECT memory_id, content, layer, confidence_score, created_at, source, user_id FROM memory_projections ORDER BY created_at DESC";

        var memories = (await conn.QueryAsync<dynamic>(memorySql)).ToList();
        var entities = (await conn.QueryAsync<dynamic>("SELECT entity_id, name, entity_type, memory_count FROM entity_projections")).ToList();
        var relationships = (await conn.QueryAsync<dynamic>(
            "SELECT r.relationship_id, r.source_entity_id, r.target_entity_id, r.relationship_type, r.confidence FROM entity_relationship_projections r")).ToList();

        var export = new
        {
            exportId = Guid.CreateVersion7(),
            exportedAt = DateTimeOffset.UtcNow,
            memories = memories.Select(m => new { m.memory_id, m.content, m.layer, m.confidence_score, m.created_at, m.source }),
            entities = entities.Select(e => new { e.entity_id, e.name, e.entity_type, e.memory_count }),
            relationships = relationships.Select(r => new { r.relationship_id, r.source_entity_id, r.target_entity_id, r.relationship_type, r.confidence })
        };

        var json = JsonSerializer.Serialize(export, _jsonOptions);
        byte[] data = Encoding.UTF8.GetBytes(json);

        if (encrypt && !string.IsNullOrEmpty(encryptionKey))
            data = await EncryptAsync(data, encryptionKey);

        if (compress)
        {
            data = await CompressAsync(data);
            if (!outputPath.EndsWith(".gz")) outputPath += ".gz";
        }

        await File.WriteAllBytesAsync(outputPath, data);

        logger.LogInformation("Workspace exported: {Memories} memories, {Entities} entities", memories.Count, entities.Count);
        return CreateTextResponse(
            $"Workspace exported.\n" +
            $"Path: {outputPath}\n" +
            $"Memories: {memories.Count}\n" +
            $"Entities: {entities.Count}\n" +
            $"Relationships: {relationships.Count}\n" +
            $"Size: {data.Length:N0} bytes");
    }

    public async Task<object> HandleExportMemories(JsonNode? arguments)
    {
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim() ?? $"memories_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
        var layerFilter = arguments?["layer"]?.GetValue<string>()?.Trim();
        var minConfidence = arguments?["min_confidence"]?.GetValue<float>() ?? 0f;
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 10000, 1, 100000);
        var format = arguments?["format"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "json";

        await using var conn = await _dataSource.OpenConnectionAsync();

        var sql = "SELECT memory_id, content, layer, confidence_score, created_at, source, user_id FROM memory_projections WHERE is_active = TRUE";
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
        sql += " ORDER BY created_at DESC LIMIT @Limit";

        var memories = (await conn.QueryAsync<dynamic>(sql, parameters)).ToList();

        string output;
        if (format == "csv")
        {
            var csv = new StringBuilder("memory_id,content,layer,confidence_score,created_at,source\n");
            foreach (var m in memories)
                csv.AppendLine($"\"{m.memory_id}\",\"{((string)m.content).Replace("\"", "\"\"")}\",\"{m.layer}\",{m.confidence_score},{m.created_at:O},\"{m.source}\"");
            output = csv.ToString();
            if (!outputPath.EndsWith(".csv")) outputPath = outputPath.Replace(".json", ".csv");
        }
        else
        {
            output = JsonSerializer.Serialize(new { exportedAt = DateTimeOffset.UtcNow, memories }, _jsonOptions);
        }

        await File.WriteAllTextAsync(outputPath, output);

        logger.LogInformation("Exported {Count} memories to {Path}", memories.Count, outputPath);
        return CreateTextResponse($"Exported {memories.Count} memories.\nPath: {outputPath}\nFormat: {format.ToUpperInvariant()}");
    }

    public async Task<object> HandleExportGraph(JsonNode? arguments)
    {
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim() ?? $"graph_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
        var format = arguments?["format"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "json";
        var includeIsolated = arguments?["include_isolated"]?.GetValue<bool>() ?? false;

        await using var conn = await _dataSource.OpenConnectionAsync();

        var entitySql = includeIsolated
            ? "SELECT entity_id, name, entity_type, memory_count FROM entity_projections WHERE is_active = TRUE"
            : @"SELECT e.entity_id, e.name, e.entity_type, e.memory_count FROM entity_projections e
                WHERE e.is_active = TRUE AND (
                    EXISTS (SELECT 1 FROM entity_relationship_projections WHERE source_entity_id = e.entity_id)
                    OR EXISTS (SELECT 1 FROM entity_relationship_projections WHERE target_entity_id = e.entity_id))";

        var entities = (await conn.QueryAsync<dynamic>(entitySql)).ToList();
        var relationships = (await conn.QueryAsync<dynamic>(
            "SELECT relationship_id, source_entity_id, target_entity_id, relationship_type, confidence FROM entity_relationship_projections")).ToList();

        string output;
        if (format == "cytoscape")
        {
            output = JsonSerializer.Serialize(new
            {
                elements = new
                {
                    nodes = entities.Select(e => new { data = new { id = e.entity_id.ToString(), label = e.name, type = e.entity_type } }),
                    edges = relationships.Select(r => new { data = new { source = r.source_entity_id.ToString(), target = r.target_entity_id.ToString(), label = r.relationship_type } })
                }
            }, _jsonOptions);
        }
        else
        {
            output = JsonSerializer.Serialize(new
            {
                nodes = entities.Select(e => new { e.entity_id, e.name, e.entity_type, e.memory_count }),
                edges = relationships.Select(r => new { r.source_entity_id, r.target_entity_id, r.relationship_type, r.confidence })
            }, _jsonOptions);
        }

        await File.WriteAllTextAsync(outputPath, output);

        logger.LogInformation("Exported graph: {Nodes} nodes, {Edges} edges", entities.Count, relationships.Count);
        return CreateTextResponse($"Graph exported.\nNodes: {entities.Count}\nEdges: {relationships.Count}\nFormat: {format.ToUpperInvariant()}\nPath: {outputPath}");
    }

    public async Task<object> HandleExportUserProfile(JsonNode? arguments)
    {
        var userId = arguments?["user_id"]?.GetValue<string>()?.Trim() ?? "default_user";
        var outputPath = arguments?["output_path"]?.GetValue<string>()?.Trim() ?? $"user_{userId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";

        await using var conn = await _dataSource.OpenConnectionAsync();

        var personas = (await conn.QueryAsync<dynamic>(
            "SELECT attribute_type, attribute_key, attribute_value, confidence FROM user_personas WHERE user_id = @UserId ORDER BY attribute_type",
            new { UserId = userId })).ToList();

        var stats = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT COUNT(*) AS total, AVG(confidence_score) AS avg_confidence FROM memory_projections WHERE user_id = @UserId AND is_active = TRUE",
            new { UserId = userId });

        var export = new
        {
            userId,
            exportedAt = DateTimeOffset.UtcNow,
            persona = personas.GroupBy(p => (string)p.attribute_type).ToDictionary(g => g.Key, g => g.Select(p => new { p.attribute_key, p.attribute_value, p.confidence })),
            memoryStats = new { total = stats?.total ?? 0, avgConfidence = stats?.avg_confidence }
        };

        var json = JsonSerializer.Serialize(export, _jsonOptions);
        await File.WriteAllTextAsync(outputPath, json);

        logger.LogInformation("Exported user profile: {UserId}", userId);
        return CreateTextResponse($"User profile exported.\nUser: {userId}\nAttributes: {personas.Count}\nPath: {outputPath}");
    }

    private static async Task<byte[]> EncryptAsync(byte[] data, string key)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        aes.GenerateIV();

        using var ms = new MemoryStream();
        await ms.WriteAsync(aes.IV);
        await using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            await cs.WriteAsync(data);
        return ms.ToArray();
    }

    private static async Task<byte[]> CompressAsync(byte[] data)
    {
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            await gzip.WriteAsync(data);
        return output.ToArray();
    }

    private static object CreateTextResponse(string text) =>
        new { content = new[] { new { type = "text", text } } };
}
