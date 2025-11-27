using SerialMemory.Core.Models;
using Xunit;

namespace SerialMemory.Mcp.Tests;

public class UsageModelsTests
{
    [Theory]
    [InlineData(UsageEventType.MemoryIngest, 1.0)]
    [InlineData(UsageEventType.MemorySearch, 0.25)]
    [InlineData(UsageEventType.MemoryMultiHopSearch, 2.0)]
    [InlineData(UsageEventType.MemoryUpdate, 0.5)]
    [InlineData(UsageEventType.MemoryDelete, 0.2)]
    [InlineData(UsageEventType.MemoryMerge, 1.5)]
    [InlineData(UsageEventType.MemorySplit, 1.5)]
    [InlineData(UsageEventType.MemoryDecay, 0.1)]
    [InlineData(UsageEventType.MemoryReinforce, 0.2)]
    [InlineData(UsageEventType.MemoryExpire, 0.2)]
    [InlineData(UsageEventType.CrawlRelationships, 1.0)]
    [InlineData(UsageEventType.ExportWorkspace, 10.0)]
    [InlineData(UsageEventType.ExportMemories, 5.0)]
    [InlineData(UsageEventType.ExportGraph, 5.0)]
    [InlineData(UsageEventType.ReembedMemories, 5.0)]
    public void GetCost_ReturnsCorrectCredits(UsageEventType eventType, decimal expectedCredits)
    {
        var cost = UsageCreditCosts.GetCost(eventType);
        Assert.Equal(expectedCredits, cost);
    }

    [Theory]
    [InlineData(UsageEventType.MemoryIngest, "memory_ingest")]
    [InlineData(UsageEventType.MemorySearch, "memory_search")]
    [InlineData(UsageEventType.MemoryMultiHopSearch, "memory_multi_hop_search")]
    [InlineData(UsageEventType.MemoryUpdate, "memory_update")]
    [InlineData(UsageEventType.MemoryDelete, "memory_delete")]
    [InlineData(UsageEventType.MemoryMerge, "memory_merge")]
    [InlineData(UsageEventType.MemorySplit, "memory_split")]
    [InlineData(UsageEventType.MemoryDecay, "memory_decay")]
    [InlineData(UsageEventType.MemoryReinforce, "memory_reinforce")]
    [InlineData(UsageEventType.MemoryExpire, "memory_expire")]
    [InlineData(UsageEventType.CrawlRelationships, "crawl_relationships")]
    [InlineData(UsageEventType.ExportWorkspace, "export_workspace")]
    [InlineData(UsageEventType.ExportMemories, "export_memories")]
    [InlineData(UsageEventType.ExportGraph, "export_graph")]
    [InlineData(UsageEventType.ReembedMemories, "reembed_memories")]
    public void ToSnakeCase_ReturnsCorrectFormat(UsageEventType eventType, string expected)
    {
        var snakeCase = UsageCreditCosts.ToSnakeCase(eventType);
        Assert.Equal(expected, snakeCase);
    }

    [Fact]
    public void UsageEvent_DefaultValues_AreCorrect()
    {
        var evt = new UsageEvent();

        Assert.Equal("self", evt.TenantId);
        Assert.Equal("default", evt.WorkspaceId);
        Assert.True(evt.Success);
        Assert.Null(evt.ErrorMessage);
        Assert.Null(evt.MemoryId);
        Assert.Null(evt.SessionId);
    }

    [Fact]
    public void UsageEvent_CanBePopulated()
    {
        var id = Guid.CreateVersion7();
        var memoryId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var timestamp = DateTimeOffset.UtcNow;

        var evt = new UsageEvent
        {
            Id = id,
            TenantId = "test-tenant",
            WorkspaceId = "test-workspace",
            EventType = UsageEventType.MemorySearch,
            CreditsConsumed = 0.25m,
            EventTimestamp = timestamp,
            MemoryId = memoryId,
            UserId = "user123",
            SessionId = sessionId,
            LatencyMs = 150,
            Success = true,
            Metadata = new Dictionary<string, object> { ["key"] = "value" }
        };

        Assert.Equal(id, evt.Id);
        Assert.Equal("test-tenant", evt.TenantId);
        Assert.Equal("test-workspace", evt.WorkspaceId);
        Assert.Equal(UsageEventType.MemorySearch, evt.EventType);
        Assert.Equal(0.25m, evt.CreditsConsumed);
        Assert.Equal(timestamp, evt.EventTimestamp);
        Assert.Equal(memoryId, evt.MemoryId);
        Assert.Equal("user123", evt.UserId);
        Assert.Equal(sessionId, evt.SessionId);
        Assert.Equal(150, evt.LatencyMs);
        Assert.True(evt.Success);
        Assert.NotNull(evt.Metadata);
    }

    [Fact]
    public void BillingCycle_CreditsRemaining_IsComputed()
    {
        var cycle = new BillingCycle
        {
            CreditsAllocated = 1000m,
            CreditsUsed = 250m
        };

        Assert.Equal(750m, cycle.CreditsRemaining);
    }

    [Fact]
    public void TenantPlan_DefaultValues_AreCorrect()
    {
        var plan = new TenantPlan();

        Assert.Equal("self", plan.PlanName);
        Assert.Equal("Self-Hosted", plan.DisplayName);
        Assert.Equal(999999999m, plan.CreditsPerCycle);
        Assert.Equal(30, plan.CycleDays);
        Assert.True(plan.IsActive);
        Assert.Null(plan.MaxMemories);
        Assert.Null(plan.MaxEntities);
    }

    [Fact]
    public void UsageDailyRollup_CanTrackStatistics()
    {
        var rollup = new UsageDailyRollup
        {
            TenantId = "self",
            WorkspaceId = "default",
            RollupDate = DateOnly.FromDateTime(DateTime.UtcNow),
            EventType = UsageEventType.MemorySearch,
            EventCount = 100,
            TotalCredits = 25m,
            SuccessCount = 95,
            FailureCount = 5,
            AvgLatencyMs = 150.5m,
            P95LatencyMs = 300,
            P99LatencyMs = 500
        };

        Assert.Equal(100, rollup.EventCount);
        Assert.Equal(25m, rollup.TotalCredits);
        Assert.Equal(95, rollup.SuccessCount);
        Assert.Equal(5, rollup.FailureCount);
    }

    [Fact]
    public void AllEventTypes_HaveCreditCosts()
    {
        foreach (UsageEventType eventType in Enum.GetValues<UsageEventType>())
        {
            var cost = UsageCreditCosts.GetCost(eventType);
            Assert.True(cost > 0, $"Event type {eventType} should have a positive credit cost");
        }
    }

    [Fact]
    public void AllEventTypes_HaveSnakeCaseMapping()
    {
        foreach (UsageEventType eventType in Enum.GetValues<UsageEventType>())
        {
            var snakeCase = UsageCreditCosts.ToSnakeCase(eventType);
            Assert.NotNull(snakeCase);
            Assert.NotEmpty(snakeCase);
            Assert.Contains("_", snakeCase);
        }
    }
}
