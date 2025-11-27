using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure.Services;

/// <summary>
/// Orchestrates multi-model reasoning with parallel execution, timeouts, and result merging.
/// </summary>
public sealed class MultiModelReasoningService : IMultiModelReasoningService
{
    private readonly IKnowledgeGraphStore _store;
    private readonly IEngineeringReasoningService _ruleBasedService;
    private readonly IReasoningModelFactory _modelFactory;
    private readonly ILogger<MultiModelReasoningService> _logger;

    public MultiModelReasoningService(
        IKnowledgeGraphStore store,
        IEngineeringReasoningService ruleBasedService,
        IReasoningModelFactory modelFactory,
        ILogger<MultiModelReasoningService> logger)
    {
        _store = store;
        _ruleBasedService = ruleBasedService;
        _modelFactory = modelFactory;
        _logger = logger;
    }

    public async Task<MultiModelReasoningResult> ReasonAsync(
        string? projectFilter = null,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Fetch graph data
        var entities = await _store.GetAllEntitiesAsync(10000, cancellationToken);
        var relationships = await _store.GetAllRelationshipsAsync(10000, cancellationToken);

        // Filter by project if specified
        if (!string.IsNullOrEmpty(projectFilter))
        {
            var projectEntity = entities.FirstOrDefault(e =>
                e.EntityType == "PROJECT" &&
                e.Name.Equals(projectFilter, StringComparison.OrdinalIgnoreCase));

            if (projectEntity != null)
            {
                var connectedIds = GetConnectedEntityIds(projectEntity.Id, relationships);
                entities = entities.Where(e => connectedIds.Contains(e.Id)).ToList();
                relationships = relationships.Where(r =>
                    connectedIds.Contains(r.SourceEntityId) ||
                    connectedIds.Contains(r.TargetEntityId)).ToList();
            }
        }

        // Create reasoning input
        var input = new ReasoningInput
        {
            Content = BuildGraphSummary(entities, relationships),
            Entities = entities,
            Relationships = relationships,
            Context = new Dictionary<string, object>
            {
                ["projectFilter"] = projectFilter ?? "",
                ["entityCount"] = entities.Count,
                ["relationshipCount"] = relationships.Count
            }
        };

        var inputHash = ComputeInputHash(input);

        return await ExecuteReasoningAsync(input, inputHash, maxDurationMs, sw, cancellationToken);
    }

    public async Task<MultiModelReasoningResult> ReasonMemoryAsync(
        Guid memoryId,
        int maxDurationMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Get entities for memory
        var memoryEntities = await _store.GetEntitiesForMemoryAsync(memoryId, cancellationToken);
        if (memoryEntities.Count == 0)
        {
            return new MultiModelReasoningResult
            {
                TotalDurationMs = (int)sw.ElapsedMilliseconds,
                InputHash = memoryId.ToString()
            };
        }

        // Expand to connected entities
        var entityIds = memoryEntities.Select(e => e.Id).ToHashSet();
        var allRelationships = await _store.GetAllRelationshipsAsync(10000, cancellationToken);
        var relevantRels = allRelationships
            .Where(r => entityIds.Contains(r.SourceEntityId) || entityIds.Contains(r.TargetEntityId))
            .ToList();

        var connectedIds = new HashSet<Guid>(entityIds);
        foreach (var rel in relevantRels)
        {
            connectedIds.Add(rel.SourceEntityId);
            connectedIds.Add(rel.TargetEntityId);
        }

        var allEntities = await _store.GetAllEntitiesAsync(10000, cancellationToken);
        var entities = allEntities.Where(e => connectedIds.Contains(e.Id)).ToList();
        var relationships = relevantRels;

        // Create reasoning input
        var input = new ReasoningInput
        {
            Content = BuildGraphSummary(entities, relationships),
            Entities = entities,
            Relationships = relationships,
            Context = new Dictionary<string, object>
            {
                ["memoryId"] = memoryId.ToString(),
                ["entityCount"] = entities.Count,
                ["relationshipCount"] = relationships.Count
            }
        };

        var inputHash = ComputeInputHash(input);

        return await ExecuteReasoningAsync(input, inputHash, maxDurationMs, sw, cancellationToken);
    }

