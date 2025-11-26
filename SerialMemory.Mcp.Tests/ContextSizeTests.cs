using System.Text.Json;
using Xunit;
using SerialMemory.Mcp.Shared;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Tests validating context size reduction goals.
/// </summary>
public class ContextSizeTests
{
    private const int MaxCoreTokens = 3000;     // Target: ~2-3k tokens for core
    private const int MaxAdminTokens = 5000;    // Target: ~4-5k tokens for admin
    private const int MaxTokensPerTool = 150;   // Target: <150 tokens per tool

    [Fact]
    public void CoreTools_TotalTokens_ShouldBeUnderThreshold()
    {
        var coreTools = ToolDefinitions.GetCoreTools();
        var totalTokens = EstimateTotalTokens(coreTools);

        Assert.True(totalTokens < MaxCoreTokens,
            $"Core tools total context ({totalTokens} tokens) exceeds target ({MaxCoreTokens})");
    }

    [Fact]
    public void AdminTools_TotalTokens_ShouldBeUnderThreshold()
    {
        var adminTools = ToolDefinitions.GetAdminTools();
        var totalTokens = EstimateTotalTokens(adminTools);

        Assert.True(totalTokens < MaxAdminTokens,
            $"Admin tools total context ({totalTokens} tokens) exceeds target ({MaxAdminTokens})");
    }

    [Fact]
    public void EachCoreTool_ShouldBeUnder150Tokens()
    {
        var coreTools = ToolDefinitions.GetCoreTools();

        foreach (var tool in coreTools)
        {
            var tokens = EstimateToolTokens(tool);
            var name = GetToolName(tool);

            Assert.True(tokens < MaxTokensPerTool,
                $"Core tool '{name}' ({tokens} tokens) exceeds {MaxTokensPerTool} token limit");
        }
    }

    [Fact]
    public void EachAdminTool_ShouldBeUnder150Tokens()
    {
        var adminTools = ToolDefinitions.GetAdminTools();

        foreach (var tool in adminTools)
        {
            var tokens = EstimateToolTokens(tool);
            var name = GetToolName(tool);

            Assert.True(tokens < MaxTokensPerTool,
                $"Admin tool '{name}' ({tokens} tokens) exceeds {MaxTokensPerTool} token limit");
        }
    }

    [Fact]
    public void CoreOnly_ShouldReduceContextBy70Percent()
    {
        // Original had ~34 tools at ~21k tokens
        // Core should be ~12 tools at ~2-3k tokens (roughly 85-90% reduction)
        var coreTools = ToolDefinitions.GetCoreTools();
        var coreTokens = EstimateTotalTokens(coreTools);

        var estimatedOriginalTokens = 21000;
        var reductionPercent = (1.0 - (double)coreTokens / estimatedOriginalTokens) * 100;

        Assert.True(reductionPercent >= 70,
            $"Context reduction ({reductionPercent:F1}%) is less than 70% target");
    }

    [Fact]
    public void ToolSchemas_ShouldBeMinimal()
    {
        var allTools = ToolDefinitions.GetCoreTools().Concat(ToolDefinitions.GetAdminTools());

        foreach (var tool in allTools)
        {
            var json = JsonSerializer.Serialize(tool);
            using var doc = JsonDocument.Parse(json);

            var schema = doc.RootElement.GetProperty("inputSchema");
            var properties = schema.GetProperty("properties");
            var propertyCount = properties.EnumerateObject().Count();

            // No tool should have more than 8 parameters
            Assert.True(propertyCount <= 8,
                $"Tool '{GetToolName(tool)}' has {propertyCount} properties (max 8)");
        }
    }

    private static int EstimateTotalTokens(object[] tools)
    {
        return tools.Sum(EstimateToolTokens);
    }

    private static int EstimateToolTokens(object tool)
    {
        var json = JsonSerializer.Serialize(tool);
        // Rough estimate: ~3-4 characters per token for JSON
        return json.Length / 3;
    }

    private static string GetToolName(object tool)
    {
        var json = JsonSerializer.Serialize(tool);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("name").GetString() ?? "";
    }
}
