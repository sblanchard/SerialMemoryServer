using SerialMemory.Mcp.Tools;
using Xunit;

namespace SerialMemory.Mcp.Tests;

public class ToolDefinitionsTests
{
    [Fact]
    public void GetLifecycleTools_ReturnsSevenTools()
    {
        var tools = ToolDefinitions.GetLifecycleTools();
        Assert.Equal(7, tools.Length);
    }

    [Fact]
    public void GetLifecycleTools_ContainsExpectedToolNames()
    {
        var tools = ToolDefinitions.GetLifecycleTools();
        var toolNames = tools.Select(t => ((dynamic)t).name as string).ToList();

        Assert.Contains("memory_update", toolNames);
        Assert.Contains("memory_delete", toolNames);
        Assert.Contains("memory_merge", toolNames);
        Assert.Contains("memory_split", toolNames);
        Assert.Contains("memory_decay", toolNames);
        Assert.Contains("memory_reinforce", toolNames);
        Assert.Contains("memory_expire", toolNames);
    }

    [Fact]
    public void GetObservabilityTools_ReturnsFourTools()
    {
        var tools = ToolDefinitions.GetObservabilityTools();
        Assert.Equal(4, tools.Length);
    }

    [Fact]
    public void GetObservabilityTools_ContainsExpectedToolNames()
    {
        var tools = ToolDefinitions.GetObservabilityTools();
        var toolNames = tools.Select(t => ((dynamic)t).name as string).ToList();

        Assert.Contains("memory_trace", toolNames);
        Assert.Contains("memory_lineage", toolNames);
        Assert.Contains("memory_explain", toolNames);
        Assert.Contains("memory_conflicts", toolNames);
    }

    [Fact]
    public void GetSafetyTools_ReturnsFourTools()
    {
        var tools = ToolDefinitions.GetSafetyTools();
        Assert.Equal(4, tools.Length);
    }

    [Fact]
    public void GetSafetyTools_ContainsExpectedToolNames()
    {
        var tools = ToolDefinitions.GetSafetyTools();
        var toolNames = tools.Select(t => ((dynamic)t).name as string).ToList();

        Assert.Contains("detect_contradictions", toolNames);
        Assert.Contains("detect_hallucinations", toolNames);
        Assert.Contains("verify_memory_integrity", toolNames);
        Assert.Contains("scan_loops", toolNames);
    }

    [Fact]
    public void GetExportTools_ReturnsFourTools()
    {
        var tools = ToolDefinitions.GetExportTools();
        Assert.Equal(4, tools.Length);
    }

    [Fact]
    public void GetExportTools_ContainsExpectedToolNames()
    {
        var tools = ToolDefinitions.GetExportTools();
        var toolNames = tools.Select(t => ((dynamic)t).name as string).ToList();

        Assert.Contains("export_workspace", toolNames);
        Assert.Contains("export_memories", toolNames);
        Assert.Contains("export_graph", toolNames);
        Assert.Contains("export_user_profile", toolNames);
    }

    [Fact]
    public void AllTools_HaveRequiredProperties()
    {
        var allTools = ToolDefinitions.GetLifecycleTools()
            .Concat(ToolDefinitions.GetObservabilityTools())
            .Concat(ToolDefinitions.GetSafetyTools())
            .Concat(ToolDefinitions.GetExportTools());

        foreach (dynamic tool in allTools)
        {
            Assert.NotNull(tool.name);
            Assert.NotNull(tool.description);
            Assert.NotNull(tool.inputSchema);
            Assert.Equal("object", (string)tool.inputSchema.type);
        }
    }

    [Fact]
    public void TotalToolCount_IsNineteen()
    {
        var totalTools = ToolDefinitions.GetLifecycleTools().Length
            + ToolDefinitions.GetObservabilityTools().Length
            + ToolDefinitions.GetSafetyTools().Length
            + ToolDefinitions.GetExportTools().Length;

        Assert.Equal(19, totalTools);
    }
}