    private async Task<MultiModelReasoningResult> ExecuteReasoningAsync(
        ReasoningInput input,
        string inputHash,
        int maxDurationMs,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var traces = new List<ModelTrace>();
        var allModelResults = new List<ModelResult>();

        // Get all models from factory
        var models = _modelFactory.CreateModels().ToList();

        _logger.LogInformation("Starting multi-model reasoning with {ModelCount} models, max duration {MaxDuration}ms",
            models.Count, maxDurationMs);

        // Create overall timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(maxDurationMs);

        // Also include rule-based analysis as a "model"
        var ruleBasedTrace = new ModelTrace
        {
            Model = "RuleBasedEngine",
            Version = "1.0.0",
            Role = ReasoningRole.Structural,
            DurationMs = 0,
            Success = true
        };

        try
        {
            var rulesSw = Stopwatch.StartNew();
            var ruleBasedResult = await _ruleBasedService.AnalyzeAsync(null, cts.Token);
            rulesSw.Stop();

            ruleBasedTrace = ruleBasedTrace with { DurationMs = (int)rulesSw.ElapsedMilliseconds };

            // Convert rule-based insights to model insights
            var ruleBasedModelResult = new ModelResult
            {
                ModelName = "RuleBasedEngine",
                Version = "1.0.0",
                Role = ReasoningRole.Structural,
                Success = true,
                DurationMs = (int)rulesSw.ElapsedMilliseconds,
                Insights = ruleBasedResult.Insights.Select(i => new ModelInsight
                {
                    Type = i.Severity switch
                    {
                        InsightSeverity.Risk => MultiModelInsightType.Risk,
                        InsightSeverity.Conflict => MultiModelInsightType.Conflict,
                        InsightSeverity.Warning => MultiModelInsightType.Risk,
                        _ => MultiModelInsightType.Info
                    },
                    Message = $"{i.Title}: {i.Description}",
                    Confidence = i.Confidence,
                    AffectedEntities = i.InvolvedEntities.Select(e => e.Id).ToList()
                }).ToList()
            };

            allModelResults.Add(ruleBasedModelResult);
        }
        catch (OperationCanceledException)
        {
            ruleBasedTrace = ruleBasedTrace with { Success = false, Error = "Timeout" };
            _logger.LogWarning("Rule-based analysis timed out");
        }
        catch (Exception ex)
        {
            ruleBasedTrace = ruleBasedTrace with { Success = false, Error = ex.Message };
            _logger.LogError(ex, "Rule-based analysis failed");
        }

        traces.Add(ruleBasedTrace);

        // Run all models in parallel with individual timeouts
        var modelTasks = models.Select(async model =>
        {
            var modelSw = Stopwatch.StartNew();
            var trace = new ModelTrace
            {
                Model = model.Name,
                Version = model.Version,
                Role = model.Role,
                DurationMs = 0,
                Success = false
            };

            try
            {
                using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                modelCts.CancelAfter(model.Timeout);

                var result = await model.ReasonAsync(input, modelCts.Token);
                modelSw.Stop();

                trace = trace with
                {
                    DurationMs = (int)modelSw.ElapsedMilliseconds,
                    Success = result.Success,
                    Error = result.Error
                };

                return (Trace: trace, Result: result);
            }
            catch (OperationCanceledException)
            {
                modelSw.Stop();
                trace = trace with
                {
                    DurationMs = (int)modelSw.ElapsedMilliseconds,
                    Error = "Timeout"
                };
                _logger.LogWarning("Model {Model} timed out after {Duration}ms", model.Name, modelSw.ElapsedMilliseconds);
                return (Trace: trace, Result: (ModelResult?)null);
            }
            catch (Exception ex)
            {
                modelSw.Stop();
                trace = trace with
                {
                    DurationMs = (int)modelSw.ElapsedMilliseconds,
                    Error = ex.Message
                };
                _logger.LogError(ex, "Model {Model} failed", model.Name);
                return (Trace: trace, Result: (ModelResult?)null);
            }
        }).ToList();

        var modelResults = await Task.WhenAll(modelTasks);

        foreach (var (trace, result) in modelResults)
        {
            traces.Add(trace);
            if (result != null)
            {
                allModelResults.Add(result);
            }
        }

        // Merge insights from all models
        var mergedInsights = MergeInsights(allModelResults);

        sw.Stop();

        _logger.LogInformation("Multi-model reasoning completed in {Duration}ms. Models: {Total}, Successful: {Success}, Insights: {Insights}",
            sw.ElapsedMilliseconds, traces.Count, traces.Count(t => t.Success), mergedInsights.Count);

        return new MultiModelReasoningResult
        {
            Insights = mergedInsights,
            Trace = traces,
            TotalDurationMs = (int)sw.ElapsedMilliseconds,
            ModelsUsed = traces.Count,
            SuccessfulModels = traces.Count(t => t.Success),
            InputHash = inputHash
        };
    }

