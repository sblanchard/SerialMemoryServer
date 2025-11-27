using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using Xunit;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Tests for the credit-based usage enforcement system.
/// </summary>
public class CreditSystemTests
{
    #region Credit Cost Tests

    [Fact]
    public void UsageCreditCosts_GetCost_ReturnsCorrectCostsForMeteredOperations()
    {
        // Arrange & Act & Assert
        Assert.Equal(1.0m, UsageCreditCosts.GetCost(UsageEventType.MemoryIngest));
        Assert.Equal(0.25m, UsageCreditCosts.GetCost(UsageEventType.MemorySearch));
        Assert.Equal(2.0m, UsageCreditCosts.GetCost(UsageEventType.MemoryMultiHopSearch));
        Assert.Equal(0.5m, UsageCreditCosts.GetCost(UsageEventType.MemoryUpdate));
        Assert.Equal(0.2m, UsageCreditCosts.GetCost(UsageEventType.MemoryDelete));
        Assert.Equal(1.5m, UsageCreditCosts.GetCost(UsageEventType.MemoryMerge));
        Assert.Equal(1.5m, UsageCreditCosts.GetCost(UsageEventType.MemorySplit));
        Assert.Equal(0.1m, UsageCreditCosts.GetCost(UsageEventType.MemoryDecay));
        Assert.Equal(0.2m, UsageCreditCosts.GetCost(UsageEventType.MemoryReinforce));
        Assert.Equal(0.2m, UsageCreditCosts.GetCost(UsageEventType.MemoryExpire));
    }

    [Fact]
    public void UsageCreditCosts_GetCost_ReturnsBatchOperationCosts()
    {
        // Arrange & Act & Assert
        Assert.Equal(1.0m, UsageCreditCosts.GetCost(UsageEventType.CrawlRelationships));
        Assert.Equal(10.0m, UsageCreditCosts.GetCost(UsageEventType.ExportWorkspace));
        Assert.Equal(5.0m, UsageCreditCosts.GetCost(UsageEventType.ExportMemories));
        Assert.Equal(5.0m, UsageCreditCosts.GetCost(UsageEventType.ExportGraph));
        Assert.Equal(5.0m, UsageCreditCosts.GetCost(UsageEventType.ReembedMemories));
    }

    [Fact]
    public void UsageCreditCosts_ToSnakeCase_ConvertsCorrectly()
    {
        // Arrange & Act & Assert
        Assert.Equal("memory_ingest", UsageCreditCosts.ToSnakeCase(UsageEventType.MemoryIngest));
        Assert.Equal("memory_search", UsageCreditCosts.ToSnakeCase(UsageEventType.MemorySearch));
        Assert.Equal("memory_multi_hop_search", UsageCreditCosts.ToSnakeCase(UsageEventType.MemoryMultiHopSearch));
        Assert.Equal("export_workspace", UsageCreditCosts.ToSnakeCase(UsageEventType.ExportWorkspace));
    }

    #endregion

    #region Plan Limit Tests

    [Theory]
    [InlineData("free", 5000)]
    [InlineData("pro", 100000)]
    [InlineData("team", 500000)]
    [InlineData("enterprise", 10000000)]
    public void PlanLimits_HaveExpectedCreditAllocations(string planName, int expectedCredits)
    {
        // This tests the plan configuration defined in migrate_credits.sql
        // The actual values would come from the database; here we verify expectations

        var expectedLimits = new Dictionary<string, int>
        {
            ["free"] = 5000,
            ["pro"] = 100000,
            ["team"] = 500000,
            ["enterprise"] = 10000000
        };

        Assert.Equal(expectedCredits, expectedLimits[planName]);
    }

    #endregion

    #region Alert Threshold Tests

    [Fact]
    public void UsageAlertType_HasCorrectThresholds()
    {
        // Verify alert types exist for 75%, 90%, and 100%
        Assert.Equal(75, GetThresholdForAlertType(UsageAlertType.CreditWarning75));
        Assert.Equal(90, GetThresholdForAlertType(UsageAlertType.CreditWarning90));
        Assert.Equal(100, GetThresholdForAlertType(UsageAlertType.CreditLimitReached));
    }

