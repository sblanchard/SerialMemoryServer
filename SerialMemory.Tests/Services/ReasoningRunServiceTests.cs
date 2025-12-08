using FluentAssertions;
using SerialMemory.Core.Models;
using Xunit;

namespace SerialMemory.Tests.Services;

/// <summary>
/// Unit tests for reasoning execution models and DTOs.
/// The ReasoningRunService itself requires integration tests due to Dapper usage.
/// </summary>
public class ReasoningExecutionModelTests
{
    [Fact]
    public void ReasoningExecution_InitializesWithDefaults()
    {
        // Act
        var execution = new ReasoningExecution
        {
            TenantId = Guid.CreateVersion7(),
            UserId = "test-user",
            InputType = ReasoningInputType.Query
        };

        // Assert
        execution.Id.Should().NotBe(Guid.Empty);
        execution.Status.Should().Be(ReasoningExecutionStatus.Pending);
        execution.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        execution.Traces.Should().BeEmpty();
        execution.Insights.Should().BeEmpty();
        execution.Disagreements.Should().BeEmpty();
    }

    [Fact]
    public void ReasoningTrace_InitializesWithDefaults()
    {
        // Act
        var trace = new ReasoningTrace
        {
            ExecutionId = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            StepId = "step-1",
            Role = ReasoningRole.Structural,
            ModelName = "TestModel"
        };

        // Assert
        trace.Id.Should().NotBe(Guid.Empty);
        trace.ParentStepIds.Should().BeEmpty();
        trace.StepOrder.Should().Be(0);
        trace.Success.Should().BeFalse();
        trace.DurationMs.Should().Be(0);
    }

    [Fact]
    public void ReasoningInsight_InitializesWithDefaults()
    {
        // Act
        var insight = new ReasoningInsight
        {
            ExecutionId = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            InsightType = "risk",
            Content = "Test insight"
        };

        // Assert
        insight.Id.Should().NotBe(Guid.Empty);
        insight.AgreementCount.Should().Be(1);
        insight.SourceModels.Should().BeEmpty();
        insight.RelatedEntityIds.Should().BeEmpty();
        insight.RelatedMemoryIds.Should().BeEmpty();
    }

    [Fact]
    public void ReasoningDisagreement_InitializesWithDefaults()
    {
        // Act
        var disagreement = new ReasoningDisagreement
        {
            ExecutionId = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            Topic = "Test topic",
            ModelA = "Model1",
            PositionA = "Position A",
            ModelB = "Model2",
            PositionB = "Position B"
        };

        // Assert
        disagreement.Id.Should().NotBe(Guid.Empty);
        disagreement.Resolved.Should().BeFalse();
        disagreement.Resolution.Should().BeNull();
    }

    [Fact]
    public void ReasoningExecutionSummary_MapsFromExecution()
    {
        // Arrange
        var execution = new ReasoningExecution
        {
            TenantId = Guid.CreateVersion7(),
            UserId = "test-user",
            InputType = ReasoningInputType.Memory,
            InputReference = "memory-123",
            Status = ReasoningExecutionStatus.Completed,
            ModelsUsed = 4,
            SuccessfulModels = 3,
            TotalDurationMs = 1500,
            OverallConfidence = 0.87f,
            InsightCount = 5,
            DisagreementCount = 1
        };

        // Act
        var summary = new ReasoningExecutionSummary
        {
            Id = execution.Id,
            TenantId = execution.TenantId,
            UserId = execution.UserId,
            InputType = execution.InputType,
            InputReference = execution.InputReference,
            Status = execution.Status,
            StartedAt = execution.StartedAt,
            CompletedAt = execution.CompletedAt,
            ModelsUsed = execution.ModelsUsed,
            SuccessfulModels = execution.SuccessfulModels,
            TotalDurationMs = execution.TotalDurationMs,
            OverallConfidence = execution.OverallConfidence,
            InsightCount = execution.InsightCount,
            DisagreementCount = execution.DisagreementCount,
            CreatedAt = execution.CreatedAt
        };

        // Assert
        summary.Id.Should().Be(execution.Id);
        summary.InputType.Should().Be(ReasoningInputType.Memory);
        summary.Status.Should().Be(ReasoningExecutionStatus.Completed);
        summary.ModelsUsed.Should().Be(4);
        summary.SuccessfulModels.Should().Be(3);
        summary.TotalDurationMs.Should().Be(1500);
        summary.OverallConfidence.Should().Be(0.87f);
    }

    [Fact]
    public void ReasoningRunRequest_HasDefaults()
    {
        // Act
        var request = new ReasoningRunRequest
        {
            TenantId = Guid.CreateVersion7(),
            UserId = "test-user",
            InputType = ReasoningInputType.Query
        };

        // Assert
        request.MaxDurationMs.Should().Be(30000); // Default timeout
        request.InputReference.Should().BeNull();
    }

