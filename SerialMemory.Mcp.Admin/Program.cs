using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Auth;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Services;
using SerialMemory.Mcp.Shared;
using SerialMemory.Mcp.Shared.Tools;

// =================================================================
// SerialMemory MCP Admin Server
// Maintenance, diagnostics, export, model management (~21 tools)
// Load only when needed: "export workspace", "audit integrity", etc.
// =================================================================

var server = new AdminMcpServer();
await server.RunAsync();

public sealed class AdminMcpServer : McpServerBase
{
    private readonly MemoryLifecycleTools _lifecycleTools;
    private readonly MemoryObservabilityTools _observabilityTools;
    private readonly MemorySafetyTools _safetyTools;
    private readonly MemoryExportTools _exportTools;

    public AdminMcpServer() : base("serialmemory-admin", "3.0.0")
    {
        _lifecycleTools = new MemoryLifecycleTools(EventStore, EmbeddingService, Logger);
        _observabilityTools = new MemoryObservabilityTools(EventStore, VectorDataSource, Logger);
        _safetyTools = new MemorySafetyTools(EventStore, VectorDataSource, Logger);
        _exportTools = new MemoryExportTools(VectorDataSource, Logger);
        Logger.LogInformation("Admin MCP server initialized (21 maintenance/export tools)");
    }

    /// <summary>
    /// Required scope for Admin MCP server: serialmemory.admin
    /// </summary>
    protected override string RequiredScope => Scopes.Admin;

    protected override object HandleToolsList()
    {
        return new { tools = ToolDefinitions.GetAdminTools() };
    }

    protected override async Task<object> HandleToolsCall(JsonNode? @params)
    {
        // Authenticate request and set tenant context
        var authError = await AuthenticateRequestAsync(@params);
        if (authError != null)
            return authError;

        // Admin tools require admin role
        if (!IsAdmin)
        {
            return CreateAuthErrorResponse(AuthenticationResult.InsufficientRole("admin"));
        }

        var toolName = @params?["name"]?.GetValue<string>();

        // Enforce usage limits before execution
        var usageError = await EnforceUsageLimitsAsync(toolName);
        if (usageError != null)
            return usageError;

        var arguments = @params?["arguments"];
        var sw = Stopwatch.StartNew();

        // Log admin action BEFORE execution (tamper-evident audit)
        AdminActionEntry? auditEntry = null;
        if (CurrentAuth?.TenantId.HasValue == true)
        {
            try
            {
                auditEntry = await AdminAuditService.LogActionAsync(
                    CurrentAuth.TenantId.Value,
                    CurrentAuth.UserId ?? "unknown",
                    toolName ?? "unknown",
                    arguments?.ToJsonString());
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to log admin action for audit");
            }
        }

        try
        {
            var result = toolName switch
            {
                // Graph traversal
                "memory_multi_hop_search" => await HandleMultiHopSearch(arguments),
                "crawl_relationships" => await HandleCrawlRelationships(arguments),
                "get_graph_statistics" => await HandleGetGraphStatistics(),

                // Lifecycle maintenance
                "memory_decay" => await _lifecycleTools.HandleMemoryDecay(arguments),

                // Observability
                "memory_trace" => await _observabilityTools.HandleMemoryTrace(arguments),
                "memory_lineage" => await _observabilityTools.HandleMemoryLineage(arguments),
                "memory_explain" => await _observabilityTools.HandleMemoryExplain(arguments),
                "memory_conflicts" => await _observabilityTools.HandleMemoryConflicts(arguments),

                // Safety
                "detect_contradictions" => await _safetyTools.HandleDetectContradictions(arguments),
                "detect_hallucinations" => await _safetyTools.HandleDetectHallucinations(arguments),
                "verify_memory_integrity" => await _safetyTools.HandleVerifyIntegrity(arguments),
                "scan_loops" => await _safetyTools.HandleScanLoops(arguments),

                // Import/Export
                "import_from_core" => await HandleImportFromCore(arguments),
                "export_workspace" => await _exportTools.HandleExportWorkspace(arguments),
                "export_memories" => await _exportTools.HandleExportMemories(arguments),
                "export_graph" => await _exportTools.HandleExportGraph(arguments),
                "export_user_profile" => await _exportTools.HandleExportUserProfile(arguments),

                // Model management
                "get_model_info" => HandleGetModelInfo(),
                "reembed_memories" => await HandleReembedMemories(arguments),
                "get_integrations" => CreateTextResponse("No integrations configured."),

                _ => throw new Exception($"Unknown tool: {toolName}")
            };

            sw.Stop();

            // Complete audit entry with success
            if (auditEntry != null)
            {
                try
                {
                    await AdminAuditService.CompleteActionAsync(
                        auditEntry.Id,
                        (int)sw.ElapsedMilliseconds,
                        success: true);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to complete admin audit entry");
                }
            }

            Logger.LogDebug("Tool {Tool} completed in {Ms}ms (tenant: {TenantId})",
                toolName, sw.ElapsedMilliseconds, CurrentAuth?.TenantId);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();

            // Complete audit entry with failure
            if (auditEntry != null)
            {
                try
                {
                    await AdminAuditService.CompleteActionAsync(
                        auditEntry.Id,
                        (int)sw.ElapsedMilliseconds,
                        success: false,
                        ex.Message);
                }
                catch (Exception auditEx)
                {
                    Logger.LogWarning(auditEx, "Failed to complete admin audit entry");
                }
            }

            Logger.LogError(ex, "Error executing tool {Tool}", toolName);
            return CreateErrorResponse(ex.Message);
        }
    }

