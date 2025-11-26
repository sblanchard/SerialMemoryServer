using Xunit;
using SerialMemory.Mcp.Shared;
using Microsoft.Extensions.Configuration;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Tests for admin tool gating behavior.
/// </summary>
public class AdminGatingTests
{
    [Fact]
    public void IsAdminEnabled_ReturnsFalse_ByDefault()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.False(AdminGating.IsAdminEnabled(config));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void IsAdminEnabled_RespectsEnvironmentVariable(string value, bool expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SERIALMEMORY_ADMIN_ENABLED"] = value
            })
            .Build();

        Assert.Equal(expected, AdminGating.IsAdminEnabled(config));
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("Admin", true)]
    [InlineData("ADMIN", true)]
    [InlineData("maintenance", true)]
    [InlineData("user", false)]
    [InlineData("readonly", false)]
    public void IsAdminEnabled_RespectsRoleVariable(string role, bool expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SERIALMEMORY_ROLE"] = role
            })
            .Build();

        Assert.Equal(expected, AdminGating.IsAdminEnabled(config));
    }

    [Theory]
    [InlineData("export my workspace", true)]
    [InlineData("Export the memories", true)]
    [InlineData("audit integrity of the database", true)]
    [InlineData("reembed all memories after model change", true)]
    [InlineData("crawl relationships", true)]
    [InlineData("detect contradictions in my knowledge base", true)]
    [InlineData("scan loops in causal graph", true)]
    [InlineData("show me model info", true)]
    [InlineData("import from core", true)]
    [InlineData("multi-hop search for entities", true)]
    public void ShouldActivateAdmin_ReturnsTrueForAdminKeywords(string message, bool expected)
    {
        Assert.Equal(expected, AdminGating.ShouldActivateAdmin(message));
    }

    [Theory]
    [InlineData("search for memories about project X")]
    [InlineData("remember that I prefer TypeScript")]
    [InlineData("what do you know about me?")]
    [InlineData("start a new session")]
    [InlineData("end session")]
    [InlineData("update memory content")]
    [InlineData("delete the wrong memory")]
    public void ShouldActivateAdmin_ReturnsFalseForCoreOperations(string message)
    {
        Assert.False(AdminGating.ShouldActivateAdmin(message));
    }

    [Fact]
    public void AdminTriggerKeywords_ContainsExpectedKeywords()
    {
        var keywords = AdminGating.AdminTriggerKeywords;

        Assert.Contains("export", keywords);
        Assert.Contains("audit", keywords);
        Assert.Contains("reembed", keywords);
        Assert.Contains("crawl", keywords);
        Assert.Contains("detect contradictions", keywords);
        Assert.Contains("scan loops", keywords);
        Assert.Contains("maintenance", keywords);
    }

    [Fact]
    public void AdminTriggerKeywords_ShouldNotOverlapWithCoreOperations()
    {
        var keywords = AdminGating.AdminTriggerKeywords;

        // These are core operations and should NOT trigger admin
        Assert.DoesNotContain("search", keywords);
        Assert.DoesNotContain("ingest", keywords);
        Assert.DoesNotContain("remember", keywords);
        Assert.DoesNotContain("session", keywords);
        Assert.DoesNotContain("persona", keywords);
    }
}