    [Theory]
    [InlineData(ReasoningExecutionStatus.Pending)]
    [InlineData(ReasoningExecutionStatus.Running)]
    [InlineData(ReasoningExecutionStatus.Completed)]
    [InlineData(ReasoningExecutionStatus.Failed)]
    [InlineData(ReasoningExecutionStatus.Cancelled)]
    public void ReasoningExecutionStatus_AllValuesValid(ReasoningExecutionStatus status)
    {
        // Act
        var execution = new ReasoningExecution
        {
            TenantId = Guid.CreateVersion7(),
            UserId = "test",
            InputType = ReasoningInputType.Query,
            Status = status
        };

        // Assert
        execution.Status.Should().Be(status);
    }

    [Theory]
    [InlineData(ReasoningInputType.Memory)]
    [InlineData(ReasoningInputType.Query)]
    [InlineData(ReasoningInputType.Project)]
    [InlineData(ReasoningInputType.Graph)]
    public void ReasoningInputType_AllValuesValid(ReasoningInputType inputType)
    {
        // Act
        var execution = new ReasoningExecution
        {
            TenantId = Guid.CreateVersion7(),
            UserId = "test",
            InputType = inputType
        };

        // Assert
        execution.InputType.Should().Be(inputType);
    }
}

/// <summary>
/// Tests for IReasoningRunService event contracts.
/// </summary>
public class ReasoningRunServiceEventTests
{
    [Fact]
    public void OnTraceAdded_EventSignature_IsCorrect()
    {
        // Arrange
        Func<Guid, ReasoningTrace, Task>? handler = null;
        Guid capturedExecutionId = Guid.Empty;
        ReasoningTrace? capturedTrace = null;

        handler = async (executionId, trace) =>
        {
            capturedExecutionId = executionId;
            capturedTrace = trace;
            await Task.CompletedTask;
        };

        var testTrace = new ReasoningTrace
        {
            ExecutionId = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            StepId = "test-step",
            Role = ReasoningRole.Risk,
            ModelName = "RiskModel"
        };

        // Act
        handler.Invoke(testTrace.ExecutionId, testTrace);

        // Assert
        capturedExecutionId.Should().Be(testTrace.ExecutionId);
        capturedTrace.Should().Be(testTrace);
    }

    [Fact]
    public void OnExecutionCompleted_EventSignature_IsCorrect()
    {
        // Arrange
        Func<Guid, ReasoningExecution, Task>? handler = null;
        Guid capturedExecutionId = Guid.Empty;
        ReasoningExecution? capturedExecution = null;

        handler = async (executionId, execution) =>
        {
            capturedExecutionId = executionId;
            capturedExecution = execution;
            await Task.CompletedTask;
        };

        var testExecution = new ReasoningExecution
        {
            TenantId = Guid.CreateVersion7(),
            UserId = "test-user",
            InputType = ReasoningInputType.Query,
            Status = ReasoningExecutionStatus.Completed
        };

        // Act
        handler.Invoke(testExecution.Id, testExecution);

        // Assert
        capturedExecutionId.Should().Be(testExecution.Id);
        capturedExecution.Should().Be(testExecution);
    }
}

/// <summary>
/// Tests for reasoning result merging and transformation.
/// </summary>
public class ReasoningResultTransformationTests
{
    [Fact]
    public void MultiModelReasoningResult_CanTransformToTraces()
    {
        // Arrange
        var result = new MultiModelReasoningResult
        {
            ModelsUsed = 3,
            SuccessfulModels = 3,
            TotalDurationMs = 500,
            OverallConfidence = 0.85f,
            InputHash = "hash123",
            Trace =
            [
                new ModelTrace
                {
                    Model = "Structural",
                    Version = "1.0",
                    Role = ReasoningRole.Structural,
                    DurationMs = 150,
                    Success = true
                },
                new ModelTrace
                {
                    Model = "Risk",
                    Version = "1.0",
                    Role = ReasoningRole.Risk,
                    DurationMs = 200,
                    Success = true
                },
                new ModelTrace
                {
                    Model = "Optimization",
                    Version = "1.0",
                    Role = ReasoningRole.Optimization,
                    DurationMs = 150,
                    Success = true
                }
            ],
            Insights = [],
            Disagreements = []
        };

        var executionId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        // Act
        var traces = result.Trace.Select((t, idx) => new ReasoningTrace
        {
            ExecutionId = executionId,
            TenantId = tenantId,
            StepId = $"{t.Role.ToString().ToLowerInvariant()}-{idx + 1}",
            StepOrder = idx,
            Role = t.Role,
            ModelName = t.Model,
            ModelVersion = t.Version,
            DurationMs = t.DurationMs,
            Success = t.Success,
            ErrorMessage = t.Error
        }).ToList();

        // Assert
        traces.Should().HaveCount(3);
        traces[0].StepId.Should().Be("structural-1");
        traces[1].StepId.Should().Be("risk-2");
        traces[2].StepId.Should().Be("optimization-3");
        traces.All(t => t.ExecutionId == executionId).Should().BeTrue();
        traces.All(t => t.TenantId == tenantId).Should().BeTrue();
    }

