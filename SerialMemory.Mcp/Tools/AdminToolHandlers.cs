using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Core.Services;
using static SerialMemory.Mcp.McpResponseHelpers;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// Configuration for embedding model info display.
/// </summary>
internal record EmbeddingModelConfig(
    string? OpenAiApiKey,
    string OpenAiEmbedModel,
    string OllamaModel,
    string OllamaUrl);

/// <summary>
/// Handlers for admin tools: import, crawl, statistics, model info, reembed, context, integrations.
/// </summary>
internal sealed class AdminToolHandlers(
    KnowledgeGraphService kgService,
    IKnowledgeGraphStore store,
    IEmbeddingService embeddingService,
    IEntityExtractionService entityService,
    NpgsqlDataSource vectorDataSource,
    EmbeddingModelConfig modelConfig,
    ILogger logger)
{
    public Task<object> HandleGetIntegrations()
    {
        return Task.FromResult(CreateTextResponse("No integrations configured yet."));
    }

    public async Task<object> HandleImportFromCore(JsonNode? arguments)
    {
        var dataNode = arguments?["data"] ?? throw new Exception("Missing data");
        var source = arguments?["source"]?.GetValue<string>() ?? "core-import";

        var coreData = new CoreExportData();

        // Parse entities
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
                {
                    entity.Observations = obsArray.Select(o => o?.GetValue<string>() ?? "").ToList();
                }

                coreData.Entities.Add(entity);
            }
        }

        // Parse relations
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

        var result = await kgService.ImportFromCoreAsync(coreData, source);

        var text =
            $"CORE Import completed!\n\n" +
            $"Entities imported: {result.EntitiesImported}\n" +
            $"Relations imported: {result.RelationsImported}\n" +
            $"Observations imported: {result.ObservationsImported}\n";

        if (result.Errors.Count > 0)
        {
            text += $"\nWarnings/Errors ({result.Errors.Count}):\n" +
                    string.Join("\n", result.Errors.Take(10).Select(e => $"  - {e}"));
        }

        return CreateTextResponse(text);
    }

    public async Task<object> HandleCrawlRelationships(JsonNode? arguments)
    {
        var batchSize = Math.Clamp(arguments?["batch_size"]?.GetValue<int>() ?? 100, 1, 1000);
        var forceReprocess = arguments?["force_reprocess"]?.GetValue<bool>() ?? false;

        logger.LogInformation("Starting relationship crawl: batch_size={BatchSize}, force_reprocess={Force}", batchSize, forceReprocess);

        var totalEntities = 0;
        var totalRelationships = 0;
        var processedMemories = 0;
        var rowsFetched = 0;

        while (true)
        {
            List<Memory> memories;

            if (forceReprocess)
            {
                memories = await store.GetAllMemoriesAsync(batchSize, rowsFetched);
                rowsFetched += memories.Count;
            }
            else
            {
                memories = await store.GetMemoriesWithoutEntitiesAsync(batchSize);
            }

            if (memories.Count == 0) break;

            logger.LogInformation("Processing batch of {Count} memories (total processed: {Total})", memories.Count, processedMemories);

            // Phase 1: Extract entities and relationships per memory
            var perMemoryExtractions = new List<(Memory Memory, List<ExtractedEntity> Entities, List<ExtractedRelationship> Relationships)>();
            var batchEntitiesToCreate = new List<Entity>();
            var batchEntityNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var memory in memories)
            {
                var (entities, relationships) = await entityService.ExtractAllAsync(memory.Content);
                perMemoryExtractions.Add((memory, entities, relationships));

                foreach (var e in entities)
                {
                    if (batchEntityNames.Add(e.Text))
                    {
                        batchEntitiesToCreate.Add(new Entity
                        {
                            Name = e.Text,
                            EntityType = e.Label,
                            CanonicalName = e.Text.ToLowerInvariant(),
                            FirstSeenMemoryId = memory.Id,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }

                foreach (var rel in relationships)
                {
                    if (batchEntityNames.Add(rel.SourceEntity))
                    {
                        batchEntitiesToCreate.Add(new Entity
                        {
                            Name = rel.SourceEntity,
                            EntityType = "UNKNOWN",
                            CanonicalName = rel.SourceEntity.ToLowerInvariant(),
                            FirstSeenMemoryId = memory.Id,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                    if (batchEntityNames.Add(rel.TargetEntity))
                    {
                        batchEntitiesToCreate.Add(new Entity
                        {
                            Name = rel.TargetEntity,
                            EntityType = "UNKNOWN",
                            CanonicalName = rel.TargetEntity.ToLowerInvariant(),
                            FirstSeenMemoryId = memory.Id,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }

            // Phase 2: Batch-create ALL entities
            var entityIdMap = await store.CreateEntitiesBatchAsync(batchEntitiesToCreate);
            totalEntities += entityIdMap.Count;

            // Phase 3: Link entities and build relationships
            var batchRelationships = new List<EntityRelationship>();

            foreach (var (memory, entities, relationships) in perMemoryExtractions)
            {
                var entityRelevances = new Dictionary<Guid, float>();
                foreach (var entity in entities)
                {
                    if (entityIdMap.TryGetValue(entity.Text, out var entityId))
                        entityRelevances[entityId] = entity.Confidence;
                }
                await store.LinkMemoryToEntitiesBatchAsync(memory.Id, entityRelevances);

                foreach (var rel in relationships)
                {
                    if (entityIdMap.TryGetValue(rel.SourceEntity, out var sourceId) &&
                        entityIdMap.TryGetValue(rel.TargetEntity, out var targetId))
                    {
                        batchRelationships.Add(new EntityRelationship
                        {
                            SourceEntityId = sourceId,
                            TargetEntityId = targetId,
                            RelationshipType = rel.RelationType,
                            Confidence = rel.Confidence,
                            FirstSeenMemoryId = memory.Id,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }

                // Infer co-occurrence relationships
                var personEntities = entities.Where(e => e.Label == "PERSON").ToList();
                var orgEntities = entities.Where(e => e.Label == "ORG").ToList();

                foreach (var person in personEntities)
                {
                    foreach (var org in orgEntities)
                    {
                        if (entityIdMap.TryGetValue(person.Text, out var personId) &&
                            entityIdMap.TryGetValue(org.Text, out var orgId))
                        {
                            batchRelationships.Add(new EntityRelationship
                            {
                                SourceEntityId = personId,
                                TargetEntityId = orgId,
                                RelationshipType = "MENTIONED_WITH",
                                Confidence = 0.5f,
                                FirstSeenMemoryId = memory.Id,
                                CreatedAt = DateTime.UtcNow
                            });
                        }
                    }
                }

                processedMemories++;
            }

            // Phase 4: Batch-create ALL relationships
            await store.CreateRelationshipsBatchAsync(batchRelationships);
            totalRelationships += batchRelationships.Count;
        }

        var text = $"Relationship crawl completed!\n\n" +
                   $"Memories processed: {processedMemories}\n" +
                   $"Entities created: {totalEntities}\n" +
                   $"Relationships created: {totalRelationships}";

        logger.LogInformation("Crawl completed: {Memories} memories, {Entities} entities, {Relationships} relationships",
            processedMemories, totalEntities, totalRelationships);

        return CreateTextResponse(text);
    }

    public async Task<object> HandleGetGraphStatistics()
    {
        var (memoryCnt, entityCnt, relationshipCnt) = await store.GetGraphStatisticsAsync();
        var typeBreakdown = await store.GetRelationshipTypeBreakdownAsync();

        var text = $"Knowledge Graph Statistics\n\n" +
                   $"Total Memories: {memoryCnt}\n" +
                   $"Total Entities: {entityCnt}\n" +
                   $"Total Relationships: {relationshipCnt}\n\n" +
                   $"Relationship Types:\n" +
                   string.Join("\n", typeBreakdown.Select(t => $"  - {t.Key}: {t.Value}"));

        return CreateTextResponse(text);
    }

    public object HandleGetModelInfo()
    {
        var isOpenAi = !string.IsNullOrEmpty(modelConfig.OpenAiApiKey);

        var text = $"## Current Embedding Model\n\n" +
                   $"**Service:** {(isOpenAi ? "OpenAI" : "Ollama")}\n" +
                   $"**Model:** {(isOpenAi ? modelConfig.OpenAiEmbedModel : modelConfig.OllamaModel)}\n" +
                   (isOpenAi ? "" : $"**URL:** {modelConfig.OllamaUrl}\n") +
                   $"**Embedding Dimension:** {embeddingService.EmbeddingDimension}\n\n" +
                   (isOpenAi
                       ? $"## OpenAI Embedding Models\n\n" +
                         $"| Model | Dimensions | Notes |\n" +
                         $"|-------|------------|-------|\n" +
                         $"| text-embedding-3-small | 1536 | Default, fast, good quality |\n" +
                         $"| text-embedding-3-large | 3072 | Higher quality, slower |\n" +
                         $"| text-embedding-ada-002 | 1536 | Legacy model |\n\n"
                       : $"## Supported Ollama Models\n\n" +
                         $"| Model | Dimensions | Notes |\n" +
                         $"|-------|------------|-------|\n" +
                         $"| nomic-embed-text | 768 | Default, good quality |\n" +
                         $"| mxbai-embed-large | 1024 | Higher quality, slower |\n" +
                         $"| all-minilm | 384 | Fast, smaller vectors |\n\n") +
                   $"## How to Switch Models\n\n" +
                   (isOpenAi
                       ? $"Set environment variable: `OPENAI_EMBED_MODEL=<model-name>`\n" +
                         $"Then restart MCP server.\n\n" +
                         $"To switch to Ollama, unset OPENAI_API_KEY."
                       : $"1. Pull the new model: `ollama pull <model-name>`\n" +
                         $"2. Update environment variables:\n" +
                         $"   ```\n" +
                         $"   OLLAMA_MODEL=<model-name>\n" +
                         $"   OLLAMA_EMBEDDING_DIM=<dimension>\n" +
                         $"   ```\n" +
                         $"3. If dimension changed, migrate database\n" +
                         $"4. Restart MCP server and run `reembed_memories` with `force_all: true`");

        return CreateTextResponse(text);
    }

    public async Task<object> HandleReembedMemories(JsonNode? arguments)
    {
        var forceAll = arguments?["force_all"]?.GetValue<bool>() ?? false;
        var batchSize = Math.Clamp(arguments?["batch_size"]?.GetValue<int>() ?? 100, 1, 1000);

        logger.LogInformation("Starting re-embedding: force_all={ForceAll}, batch_size={BatchSize}", forceAll, batchSize);

        var totalMemories = await store.GetMemoryCountAsync();
        long totalToProcess;

        if (forceAll)
        {
            totalToProcess = totalMemories;
        }
        else
        {
            await using var countConn = await vectorDataSource.OpenConnectionAsync();
            totalToProcess = await Dapper.SqlMapper.ExecuteScalarAsync<long>(countConn,
                "SELECT COUNT(*) FROM memories WHERE embedding IS NULL");
        }

        var processedCount = 0;
        var errorCount = 0;
        var stopwatch = Stopwatch.StartNew();

        await using var reembedConn = await vectorDataSource.OpenConnectionAsync();

        if (forceAll)
        {
            var rowsFetched = 0;

            while (true)
            {
                var memories = await store.GetAllMemoriesAsync(batchSize, rowsFetched);
                if (memories.Count == 0) break;

                rowsFetched += memories.Count;

                var texts = memories.Select(m => m.Content).ToList();
                var embeddings = await embeddingService.EmbedBatchAsync(texts);

                for (var i = 0; i < memories.Count; i++)
                {
                    try
                    {
                        await using var cmd = new NpgsqlCommand(
                            "UPDATE memories SET embedding = @Embedding WHERE id = @Id", reembedConn);
                        cmd.Parameters.AddWithValue("@Id", memories[i].Id);
                        cmd.Parameters.AddWithValue("@Embedding", new Vector(embeddings[i]));
                        await cmd.ExecuteNonQueryAsync();

                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to re-embed memory {MemoryId}", memories[i].Id);
                        errorCount++;
                    }
                }

                logger.LogInformation("Re-embedding progress: {Processed}/{Total} ({Errors} errors)",
                    processedCount, totalToProcess, errorCount);
            }
        }
        else
        {
            var memories = await store.GetMemoriesWithNullEmbeddingsAsync(batchSize);

            if (memories.Count > 0)
            {
                var texts = memories.Select(m => m.Content).ToList();
                var embeddings = await embeddingService.EmbedBatchAsync(texts);

                for (var i = 0; i < memories.Count; i++)
                {
                    try
                    {
                        await using var cmd = new NpgsqlCommand(
                            "UPDATE memories SET embedding = @Embedding WHERE id = @Id", reembedConn);
                        cmd.Parameters.AddWithValue("@Id", memories[i].Id);
                        cmd.Parameters.AddWithValue("@Embedding", new Vector(embeddings[i]));
                        await cmd.ExecuteNonQueryAsync();

                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to re-embed memory {MemoryId}", memories[i].Id);
                        errorCount++;
                    }
                }
            }
        }

        stopwatch.Stop();

        var rate = stopwatch.Elapsed.TotalSeconds > 0 ? processedCount / stopwatch.Elapsed.TotalSeconds : 0;

        long remaining;
        if (forceAll)
        {
            remaining = errorCount;
        }
        else
        {
            await using var countConn = await vectorDataSource.OpenConnectionAsync();
            remaining = await Dapper.SqlMapper.ExecuteScalarAsync<long>(countConn,
                "SELECT COUNT(*) FROM memories WHERE embedding IS NULL");
        }

        var text = $"Re-embedding completed!\n\n" +
                   $"**Processed:** {processedCount} memories\n" +
                   $"**Errors:** {errorCount}\n" +
                   $"**Total memories:** {totalMemories}\n" +
                   $"**Duration:** {stopwatch.Elapsed:hh\\:mm\\:ss}\n" +
                   $"**Rate:** {rate:F1} memories/second\n" +
                   $"**Embedding Dimension:** {embeddingService.EmbeddingDimension}";

        if (forceAll)
        {
            if (errorCount > 0)
            {
                text += $"\n\n**{errorCount} memories failed.** Check logs for details.";
            }
            else
            {
                text += "\n\n**All memories have been re-embedded successfully.**";
            }
        }
        else if (remaining > 0)
        {
            var estimatedBatches = (int)Math.Ceiling((double)remaining / batchSize);
            text += $"\n\n**{remaining} memories remaining.** Run `reembed_memories` {estimatedBatches} more time(s) to complete.";
        }
        else
        {
            text += "\n\n**All memories have embeddings.**";
        }

        return CreateTextResponse(text);
    }

    public async Task<object> HandleInstantiateContext(JsonNode? arguments)
    {
        var projectOrSubject = arguments?["project_or_subject"]?.GetValue<string>()?.Trim();
        var daysBack = Math.Clamp(arguments?["days_back"]?.GetValue<int>() ?? 3, 1, 30);
        var limit = Math.Clamp(arguments?["limit"]?.GetValue<int>() ?? 50, 1, 200);
        var includeEntities = arguments?["include_entities"]?.GetValue<bool>() ?? true;

        logger.LogInformation("Instantiating context: project={Project}, days_back={DaysBack}, limit={Limit}",
            projectOrSubject ?? "(all)", daysBack, limit);

        var context = await kgService.GetPreviousDayContextAsync(projectOrSubject, daysBack, limit, includeEntities);

        var text = new StringBuilder();
        text.AppendLine("# Previous Session Context");
        text.AppendLine();
        text.AppendLine($"**Period:** {context.FromDate:yyyy-MM-dd} to {context.ToDate:yyyy-MM-dd}");

        if (!string.IsNullOrWhiteSpace(projectOrSubject))
        {
            text.AppendLine($"**Filter:** {projectOrSubject}");
        }

        text.AppendLine($"**Memories Found:** {context.MemoryCount} ({context.RecentMemoryCount} recent, {context.ContextMemoryCount} contextual)");
        text.AppendLine();
        text.AppendLine("## Summary");
        text.AppendLine(context.SessionSummary);
        text.AppendLine();

        // Load typed memory sections
        try
        {
            var typedSections = new (string type, string heading, int maxItems)[]
            {
                ("error", "Recent Errors", 5),
                ("decision", "Active Decisions", 5),
                ("pattern", "Known Patterns", 5),
                ("session_summary", "Session Summaries", 3)
            };

            foreach (var (type, heading, maxItems) in typedSections)
            {
                try
                {
                    var typed = await kgService.GetMemoriesByTypeAsync(type, maxItems);
                    if (typed.Count > 0)
                    {
                        text.AppendLine($"## {heading}");
                        text.AppendLine();
                        foreach (var m in typed)
                        {
                            var preview = m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content;
                            text.AppendLine($"- [{m.CreatedAt:yyyy-MM-dd}] {preview}");
                        }
                        text.AppendLine();
                    }
                }
                catch { /* Typed memory query failed - memory_type column may not exist yet */ }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load typed memory sections (memory_type column may not exist)");
        }

        if (context.TopEntities.Count > 0)
        {
            text.AppendLine("## Key Entities");
            foreach (var entity in context.TopEntities)
            {
                text.AppendLine($"- **{entity.Name}** ({entity.Type})");
            }
            text.AppendLine();
        }

        if (context.TopRelationships.Count > 0)
        {
            text.AppendLine("## Key Relationships");
            foreach (var rel in context.TopRelationships.Take(5))
            {
                text.AppendLine($"- {rel.Source} --{rel.Type}--> {rel.Target}");
            }
            text.AppendLine();
        }

        if (context.Memories.Count > 0)
        {
            text.AppendLine("## Memories");
            text.AppendLine();

            var recentMemories = context.Memories.Where(m => m.CreatedAt >= context.FromDate && m.CreatedAt <= context.ToDate).ToList();
            var contextualMemories = context.Memories.Where(m => m.CreatedAt < context.FromDate).ToList();

            if (recentMemories.Count > 0)
            {
                text.AppendLine("### Recent (In Date Range)");
                text.AppendLine();

                foreach (var memory in recentMemories.Take(10))
                {
                    text.AppendLine($"**{memory.CreatedAt:yyyy-MM-dd HH:mm}**");

                    var contentPreview = memory.Content.Length > 300
                        ? memory.Content[..300] + "..."
                        : memory.Content;
                    text.AppendLine(contentPreview);

                    if (memory.Entities.Count > 0)
                    {
                        text.AppendLine($"*Entities: {string.Join(", ", memory.Entities.Select(e => e.Name))}*");
                    }

                    text.AppendLine();
                }
            }

            if (contextualMemories.Count > 0)
            {
                text.AppendLine("### Contextual (Older Background)");
                text.AppendLine();

                foreach (var memory in contextualMemories.Take(5))
                {
                    text.AppendLine($"**{memory.CreatedAt:yyyy-MM-dd}** *(older context)*");

                    var contentPreview = memory.Content.Length > 200
                        ? memory.Content[..200] + "..."
                        : memory.Content;
                    text.AppendLine(contentPreview);

                    if (memory.Entities.Count > 0)
                    {
                        text.AppendLine($"*Entities: {string.Join(", ", memory.Entities.Select(e => e.Name))}*");
                    }

                    text.AppendLine();
                }
            }

            if (context.Memories.Count > 10)
            {
                text.AppendLine($"*... and {context.Memories.Count - 10} more memories*");
            }
        }

        // Load active goals
        try
        {
            var goals = await kgService.GetActiveGoalsAsync();
            if (goals.Count > 0)
            {
                text.AppendLine("## Active Goals");
                text.AppendLine();
                foreach (var goal in goals)
                {
                    var priorityLabel = goal.Confidence >= 0.8f ? "HIGH" : goal.Confidence >= 0.5f ? "MEDIUM" : "LOW";
                    text.AppendLine($"- **{goal.AttributeKey}** [{priorityLabel}] — {goal.AttributeValue}");
                }
                text.AppendLine();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load goals for context (user_personas table may not exist)");
        }

        logger.LogInformation("Context instantiated: {MemoryCount} memories, {EntityCount} entities",
            context.MemoryCount, context.TopEntities.Count);

        return CreateTextResponse(text.ToString());
    }
}