    private async Task<object> HandleMultiHopSearch(JsonNode? arguments)
    {
        var query = arguments?["query"]?.GetValue<string>().Trim();
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("Query required");

        var hops = Math.Clamp(arguments?["hops"]?.GetValue<int>() ?? 2, 1, 3);
        var maxResultsPerHop = Math.Clamp(arguments?["max_results_per_hop"]?.GetValue<int>() ?? 5, 1, 50);

        var result = await KgService.MultiHopSearchAsync(query, hops, maxResultsPerHop);

        return CreateTextResponse(
            $"Multi-hop search ({result.Hops} hops):\n" +
            $"Memories: {result.Memories.Count}\n" +
            $"Entities: {result.Entities.Count}\n" +
            $"Relationships: {result.Relationships.Count}\n\n" +
            string.Join("\n", result.Memories.Take(5).Select((m, i) =>
                $"{i + 1}. {m.Content[..Math.Min(100, m.Content.Length)]}...")));
    }

    private async Task<object> HandleCrawlRelationships(JsonNode? arguments)
    {
        var batchSize = Math.Clamp(arguments?["batch_size"]?.GetValue<int>() ?? 100, 1, 1000);
        var forceReprocess = arguments?["force_reprocess"]?.GetValue<bool>() ?? false;

        var totalEntities = 0;
        var totalRelationships = 0;
        var processedMemories = 0;
        var rowsFetched = 0;

        while (true)
        {
            var memories = forceReprocess
                ? await Store.GetAllMemoriesAsync(batchSize, rowsFetched)
                : await Store.GetMemoriesWithoutEntitiesAsync(batchSize);

            if (memories.Count == 0) break;

            if (forceReprocess) rowsFetched += memories.Count;

            foreach (var memory in memories)
            {
                var (entities, relationships) = await EntityService.ExtractAllAsync(memory.Content);

                foreach (var entity in entities)
                {
                    var entityId = await Store.CreateEntityAsync(new SerialMemory.Core.Models.Entity
                    {
                        Id = Guid.CreateVersion7(),
                        Name = entity.Text,
                        EntityType = entity.Label,
                        CanonicalName = entity.Text.ToLowerInvariant(),
                        FirstSeenMemoryId = memory.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                    await Store.LinkMemoryToEntityAsync(memory.Id, entityId, entity.Confidence);
                    totalEntities++;
                }

                totalRelationships += relationships.Count();
                processedMemories++;
            }
        }

        return CreateTextResponse(
            $"Crawl completed.\n" +
            $"Memories: {processedMemories}\n" +
            $"Entities: {totalEntities}\n" +
            $"Relationships: {totalRelationships}");
    }

    private async Task<object> HandleGetGraphStatistics()
    {
        var memoryCnt = await Store.GetMemoryCountAsync();
        var entityCnt = await Store.GetEntityCountAsync();
        var relationshipCnt = await Store.GetRelationshipCountAsync();

        var relationships = await Store.GetAllRelationshipsAsync(500);
        var typeBreakdown = relationships
            .GroupBy(r => r.RelationshipType)
            .Select(g => $"  - {g.Key}: {g.Count()}")
            .Take(10);

        return CreateTextResponse(
            $"## Graph Statistics\n\n" +
            $"Memories: {memoryCnt}\n" +
            $"Entities: {entityCnt}\n" +
            $"Relationships: {relationshipCnt}\n\n" +
            $"Types:\n{string.Join("\n", typeBreakdown)}");
    }

    private async Task<object> HandleImportFromCore(JsonNode? arguments)
    {
        var dataNode = arguments?["data"] ?? throw new Exception("Missing data");
        var source = arguments["source"]?.GetValue<string>() ?? "core-import";

        var coreData = new CoreExportData();

        if (dataNode["entities"] is JsonArray entitiesArray)
        {
            foreach (var entityNode in entitiesArray)
            {
                var entity = new CoreEntity
                {
                    Name = entityNode?["name"]?.GetValue<string>() ?? "Unknown",
                    EntityType = entityNode?["entityType"]?.GetValue<string>()
                };

                if (entityNode?["observations"] is JsonArray obsArray)
                    entity.Observations = obsArray.Select(o => o?.GetValue<string>() ?? "").ToList();

                coreData.Entities.Add(entity);
            }
        }

        if (dataNode["relations"] is JsonArray relationsArray)
        {
            foreach (var relNode in relationsArray)
            {
                coreData.Relations.Add(new CoreRelation
                {
                    From = relNode?["from"]?.GetValue<string>() ?? "",
                    To = relNode?["to"]?.GetValue<string>() ?? "",
                    RelationType = relNode?["relationType"]?.GetValue<string>() ?? "RELATED_TO"
                });
            }
        }

        var result = await KgService.ImportFromCoreAsync(coreData, source);

        return CreateTextResponse(
            $"CORE Import completed.\n" +
            $"Entities: {result.EntitiesImported}\n" +
            $"Relations: {result.RelationsImported}\n" +
            $"Observations: {result.ObservationsImported}");
    }

    private object HandleGetModelInfo()
    {
        return CreateTextResponse(
            $"## Embedding Model\n\n" +
            $"Service: Ollama\n" +
            $"Dimension: {EmbeddingService.EmbeddingDimension}\n\n" +
            $"## Models\n" +
            $"- nomic-embed-text: 768 dim (default)\n" +
            $"- mxbai-embed-large: 1024 dim\n" +
            $"- all-minilm: 384 dim\n\n" +
            $"To switch: update OLLAMA_MODEL env, migrate db if needed, reembed.");
    }

    private async Task<object> HandleReembedMemories(JsonNode? arguments)
    {
        var forceAll = arguments?["force_all"]?.GetValue<bool>() ?? false;
        var batchSize = Math.Clamp(arguments?["batch_size"]?.GetValue<int>() ?? 100, 1, 1000);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);
        dataSourceBuilder.UseVector();
        var vectorDataSource = dataSourceBuilder.Build();

        var totalMemories = await Store.GetMemoryCountAsync();
        var processedCount = 0;
        var errorCount = 0;

        if (forceAll)
        {
            var rowsFetched = 0;
            while (true)
            {
                var memories = await Store.GetAllMemoriesAsync(batchSize, rowsFetched);
                if (memories.Count == 0) break;

                rowsFetched += memories.Count;

                foreach (var memory in memories)
                {
                    try
                    {
                        var embedding = await EmbeddingService.EmbedTextAsync(memory.Content);
                        await using var connection = await vectorDataSource.OpenConnectionAsync();
                        await using var cmd = new NpgsqlCommand(
                            "UPDATE memories SET embedding = @Embedding WHERE id = @Id", connection);
                        cmd.Parameters.AddWithValue("@Id", memory.Id);
                        cmd.Parameters.AddWithValue("@Embedding", new Pgvector.Vector(embedding));
                        await cmd.ExecuteNonQueryAsync();
                        processedCount++;
                    }
                    catch
                    {
                        errorCount++;
                    }
                }
            }
        }
        else
        {
            var memories = await Store.GetMemoriesWithNullEmbeddingsAsync(batchSize);
            foreach (var memory in memories)
            {
                try
                {
                    var embedding = await EmbeddingService.EmbedTextAsync(memory.Content);
                    await using var connection = await vectorDataSource.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand(
                        "UPDATE memories SET embedding = @Embedding WHERE id = @Id", connection);
                    cmd.Parameters.AddWithValue("@Id", memory.Id);
                    cmd.Parameters.AddWithValue("@Embedding", new Pgvector.Vector(embedding));
                    await cmd.ExecuteNonQueryAsync();
                    processedCount++;
                }
                catch
                {
                    errorCount++;
                }
            }
        }

        return CreateTextResponse(
            $"Re-embedding completed.\n" +
            $"Processed: {processedCount}\n" +
            $"Errors: {errorCount}\n" +
            $"Total: {totalMemories}\n" +
            $"Dimension: {EmbeddingService.EmbeddingDimension}");
    }
}