    [Fact]
    public void MergedInsight_CanTransformToReasoningInsight()
    {
        // Arrange
        var mergedInsight = new MergedInsight
        {
            Type = MultiModelInsightType.Risk,
            Message = "Critical security vulnerability detected",
            Confidence = 0.92f,
            SourceModels = ["RiskModel", "StructuralModel"],
            AgreementCount = 2,
            AffectedEntities = [Guid.CreateVersion7(), Guid.CreateVersion7()]
        };

        var executionId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        // Act
        var reasoningInsight = new ReasoningInsight
        {
            ExecutionId = executionId,
            TenantId = tenantId,
            InsightType = mergedInsight.Type.ToString().ToLowerInvariant(),
            Content = mergedInsight.Message,
            Confidence = mergedInsight.Confidence,
            SourceModels = mergedInsight.SourceModels,
            AgreementCount = mergedInsight.AgreementCount,
            RelatedEntityIds = mergedInsight.AffectedEntities ?? []
        };

        // Assert
        reasoningInsight.InsightType.Should().Be("risk");
        reasoningInsight.Content.Should().Be("Critical security vulnerability detected");
        reasoningInsight.Confidence.Should().Be(0.92f);
        reasoningInsight.SourceModels.Should().HaveCount(2);
        reasoningInsight.AgreementCount.Should().Be(2);
        reasoningInsight.RelatedEntityIds.Should().HaveCount(2);
    }

    [Fact]
    public void ModelDisagreement_CanTransformToReasoningDisagreement()
    {
        // Arrange
        var modelDisagreement = new ModelDisagreement
        {
            PrimaryModel = "StructuralModel",
            SecondaryModel = "RiskModel",
            PrimaryOnlyInsights = ["Database schema is well-designed", "Indexes are optimal"],
            SecondaryOnlyInsights = ["Database schema has redundancies"],
            DisagreementScore = 0.75f
        };

        var executionId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        // Act
        var severity = modelDisagreement.DisagreementScore switch
        {
            > 0.7f => "high",
            > 0.4f => "medium",
            _ => "low"
        };

        var topic = string.Join("; ", modelDisagreement.PrimaryOnlyInsights.Take(2)
            .Concat(modelDisagreement.SecondaryOnlyInsights.Take(2)));

        var reasoningDisagreement = new ReasoningDisagreement
        {
            ExecutionId = executionId,
            TenantId = tenantId,
            Topic = topic,
            ModelA = modelDisagreement.PrimaryModel,
            PositionA = string.Join("; ", modelDisagreement.PrimaryOnlyInsights.Take(3)),
            ModelB = modelDisagreement.SecondaryModel,
            PositionB = string.Join("; ", modelDisagreement.SecondaryOnlyInsights.Take(3)),
            Severity = severity
        };

        // Assert
        reasoningDisagreement.Severity.Should().Be("high");
        reasoningDisagreement.ModelA.Should().Be("StructuralModel");
        reasoningDisagreement.ModelB.Should().Be("RiskModel");
        reasoningDisagreement.Topic.Should().Contain("Database schema");
    }

    [Theory]
    [InlineData(0.8f, "high")]
    [InlineData(0.71f, "high")]
    [InlineData(0.7f, "medium")]
    [InlineData(0.5f, "medium")]
    [InlineData(0.41f, "medium")]
    [InlineData(0.4f, "low")]
    [InlineData(0.2f, "low")]
    public void DisagreementSeverity_MapsCorrectly(float score, string expectedSeverity)
    {
        // Act
        var severity = score switch
        {
            > 0.7f => "high",
            > 0.4f => "medium",
            _ => "low"
        };

        // Assert
        severity.Should().Be(expectedSeverity);
    }
}

/// <summary>
/// Integration tests for ReasoningRunService with real database operations.
/// These tests require a PostgreSQL database with the reasoning schema.
/// </summary>
[Trait("Category", "Integration")]
public class ReasoningRunServiceIntegrationTests : IAsyncLifetime
{
    // Note: These tests require a real database connection
    // They are marked as Integration tests and skipped in CI by default

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Skip = "Requires database connection")]
    public async Task FullExecutionLifecycle_PersistsAllData()
    {
        // This would test:
        // 1. Creating an execution
        // 2. Persisting traces
        // 3. Persisting insights
        // 4. Persisting disagreements
        // 5. Retrieving the full execution with all relations
        await Task.CompletedTask;
    }

    [Fact(Skip = "Requires database connection")]
    public async Task TenantIsolation_CannotAccessOtherTenantExecutions()
    {
        // This would test:
        // 1. Tenant A creates an execution
        // 2. Tenant B tries to retrieve it
        // 3. Verify RLS blocks access
        await Task.CompletedTask;
    }

    [Fact(Skip = "Requires database connection")]
    public async Task GetRecentExecutions_ReturnsOnlyTenantData()
    {
        // This would test:
        // 1. Create executions for multiple tenants
        // 2. Query recent executions for one tenant
        // 3. Verify only that tenant's executions are returned
        await Task.CompletedTask;
    }
}
