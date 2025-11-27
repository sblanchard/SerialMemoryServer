using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Mcp.Tools;

/// <summary>
/// MCP tool handlers for engineering reasoning and visualization.
/// </summary>
public sealed class EngineeringReasoningTools
{
    private readonly IEngineeringReasoningService _reasoningService;
    private readonly IGraphVisualizationService _visualizationService;
    private readonly IMultiModelReasoningService _multiModelService;
    private readonly ILogger _logger;

    public EngineeringReasoningTools(
        IEngineeringReasoningService reasoningService,
        IGraphVisualizationService visualizationService,
        IMultiModelReasoningService multiModelService,
        ILogger logger)
    {
        _reasoningService = reasoningService;
        _visualizationService = visualizationService;
        _multiModelService = multiModelService;
        _logger = logger;
    }

    public async Task<object> HandleEngineeringAnalyze(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>();
        var project = arguments?["project"]?.GetValue<string>();

        _logger.LogInformation("Engineering analysis requested. MemoryId: {MemoryId}, Project: {Project}",
            memoryIdStr, project);

        EngineeringAnalysisResult result;

        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var memoryId))
        {
            result = await _reasoningService.AnalyzeMemoryAsync(memoryId);
        }
        else
        {
            result = await _reasoningService.AnalyzeAsync(project);
        }

        var text = FormatAnalysisResult(result);
        return CreateTextResponse(text);
    }

    private static string FormatAnalysisResult(EngineeringAnalysisResult result)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Engineering Analysis Report");
        sb.AppendLine();
        sb.AppendLine($"**Analyzed:** {result.EntitiesAnalyzed} entities, {result.RelationshipsAnalyzed} relationships");
        sb.AppendLine($"**Time:** {result.AnalyzedAt:O}");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"| Severity | Count |");
        sb.AppendLine($"|----------|-------|");
        sb.AppendLine($"| **RISK** | {result.RiskCount} |");
        sb.AppendLine($"| **CONFLICT** | {result.ConflictCount} |");
        sb.AppendLine($"| **WARNING** | {result.WarningCount} |");
        sb.AppendLine($"| INFO | {result.InfoCount} |");
        sb.AppendLine();

        if (result.Insights.Count == 0)
        {
            sb.AppendLine("No engineering issues detected.");
            return sb.ToString();
        }

        // Group by category
        var byCategory = result.Insights
            .GroupBy(i => i.Category)
            .OrderByDescending(g => g.Max(i => (int)i.Severity))
            .ToList();

        foreach (var category in byCategory)
        {
            sb.AppendLine($"## {FormatCategory(category.Key)}");
            sb.AppendLine();

            foreach (var insight in category.OrderByDescending(i => i.Severity))
            {
                var severityIcon = insight.Severity switch
                {
                    InsightSeverity.Risk => "[RISK]",
                    InsightSeverity.Conflict => "[CONFLICT]",
                    InsightSeverity.Warning => "[WARNING]",
                    _ => "[INFO]"
                };

                sb.AppendLine($"### {severityIcon} {insight.Title}");
                sb.AppendLine();
                sb.AppendLine(insight.Description);
                sb.AppendLine();

                if (insight.InvolvedEntities.Count > 0)
                {
                    sb.AppendLine("**Entities:**");
                    foreach (var entity in insight.InvolvedEntities)
                    {
                        var role = string.IsNullOrEmpty(entity.Role) ? "" : $" ({entity.Role})";
                        sb.AppendLine($"- {entity.Name} [{entity.EntityType}]{role}");
                    }
                    sb.AppendLine();
                }

                if (insight.Recommendations.Count > 0)
                {
                    sb.AppendLine("**Recommendations:**");
                    foreach (var rec in insight.Recommendations)
                    {
                        sb.AppendLine($"- {rec}");
                    }
                    sb.AppendLine();
                }

                if (insight.Details.Count > 0)
                {
                    sb.AppendLine("**Details:**");
                    foreach (var (key, value) in insight.Details)
                    {
                        sb.AppendLine($"- {FormatKey(key)}: {value}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine($"*Confidence: {insight.Confidence:P0}*");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string FormatCategory(InsightCategory category)
    {
        return category switch
        {
            InsightCategory.PowerIntegrity => "Power Integrity",
            InsightCategory.SignalIntegrity => "Signal Integrity",
            InsightCategory.DependencyCorruption => "Dependency Corruption",
            InsightCategory.ThermalRisk => "Thermal Risk",
            InsightCategory.ProtocolMismatch => "Protocol Mismatch",
            InsightCategory.ComponentCompatibility => "Component Compatibility",
            _ => category.ToString()
        };
    }

    private static string FormatKey(string key)
    {
        // Convert snake_case to Title Case
        return string.Join(" ", key.Split('_')
            .Select(word => char.ToUpper(word[0]) + word[1..]));
    }

    public async Task<object> HandleEngineeringVisualize(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>();
        var project = arguments?["project"]?.GetValue<string>();
        var modeStr = arguments?["mode"]?.GetValue<string>() ?? "mixed";
        var includeOverlays = arguments?["include_overlays"]?.GetValue<bool>() ?? true;

        var mode = modeStr.ToLowerInvariant() switch
        {
            "software" => VisualizationMode.Software,
            "hardware" => VisualizationMode.Hardware,
            _ => VisualizationMode.Mixed
        };

        _logger.LogInformation("Visualization requested. MemoryId: {MemoryId}, Project: {Project}, Mode: {Mode}",
            memoryIdStr, project, mode);

        GraphVisualizationResult result;

        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var memoryId))
        {
            result = await _visualizationService.GenerateMemoryVisualizationAsync(memoryId, mode, includeOverlays);
        }
        else
        {
            result = await _visualizationService.GenerateVisualizationAsync(mode, project, includeOverlays);
        }

        // Return JSON for react-force-graph consumption
        var jsonResult = new
        {
            nodes = result.Nodes.Select(n => new
            {
                id = n.Id.ToString(),
                label = n.Label,
                type = n.Type,
                category = n.Category
            }),
            links = result.Links.Select(l => new
            {
                source = l.Source.ToString(),
                target = l.Target.ToString(),
                type = l.Type,
                category = l.Category,
                weight = l.Weight
            }),
            overlays = result.Overlays.Select(o => new
            {
                entityId = o.EntityId.ToString(),
                type = o.Type,
                message = o.Message,
                confidence = o.Confidence
            }),
            mode = result.Mode.ToString().ToLowerInvariant(),
            generatedAt = result.GeneratedAt.ToString("O"),
            summary = new
            {
                totalNodes = result.TotalNodes,
                totalLinks = result.TotalLinks,
                totalOverlays = result.TotalOverlays
            }
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        return CreateJsonResponse(JsonSerializer.Serialize(jsonResult, jsonOptions));
    }

    private static object CreateJsonResponse(string json)
    {
        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = json
                }
            }
        };
    }

    private static object CreateTextResponse(string text)
    {
        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text
                }
            }
        };
    }

    public async Task<object> HandleEngineeringReason(JsonNode? arguments)
    {
        var memoryIdStr = arguments?["memory_id"]?.GetValue<string>();
        var project = arguments?["project"]?.GetValue<string>();
        var maxDurationMs = arguments?["max_duration_ms"]?.GetValue<int>() ?? 30000;

        _logger.LogInformation("Multi-model reasoning requested. MemoryId: {MemoryId}, Project: {Project}, MaxDuration: {MaxDuration}ms",
            memoryIdStr, project, maxDurationMs);

        MultiModelReasoningResult result;

        if (!string.IsNullOrEmpty(memoryIdStr) && Guid.TryParse(memoryIdStr, out var memoryId))
        {
            result = await _multiModelService.ReasonMemoryAsync(memoryId, maxDurationMs);
        }
        else
        {
            result = await _multiModelService.ReasonAsync(project, maxDurationMs);
        }

        var text = FormatMultiModelResult(result);
        return CreateTextResponse(text);
    }

    private static string FormatMultiModelResult(MultiModelReasoningResult result)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Multi-Model Reasoning Report");
        sb.AppendLine();
        sb.AppendLine("## Execution Summary");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Total Duration | {result.TotalDurationMs}ms |");
        sb.AppendLine($"| Models Used | {result.ModelsUsed} |");
        sb.AppendLine($"| Successful | {result.SuccessfulModels} |");
        sb.AppendLine($"| Input Hash | `{result.InputHash}` |");
        sb.AppendLine($"| Reasoned At | {result.ReasonedAt:O} |");
        sb.AppendLine();

        // Model trace
        sb.AppendLine("## Model Execution Trace");
        sb.AppendLine();
        sb.AppendLine("| Model | Role | Duration | Status |");
        sb.AppendLine("|-------|------|----------|--------|");

        foreach (var trace in result.Trace.OrderBy(t => t.DurationMs))
        {
            var status = trace.Success ? "OK" : $"FAILED: {trace.Error}";
            sb.AppendLine($"| {trace.Model} v{trace.Version} | {trace.Role} | {trace.DurationMs}ms | {status} |");
        }
        sb.AppendLine();

        if (result.Insights.Count == 0)
        {
            sb.AppendLine("## Insights");
            sb.AppendLine();
            sb.AppendLine("No insights generated from multi-model reasoning.");
            return sb.ToString();
        }

        // Merged insights
        sb.AppendLine("## Merged Insights");
        sb.AppendLine();
        sb.AppendLine($"*{result.Insights.Count} insights from {result.SuccessfulModels} models*");
        sb.AppendLine();

        // Group by type
        var byType = result.Insights
            .GroupBy(i => i.Type)
            .OrderByDescending(g => g.Key switch
            {
                MultiModelInsightType.Risk => 4,
                MultiModelInsightType.Conflict => 3,
                MultiModelInsightType.Optimization => 2,
                _ => 1
            })
            .ToList();

        foreach (var typeGroup in byType)
        {
            var icon = typeGroup.Key switch
            {
                MultiModelInsightType.Risk => "[RISK]",
                MultiModelInsightType.Conflict => "[CONFLICT]",
                MultiModelInsightType.Optimization => "[OPT]",
                _ => "[INFO]"
            };

            sb.AppendLine($"### {icon} {typeGroup.Key}");
            sb.AppendLine();

            foreach (var insight in typeGroup.OrderByDescending(i => i.Confidence))
            {
                sb.AppendLine($"#### {insight.Message}");
                sb.AppendLine();
                sb.AppendLine($"- **Confidence:** {insight.Confidence:P0}");
                sb.AppendLine($"- **Agreement:** {insight.AgreementCount} model(s)");
                sb.AppendLine($"- **Sources:** {string.Join(", ", insight.SourceModels)}");

                if (insight.AffectedEntities is { Count: > 0 })
                {
                    sb.AppendLine($"- **Affected Entities:** {insight.AffectedEntities.Count}");
                }
                sb.AppendLine();
            }
        }

        // Confidence distribution
        sb.AppendLine("## Confidence Distribution");
        sb.AppendLine();
        var highConf = result.Insights.Count(i => i.Confidence >= 0.8f);
        var medConf = result.Insights.Count(i => i.Confidence >= 0.5f && i.Confidence < 0.8f);
        var lowConf = result.Insights.Count(i => i.Confidence < 0.5f);
        sb.AppendLine($"- High (>=80%): {highConf}");
        sb.AppendLine($"- Medium (50-79%): {medConf}");
        sb.AppendLine($"- Low (<50%): {lowConf}");
        sb.AppendLine();

        // Agreement summary
        sb.AppendLine("## Model Agreement");
        sb.AppendLine();
        var multiModelAgreement = result.Insights.Count(i => i.AgreementCount > 1);
        var singleModel = result.Insights.Count(i => i.AgreementCount == 1);
        sb.AppendLine($"- Multi-model agreement: {multiModelAgreement} insights");
        sb.AppendLine($"- Single model only: {singleModel} insights");

        return sb.ToString();
    }
}