    private List<MergedInsight> MergeInsights(List<ModelResult> results)
    {
        // Group similar insights by message similarity and type
        var insightGroups = new Dictionary<string, List<(ModelInsight Insight, string Model)>>();

        foreach (var result in results.Where(r => r.Success))
        {
            foreach (var insight in result.Insights)
            {
                var key = $"{insight.Type}:{NormalizeMessage(insight.Message)}";

                if (!insightGroups.ContainsKey(key))
                {
                    insightGroups[key] = [];
                }
                insightGroups[key].Add((insight, result.ModelName));
            }
        }

        // Create merged insights with confidence based on agreement
        var merged = new List<MergedInsight>();

        foreach (var (_, group) in insightGroups)
        {
            var firstInsight = group[0].Insight;
            var sourceModels = group.Select(g => g.Model).Distinct().ToList();

            // Boost confidence based on model agreement
            var baseConfidence = group.Average(g => g.Insight.Confidence);
            var agreementBonus = Math.Min(0.2f * (sourceModels.Count - 1), 0.3f);
            var finalConfidence = Math.Min(baseConfidence + agreementBonus, 1.0f);

            merged.Add(new MergedInsight
            {
                Type = firstInsight.Type,
                Message = firstInsight.Message,
                Confidence = finalConfidence,
                SourceModels = sourceModels,
                AgreementCount = sourceModels.Count,
                AffectedEntities = firstInsight.AffectedEntities
            });
        }

        // Sort by confidence descending, then by type severity
        return merged
            .OrderByDescending(m => m.Confidence)
            .ThenByDescending(m => m.Type switch
            {
                MultiModelInsightType.Risk => 3,
                MultiModelInsightType.Conflict => 2,
                MultiModelInsightType.Optimization => 1,
                _ => 0
            })
            .ToList();
    }

    private static string NormalizeMessage(string message)
    {
        // Normalize for comparison - lowercase, trim, remove extra whitespace
        return string.Join(" ", message.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static HashSet<Guid> GetConnectedEntityIds(Guid rootId, List<EntityRelationship> relationships)
    {
        var connected = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var neighbors = relationships
                .Where(r => r.SourceEntityId == currentId || r.TargetEntityId == currentId)
                .SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId })
                .Where(id => !connected.Contains(id));

            foreach (var neighbor in neighbors)
            {
                connected.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return connected;
    }

    private static string BuildGraphSummary(List<Entity> entities, List<EntityRelationship> relationships)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Graph with {entities.Count} entities and {relationships.Count} relationships.");
        sb.AppendLine();
        sb.AppendLine("Entities by type:");

        var byType = entities.GroupBy(e => e.EntityType).OrderByDescending(g => g.Count());
        foreach (var group in byType.Take(10))
        {
            sb.AppendLine($"  {group.Key}: {group.Count()}");
        }

        sb.AppendLine();
        sb.AppendLine("Relationships by type:");

        var relByType = relationships.GroupBy(r => r.RelationshipType).OrderByDescending(g => g.Count());
        foreach (var group in relByType.Take(10))
        {
            sb.AppendLine($"  {group.Key}: {group.Count()}");
        }

        return sb.ToString();
    }

    private static string ComputeInputHash(ReasoningInput input)
    {
        var json = JsonSerializer.Serialize(new
        {
            content = input.Content,
            entityCount = input.Entities?.Count ?? 0,
            relationshipCount = input.Relationships?.Count ?? 0
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