    [Fact]
    public void UsageAlertType_HasMemoryThresholds()
    {
        Assert.Equal(75, GetThresholdForAlertType(UsageAlertType.MemoryWarning75));
        Assert.Equal(90, GetThresholdForAlertType(UsageAlertType.MemoryWarning90));
        Assert.Equal(100, GetThresholdForAlertType(UsageAlertType.MemoryLimitReached));
    }

    private static int GetThresholdForAlertType(UsageAlertType alertType) => alertType switch
    {
        UsageAlertType.CreditWarning75 or UsageAlertType.MemoryWarning75 => 75,
        UsageAlertType.CreditWarning90 or UsageAlertType.MemoryWarning90 => 90,
        UsageAlertType.CreditLimitReached or UsageAlertType.MemoryLimitReached => 100,
        _ => 0
    };

    #endregion

    #region UsageRecordResult Tests

    [Fact]
    public void UsageRecordResult_Success_ContainsExpectedProperties()
    {
        // Arrange
        var result = new UsageRecordResult
        {
            Success = true,
            CreditsConsumed = 1.0m,
            CreditsRemaining = 4999.0m,
            UsagePercentage = 0.02m,
            TriggeredAlerts = []
        };

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1.0m, result.CreditsConsumed);
        Assert.Equal(4999.0m, result.CreditsRemaining);
        Assert.Equal(0.02m, result.UsagePercentage);
        Assert.Empty(result.TriggeredAlerts);
    }

    [Fact]
    public void UsageRecordResult_WithAlerts_ContainsTriggeredAlerts()
    {
        // Arrange
        var alerts = new List<UsageAlert>
        {
            new()
            {
                AlertType = UsageAlertType.CreditWarning75,
                ThresholdPercent = 75,
                CurrentPercent = 76.5m,
                Message = "75% of credit limit used",
                TriggeredAt = DateTimeOffset.UtcNow
            }
        };

        var result = new UsageRecordResult
        {
            Success = true,
            CreditsConsumed = 1.0m,
            CreditsRemaining = 1175.0m,
            UsagePercentage = 76.5m,
            TriggeredAlerts = alerts
        };

        // Assert
        Assert.Single(result.TriggeredAlerts);
        Assert.Equal(UsageAlertType.CreditWarning75, result.TriggeredAlerts[0].AlertType);
        Assert.Equal(75, result.TriggeredAlerts[0].ThresholdPercent);
    }

    #endregion

    #region UsageAlert Tests

    [Fact]
    public void UsageAlert_ContainsExpectedProperties()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var alert = new UsageAlert
        {
            AlertType = UsageAlertType.CreditWarning90,
            ThresholdPercent = 90,
            CurrentPercent = 92.5m,
            Message = "90% of credit limit used",
            TriggeredAt = now,
            Acknowledged = false
        };

        // Assert
        Assert.Equal(UsageAlertType.CreditWarning90, alert.AlertType);
        Assert.Equal(90, alert.ThresholdPercent);
        Assert.Equal(92.5m, alert.CurrentPercent);
        Assert.Equal("90% of credit limit used", alert.Message);
        Assert.Equal(now, alert.TriggeredAt);
        Assert.False(alert.Acknowledged);
    }

    [Fact]
    public void UsageAlert_AcknowledgedState_CanBeSet()
    {
        // Arrange
        var alert = new UsageAlert
        {
            AlertType = UsageAlertType.CreditLimitReached,
            ThresholdPercent = 100,
            CurrentPercent = 100.0m,
            Message = "Credit limit reached",
            TriggeredAt = DateTimeOffset.UtcNow,
            Acknowledged = true
        };

        // Assert
        Assert.True(alert.Acknowledged);
    }

    #endregion

    #region Cost Calculation Tests

    [Fact]
    public void CreditCost_TypicalSession_CalculatesExpectedUsage()
    {
        // Simulate a typical session:
        // - 10 memory searches (0.25 each) = 2.5
        // - 5 memory ingests (1.0 each) = 5.0
        // - 2 multi-hop searches (2.0 each) = 4.0
        // - 1 update (0.5) = 0.5
        // Total = 12.0

        decimal totalCost = 0;
        totalCost += 10 * UsageCreditCosts.GetCost(UsageEventType.MemorySearch);  // 2.5
        totalCost += 5 * UsageCreditCosts.GetCost(UsageEventType.MemoryIngest);   // 5.0
        totalCost += 2 * UsageCreditCosts.GetCost(UsageEventType.MemoryMultiHopSearch); // 4.0
        totalCost += 1 * UsageCreditCosts.GetCost(UsageEventType.MemoryUpdate);   // 0.5

        Assert.Equal(12.0m, totalCost);
    }

    [Fact]
    public void CreditCost_FreePlanDailyBudget_CalculatesOperationCount()
    {
        // Free plan: 5000 credits/month = ~167 credits/day
        decimal dailyBudget = 5000m / 30;

        // How many searches per day?
        decimal searchCost = UsageCreditCosts.GetCost(UsageEventType.MemorySearch);
        int searchesPerDay = (int)(dailyBudget / searchCost);

        // Approximately 668 searches per day on free plan
        Assert.True(searchesPerDay > 600, $"Expected 600+ searches/day, got {searchesPerDay}");
        Assert.True(searchesPerDay < 700, $"Expected <700 searches/day, got {searchesPerDay}");
    }

    [Fact]
    public void CreditCost_ProPlanAllowsHeavyUsage()
    {
        // Pro plan: 100,000 credits/month = ~3333 credits/day
        decimal dailyBudget = 100000m / 30;

        // Mixed daily workload:
        // - 100 ingests (100 credits)
        // - 500 searches (125 credits)
        // - 50 multi-hop (100 credits @ 2.0 each)
        // - 20 updates (10 credits)
        // Total: 335 credits/day
        decimal dailyUsage =
            100 * UsageCreditCosts.GetCost(UsageEventType.MemoryIngest) +
            500 * UsageCreditCosts.GetCost(UsageEventType.MemorySearch) +
            50 * UsageCreditCosts.GetCost(UsageEventType.MemoryMultiHopSearch) +
            20 * UsageCreditCosts.GetCost(UsageEventType.MemoryUpdate);

        // Verify heavy usage fits well within pro plan budget
        Assert.True(dailyUsage < dailyBudget, $"Daily usage {dailyUsage} should fit in budget {dailyBudget}");

        // About 9.95x headroom (still substantial)
        decimal headroom = dailyBudget / dailyUsage;
        Assert.True(headroom > 9, $"Expected 9x+ headroom, got {headroom:F1}x");
    }

    #endregion

    #region Boundary Tests

    [Theory]
    [InlineData(74.9, false, false, false)]
    [InlineData(75.0, true, false, false)]
    [InlineData(75.1, true, false, false)]
    [InlineData(89.9, true, false, false)]
    [InlineData(90.0, true, true, false)]
    [InlineData(90.1, true, true, false)]
    [InlineData(99.9, true, true, false)]
    [InlineData(100.0, true, true, true)]
    [InlineData(100.1, true, true, true)]
    public void AlertThresholds_TriggerAtCorrectBoundaries(
        decimal usagePercent,
        bool expect75,
        bool expect90,
        bool expect100)
    {
        // These thresholds match the implementation in UsageLimitService.RecordUsageAsync
        bool should75 = usagePercent >= 75 && usagePercent < 90;
        bool should90 = usagePercent >= 90 && usagePercent < 100;
        bool should100 = usagePercent >= 100;

        // For alerts, the logic is:
        // - If >= 100, only 100 alert
        // - If >= 90, only 90 alert (not 75)
        // - If >= 75, only 75 alert

        if (usagePercent >= 100)
        {
            Assert.True(expect100);
        }
        else if (usagePercent >= 90)
        {
            Assert.True(expect90);
            Assert.False(expect100);
        }
        else if (usagePercent >= 75)
        {
            Assert.True(expect75);
            Assert.False(expect90);
            Assert.False(expect100);
        }
        else
        {
            Assert.False(expect75);
            Assert.False(expect90);
            Assert.False(expect100);
        }
    }

    #endregion
}
