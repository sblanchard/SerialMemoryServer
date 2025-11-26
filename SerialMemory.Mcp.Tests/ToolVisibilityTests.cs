using System.Text.Json;
using Xunit;
using SerialMemory.Mcp.Shared;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Tests for MCP tool visibility and context size validation.
/// </summary>
public class ToolVisibilityTests
{
    [Fact]
    public void CoreTools_ShouldReturn12Tools()
    {
        var coreTools = ToolDefinitions.GetCoreTools();
        Assert.Equal(12, coreTools.Length);
    }

    [Fact]
    public void AdminTools_ShouldReturn20Tools()
    {
        var adminTools = ToolDefinitions.GetAdminTools();
        Assert.Equal(20, adminTools.Length);
    }

    [Fact]
    public void CoreTools_ShouldContainEssentialTools()
    {
        var coreTools = ToolDefinitions.GetCoreTools();
        var toolNames = coreTools.Select(GetToolName).ToHashSet();

        Assert.Contains("memory_search", toolNames);
        Assert.Contains("memory_ingest", toolNames);
        Assert.Contains("memory_about_user", toolNames);
        Assert.Contains("memory_update", toolNames);
        Assert.Contains("memory_delete", toolNames);
        Assert.Contains("initialise_conversation_session", toolNames);
        Assert.Contains("end_conversation_session", toolNames);
    }

    [Fact]
    public void AdminTools_ShouldContainMaintenanceTools()
    {
        var adminTools = ToolDefinitions.GetAdminTools();
        var toolNames = adminTools.Select(GetToolName).ToHashSet();

        Assert.Contains("memory_multi_hop_search", toolNames);
        Assert.Contains("detect_contradictions", toolNames);
        Assert.Contains("detect_hallucinations", toolNames);
        Assert.Contains("verify_memory_integrity", toolNames);
        Assert.Contains("export_workspace", toolNames);
        Assert.Contains("reembed_memories", toolNames);
    }

    [Fact]
    public void CoreTools_ShouldNotContainAdminTools()
    {
        var coreTools = ToolDefinitions.GetCoreTools();
        var toolNames = coreTools.Select(GetToolName).ToHashSet();

        // Admin-only tools should not be in core
        Assert.DoesNotContain("export_workspace", toolNames);
        Assert.DoesNotContain("detect_contradictions", toolNames);
        Assert.DoesNotContain("reembed_memories", toolNames);
        Assert.DoesNotContain("memory_trace", toolNames);
        Assert.DoesNotContain("scan_loops", toolNames);
    }

    [Fact]
    public void ToolDescriptions_ShouldBeConcise()
    {
        var allTools = ToolDefinitions.GetCoreTools().Concat(ToolDefinitions.GetAdminTools());

        foreach (var tool in allTools)
        {
            var desc = GetToolDescription(tool);
            var tokenEstimate = EstimateTokens(desc);

            // Each description should be under 50 tokens (leaving room for schema)
            Assert.True(tokenEstimate < 50, $"Tool {GetToolName(tool)} description too long: ~{tokenEstimate} tokens");
        }
    }

    private static string GetToolName(object tool)
    {
        var json = JsonSerializer.Serialize(tool);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("name").GetString() ?? "";
    }

    private static string GetToolDescription(object tool)
    {
        var json = JsonSerializer.Serialize(tool);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("description").GetString() ?? "";
    }

    private static int EstimateTokens(string text)
    {
        // Rough estimate: ~4 characters per token
        return text.Length / 4;
    }
}
