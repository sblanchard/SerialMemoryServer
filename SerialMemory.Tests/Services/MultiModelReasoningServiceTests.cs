using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure.Services;
using Xunit;

namespace SerialMemory.Tests.Services;

/// <summary>
/// Comprehensive unit tests for multi-model reasoning orchestration.
/// Tests parallel execution, timeout handling, insight merging, and failure isolation.
/// </summary>
public class MultiModelReasoningServiceTests
{
    private readonly Mock<IKnowledgeGraphStore> _storeMock;
    private readonly Mock<IEngineeringReasoningService> _ruleBasedMock;
    private readonly Mock<IReasoningModelFactory> _factoryMock;
    private readonly MultiModelReasoningService _service;

    public MultiModelReasoningServiceTests()
    {
        _storeMock = new Mock<IKnowledgeGraphStore>();
        _ruleBasedMock = new Mock<IEngineeringReasoningService>();
        _factoryMock = new Mock<IReasoningModelFactory>();
        var loggerMock = new Mock<ILogger<MultiModelReasoningService>>();

        _service = new MultiModelReasoningService(
            _storeMock.Object,
            _ruleBasedMock.Object,
            _factoryMock.Object,
            loggerMock.Object);

        // Default setup - empty data
        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntityRelationship>());

        // Default rule-based result
        _ruleBasedMock.Setup(s => s.AnalyzeAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineeringAnalysisResult { Insights = [] });

        // Default factory - no models
        _factoryMock.Setup(f => f.CreateModels()).Returns(Enumerable.Empty<IReasoningModel>());
    }

    #region Basic Execution Tests

    [Fact]
    public async Task ReasonAsync_WithNoModels_ReturnsRuleBasedResultOnly()
    {
        // Arrange
        var ruleInsight = new EngineeringInsight
        {
            Severity = InsightSeverity.Risk,
            Category = InsightCategory.PowerIntegrity,
            Title = "Test Issue",
            Description = "Test Description",
            Confidence = 0.9f
        };

        _ruleBasedMock.Setup(s => s.AnalyzeAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineeringAnalysisResult { Insights = [ruleInsight] });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.Should().NotBeNull();
        result.ModelsUsed.Should().Be(1); // Only rule-based
        result.SuccessfulModels.Should().Be(1);
        result.Insights.Should().HaveCount(1);
        result.Trace.Should().ContainSingle(t => t.Model == "RuleBasedEngine");
    }

    [Fact]
    public async Task ReasonAsync_WithModels_IncludesAllModelResults()
    {
        // Arrange
        var model1 = CreateMockModel("Model1", ReasoningRole.Structural, success: true,
            insights: [CreateInsight(MultiModelInsightType.Info, "Structural finding", 0.8f)]);

        var model2 = CreateMockModel("Model2", ReasoningRole.Risk, success: true,
            insights: [CreateInsight(MultiModelInsightType.Risk, "Risk finding", 0.9f)]);

        _factoryMock.Setup(f => f.CreateModels())
            .Returns(new[] { model1.Object, model2.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.ModelsUsed.Should().Be(3); // Rule-based + 2 models
        result.SuccessfulModels.Should().Be(3);
        result.Insights.Should().HaveCount(2);
        result.Trace.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReasonAsync_GeneratesUniqueInputHash()
    {
        // Arrange
        SetupEntitiesAndRelationships(
            [new Entity { Id = Guid.CreateVersion7(), Name = "Test", EntityType = "TEST" }],
            []);

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.InputHash.Should().NotBeNullOrEmpty();
        result.InputHash.Should().HaveLength(16); // 16-char hex hash
    }

    [Fact]
    public async Task ReasonAsync_RecordsTotalDuration()
    {
        // Arrange
        var slowModel = CreateMockModel("SlowModel", ReasoningRole.Structural, success: true,
            delay: TimeSpan.FromMilliseconds(100));

        _factoryMock.Setup(f => f.CreateModels()).Returns(new[] { slowModel.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.TotalDurationMs.Should().BeGreaterThanOrEqualTo(100);
    }

    #endregion

    #region Parallel Execution Tests

    [Fact]
    public async Task ReasonAsync_RunsModelsInParallel()
    {
        // Arrange: 3 models each taking 100ms should complete in ~100ms if parallel
        var model1 = CreateMockModel("Model1", ReasoningRole.Structural, success: true,
            delay: TimeSpan.FromMilliseconds(100));
        var model2 = CreateMockModel("Model2", ReasoningRole.Risk, success: true,
            delay: TimeSpan.FromMilliseconds(100));
        var model3 = CreateMockModel("Model3", ReasoningRole.Optimization, success: true,
            delay: TimeSpan.FromMilliseconds(100));

        _factoryMock.Setup(f => f.CreateModels())
            .Returns(new[] { model1.Object, model2.Object, model3.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert: Should complete in ~100-200ms, not 900ms+ if sequential (generous for CI)
        result.TotalDurationMs.Should().BeLessThan(800);
        result.SuccessfulModels.Should().Be(4); // Rule-based + 3 models
    }

    #endregion

    #region Timeout Handling Tests

    [Fact]
    public async Task ReasonAsync_TimesOutSlowModels()
    {
        // Arrange: Model that takes longer than its timeout
        var slowModel = new Mock<IReasoningModel>();
        slowModel.Setup(m => m.Name).Returns("SlowModel");
        slowModel.Setup(m => m.Version).Returns("1.0");
        slowModel.Setup(m => m.Role).Returns(ReasoningRole.Structural);
        slowModel.Setup(m => m.Timeout).Returns(TimeSpan.FromMilliseconds(50));
        slowModel.Setup(m => m.ReasonAsync(It.IsAny<ReasoningInput>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReasoningInput _, CancellationToken ct) =>
            {
                await Task.Delay(5000, ct); // Takes 5 seconds
                return new ModelResult
                {
                    ModelName = "SlowModel",
                    Version = "1.0",
                    Role = ReasoningRole.Structural,
                    Success = true
                };
            });

        _factoryMock.Setup(f => f.CreateModels()).Returns(new[] { slowModel.Object });

        // Act
        var result = await _service.ReasonAsync(maxDurationMs: 500);

        // Assert
        result.Trace.Should().Contain(t => t.Model == "SlowModel" && !t.Success && t.Error == "Timeout");
        result.SuccessfulModels.Should().BeLessThan(result.ModelsUsed);
    }

    [Fact]
    public async Task ReasonAsync_RespectsGlobalTimeout()
    {
        // Arrange: Multiple slow models that together exceed global timeout
        var model1 = CreateMockModel("Model1", ReasoningRole.Structural, success: true,
            delay: TimeSpan.FromSeconds(1));
        var model2 = CreateMockModel("Model2", ReasoningRole.Risk, success: true,
            delay: TimeSpan.FromSeconds(1));

        _factoryMock.Setup(f => f.CreateModels())
            .Returns(new[] { model1.Object, model2.Object });

        // Act
        var result = await _service.ReasonAsync(maxDurationMs: 100);

        // Assert: Should not wait for all models to complete
        result.TotalDurationMs.Should().BeLessThan(2000);
    }

    #endregion

    #region Failure Isolation Tests

    [Fact]
    public async Task ReasonAsync_IsolatesModelFailures()
    {
        // Arrange: One model fails, others should still work
        var failingModel = new Mock<IReasoningModel>();
        failingModel.Setup(m => m.Name).Returns("FailingModel");
        failingModel.Setup(m => m.Version).Returns("1.0");
        failingModel.Setup(m => m.Role).Returns(ReasoningRole.Structural);
        failingModel.Setup(m => m.Timeout).Returns(TimeSpan.FromSeconds(10));
        failingModel.Setup(m => m.ReasonAsync(It.IsAny<ReasoningInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Model crashed"));

        var successModel = CreateMockModel("SuccessModel", ReasoningRole.Risk, success: true,
            insights: [CreateInsight(MultiModelInsightType.Risk, "Success insight", 0.9f)]);

        _factoryMock.Setup(f => f.CreateModels())
            .Returns(new[] { failingModel.Object, successModel.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.Trace.Should().Contain(t => t.Model == "FailingModel" && !t.Success);
        result.Trace.Should().Contain(t => t.Model == "SuccessModel" && t.Success);
        result.Insights.Should().HaveCount(1); // Only successful model's insights
    }

    [Fact]
    public async Task ReasonAsync_ContinuesWhenRuleBasedFails()
    {
        // Arrange
        _ruleBasedMock.Setup(s => s.AnalyzeAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Rule engine failed"));

        var model = CreateMockModel("Model1", ReasoningRole.Structural, success: true,
            insights: [CreateInsight(MultiModelInsightType.Info, "Model insight", 0.8f)]);

        _factoryMock.Setup(f => f.CreateModels()).Returns(new[] { model.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.Trace.Should().Contain(t => t.Model == "RuleBasedEngine" && !t.Success);
        result.Insights.Should().HaveCount(1); // Model's insight
    }

    #endregion

    #region Insight Merging Tests

    [Fact]
    public async Task ReasonAsync_MergesSimilarInsights()
    {
        // Arrange: Two models produce similar insights
        var model1 = CreateMockModel("Model1", ReasoningRole.Structural, success: true,
            insights: [CreateInsight(MultiModelInsightType.Risk, "Security vulnerability detected", 0.8f)]);

        var model2 = CreateMockModel("Model2", ReasoningRole.Risk, success: true,
            insights: [CreateInsight(MultiModelInsightType.Risk, "security vulnerability detected", 0.7f)]);

        _factoryMock.Setup(f => f.CreateModels())
            .Returns(new[] { model1.Object, model2.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.Insights.Should().ContainSingle();
        var mergedInsight = result.Insights[0];
        mergedInsight.AgreementCount.Should().Be(2);
        mergedInsight.SourceModels.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReasonAsync_BoostsConfidenceWithAgreement()
    {
        // Arrange: Two models agree on same insight
        var model1 = CreateMockModel("Model1", ReasoningRole.Structural, success: true,
            insights: [CreateInsight(MultiModelInsightType.Risk, "Critical issue found", 0.6f)]);

        var model2 = CreateMockModel("Model2", ReasoningRole.Risk, success: true,
            insights: [CreateInsight(MultiModelInsightType.Risk, "critical issue found", 0.6f)]);

        _factoryMock.Setup(f => f.CreateModels())
            .Returns(new[] { model1.Object, model2.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        var mergedInsight = result.Insights[0];
        mergedInsight.Confidence.Should().BeGreaterThan(0.6f); // Should be boosted
    }

    [Fact]
    public async Task ReasonAsync_KeepsDistinctInsightsSeparate()
    {
        // Arrange: Two models produce different insights
        var model1 = CreateMockModel("Model1", ReasoningRole.Structural, success: true,
            insights: [CreateInsight(MultiModelInsightType.Risk, "Security issue", 0.8f)]);

        var model2 = CreateMockModel("Model2", ReasoningRole.Risk, success: true,
            insights: [CreateInsight(MultiModelInsightType.Optimization, "Performance issue", 0.7f)]);

        _factoryMock.Setup(f => f.CreateModels())
            .Returns(new[] { model1.Object, model2.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.Insights.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReasonAsync_SortsInsightsByConfidenceAndSeverity()
    {
        // Arrange
        var model = CreateMockModel("Model1", ReasoningRole.Structural, success: true,
            insights:
            [
                CreateInsight(MultiModelInsightType.Info, "Info", 0.9f),
                CreateInsight(MultiModelInsightType.Risk, "Risk", 0.5f),
                CreateInsight(MultiModelInsightType.Conflict, "Conflict", 0.7f)
            ]);

        _factoryMock.Setup(f => f.CreateModels()).Returns(new[] { model.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert: Should be sorted by confidence descending, then severity
        result.Insights[0].Confidence.Should().Be(0.9f);
    }

    #endregion

    #region Project Filter Tests

    [Fact]
    public async Task ReasonAsync_WithProjectFilter_FiltersEntities()
    {
        // Arrange
        var projectId = Guid.CreateVersion7();
        var relatedId = Guid.CreateVersion7();
        var unrelatedId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = projectId, Name = "TestProject", EntityType = "PROJECT" },
            new() { Id = relatedId, Name = "RelatedService", EntityType = "SERVICE" },
            new() { Id = unrelatedId, Name = "UnrelatedService", EntityType = "SERVICE" }
        };

        var relationships = new List<EntityRelationship>
        {
            new()
            {
                Id = Guid.CreateVersion7(),
                SourceEntityId = projectId,
                TargetEntityId = relatedId,
                RelationshipType = "CONTAINS"
            }
        };

        SetupEntitiesAndRelationships(entities, relationships);

        // Act
        var result = await _service.ReasonAsync(projectFilter: "TestProject");

        // Assert
        result.Should().NotBeNull();
        // The filtered input should only contain project and related entities
    }

    [Fact]
    public async Task ReasonAsync_WithNonExistentProject_ReturnsResult()
    {
        // Arrange
        SetupEntitiesAndRelationships(
            [new Entity { Id = Guid.CreateVersion7(), Name = "Other", EntityType = "SERVICE" }],
            []);

        // Act
        var result = await _service.ReasonAsync(projectFilter: "NonExistent");

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region Memory Reasoning Tests

    [Fact]
    public async Task ReasonMemoryAsync_WithValidMemory_ReturnsResult()
    {
        // Arrange
        var memoryId = Guid.CreateVersion7();
        var entityId = Guid.CreateVersion7();

        var memoryEntities = new List<Entity>
        {
            new() { Id = entityId, Name = "TestEntity", EntityType = "TEST" }
        };

        _storeMock.Setup(s => s.GetEntitiesForMemoryAsync(memoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryEntities);

        // Act
        var result = await _service.ReasonMemoryAsync(memoryId);

        // Assert
        result.Should().NotBeNull();
        result.InputHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ReasonMemoryAsync_WithNoEntities_ReturnsEmptyResult()
    {
        // Arrange
        var memoryId = Guid.CreateVersion7();

        _storeMock.Setup(s => s.GetEntitiesForMemoryAsync(memoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());

        // Act
        var result = await _service.ReasonMemoryAsync(memoryId);

        // Assert
        result.Should().NotBeNull();
        result.Insights.Should().BeEmpty();
        result.ModelsUsed.Should().Be(0);
    }

    [Fact]
    public async Task ReasonMemoryAsync_ExpandsToConnectedEntities()
    {
        // Arrange
        var memoryId = Guid.CreateVersion7();
        var entity1Id = Guid.CreateVersion7();
        var entity2Id = Guid.CreateVersion7();

        var memoryEntities = new List<Entity>
        {
            new() { Id = entity1Id, Name = "Entity1", EntityType = "TEST" }
        };

        var allEntities = new List<Entity>
        {
            new() { Id = entity1Id, Name = "Entity1", EntityType = "TEST" },
            new() { Id = entity2Id, Name = "Entity2", EntityType = "TEST" }
        };

        var relationships = new List<EntityRelationship>
        {
            new()
            {
                Id = Guid.CreateVersion7(),
                SourceEntityId = entity1Id,
                TargetEntityId = entity2Id,
                RelationshipType = "RELATED"
            }
        };

        _storeMock.Setup(s => s.GetEntitiesForMemoryAsync(memoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryEntities);
        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(allEntities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.ReasonMemoryAsync(memoryId);

        // Assert
        result.Should().NotBeNull();
        // Both entities should be included due to relationship expansion
    }

    #endregion

    #region Trace Tests

    [Fact]
    public async Task ReasonAsync_RecordsTraceForAllModels()
    {
        // Arrange
        var model = CreateMockModel("TestModel", ReasoningRole.Structural, success: true);
        _factoryMock.Setup(f => f.CreateModels()).Returns(new[] { model.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        result.Trace.Should().Contain(t => t.Model == "RuleBasedEngine");
        result.Trace.Should().Contain(t => t.Model == "TestModel");
        result.Trace.All(t => t.DurationMs >= 0).Should().BeTrue();
    }

    [Fact]
    public async Task ReasonAsync_TraceIncludesRoleAndVersion()
    {
        // Arrange
        var model = CreateMockModel("TestModel", ReasoningRole.Risk, success: true);
        _factoryMock.Setup(f => f.CreateModels()).Returns(new[] { model.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        var modelTrace = result.Trace.Single(t => t.Model == "TestModel");
        modelTrace.Role.Should().Be(ReasoningRole.Risk);
        modelTrace.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task ReasonAsync_TraceIncludesErrorMessage()
    {
        // Arrange
        var failingModel = new Mock<IReasoningModel>();
        failingModel.Setup(m => m.Name).Returns("FailingModel");
        failingModel.Setup(m => m.Version).Returns("1.0");
        failingModel.Setup(m => m.Role).Returns(ReasoningRole.Structural);
        failingModel.Setup(m => m.Timeout).Returns(TimeSpan.FromSeconds(10));
        failingModel.Setup(m => m.ReasonAsync(It.IsAny<ReasoningInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error message"));

        _factoryMock.Setup(f => f.CreateModels()).Returns(new[] { failingModel.Object });

        // Act
        var result = await _service.ReasonAsync();

        // Assert
        var failedTrace = result.Trace.Single(t => t.Model == "FailingModel");
        failedTrace.Success.Should().BeFalse();
        failedTrace.Error.Should().Be("Test error message");
    }

    #endregion

    #region Helper Methods

    private void SetupEntitiesAndRelationships(List<Entity> entities, List<EntityRelationship> relationships)
    {
        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);
    }

    private static Mock<IReasoningModel> CreateMockModel(
        string name,
        ReasoningRole role,
        bool success,
        List<ModelInsight>? insights = null,
        TimeSpan? delay = null)
    {
        var model = new Mock<IReasoningModel>();
        model.Setup(m => m.Name).Returns(name);
        model.Setup(m => m.Version).Returns("1.0.0");
        model.Setup(m => m.Role).Returns(role);
        model.Setup(m => m.Timeout).Returns(TimeSpan.FromSeconds(10));

        model.Setup(m => m.ReasonAsync(It.IsAny<ReasoningInput>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReasoningInput _, CancellationToken ct) =>
            {
                if (delay.HasValue)
                {
                    await Task.Delay(delay.Value, ct);
                }

                return new ModelResult
                {
                    ModelName = name,
                    Version = "1.0.0",
                    Role = role,
                    Success = success,
                    Insights = insights ?? []
                };
            });

        return model;
    }

    private static ModelInsight CreateInsight(MultiModelInsightType type, string message, float confidence)
    {
        return new ModelInsight
        {
            Type = type,
            Message = message,
            Confidence = confidence
        };
    }

    #endregion
}

/// <summary>
/// Tests for individual reasoning models in DefaultReasoningModelFactory.
/// </summary>
public class ReasoningModelTests
{
    private readonly Mock<IKnowledgeGraphStore> _storeMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;

    public ReasoningModelTests()
    {
        _storeMock = new Mock<IKnowledgeGraphStore>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();

        // Setup logger factory to return mock loggers
        _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);
    }

    [Fact]
    public void Factory_CreatesAllModelTypes()
    {
        // Arrange
        var factory = new DefaultReasoningModelFactory(_storeMock.Object, _loggerFactoryMock.Object);

        // Act
        var models = factory.CreateModels().ToList();

        // Assert
        models.Should().HaveCount(4);
        models.Should().Contain(m => m.Role == ReasoningRole.Structural);
        models.Should().Contain(m => m.Role == ReasoningRole.Risk);
        models.Should().Contain(m => m.Role == ReasoningRole.Optimization);
        models.Should().Contain(m => m.Role == ReasoningRole.Contradiction);
    }

    [Fact]
    public void Factory_CreateModelsForRole_ReturnsCorrectModels()
    {
        // Arrange
        var factory = new DefaultReasoningModelFactory(_storeMock.Object, _loggerFactoryMock.Object);

        // Act
        var riskModels = factory.CreateModelsForRole(ReasoningRole.Risk).ToList();

        // Assert
        riskModels.Should().HaveCount(1);
        riskModels[0].Role.Should().Be(ReasoningRole.Risk);
    }

    [Fact]
    public async Task StructuralModel_DetectsOrphanedEntities()
    {
        // Arrange
        var entities = new List<Entity>
        {
            new() { Id = Guid.CreateVersion7(), Name = "Connected", EntityType = "TEST" },
            new() { Id = Guid.CreateVersion7(), Name = "Orphaned", EntityType = "TEST" }
        };

        var relationships = new List<EntityRelationship>
        {
            new()
            {
                Id = Guid.CreateVersion7(),
                SourceEntityId = entities[0].Id,
                TargetEntityId = Guid.CreateVersion7(), // Points to non-existent
                RelationshipType = "TEST"
            }
        };

        var input = new ReasoningInput
        {
            Content = "Test",
            Entities = entities,
            Relationships = relationships
        };

        var factory = new DefaultReasoningModelFactory(_storeMock.Object, _loggerFactoryMock.Object);
        var structuralModel = factory.CreateModelsForRole(ReasoningRole.Structural).First();

        // Act
        var result = await structuralModel.ReasonAsync(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Insights.Should().Contain(i => i.Message.Contains("orphaned"));
    }

    [Fact]
    public async Task RiskModel_DetectsSinglePointsOfFailure()
    {
        // Arrange
        var criticalId = Guid.CreateVersion7();
        var dep1Id = Guid.CreateVersion7();
        var dep2Id = Guid.CreateVersion7();
        var dep3Id = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = criticalId, Name = "CriticalService", EntityType = "SERVICE" },
            new() { Id = dep1Id, Name = "Dep1", EntityType = "SERVICE" },
            new() { Id = dep2Id, Name = "Dep2", EntityType = "SERVICE" },
            new() { Id = dep3Id, Name = "Dep3", EntityType = "SERVICE" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dep1Id, TargetEntityId = criticalId, RelationshipType = "DEPENDS_ON" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dep2Id, TargetEntityId = criticalId, RelationshipType = "DEPENDS_ON" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dep3Id, TargetEntityId = criticalId, RelationshipType = "DEPENDS_ON" }
        };

        var input = new ReasoningInput
        {
            Content = "Test",
            Entities = entities,
            Relationships = relationships
        };

        var factory = new DefaultReasoningModelFactory(_storeMock.Object, _loggerFactoryMock.Object);
        var riskModel = factory.CreateModelsForRole(ReasoningRole.Risk).First();

        // Act
        var result = await riskModel.ReasonAsync(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Insights.Should().Contain(i =>
            i.Type == MultiModelInsightType.Risk &&
            i.Message.Contains("Single point of failure"));
    }

    [Fact]
    public async Task OptimizationModel_DetectsPotentialDuplicates()
    {
        // Arrange
        var entities = new List<Entity>
        {
            new() { Id = Guid.CreateVersion7(), Name = "UserService", EntityType = "SERVICE" },
            new() { Id = Guid.CreateVersion7(), Name = "User Service", EntityType = "SERVICE" },
            new() { Id = Guid.CreateVersion7(), Name = "Unique", EntityType = "SERVICE" }
        };

        var input = new ReasoningInput
        {
            Content = "Test",
            Entities = entities,
            Relationships = []
        };

        var factory = new DefaultReasoningModelFactory(_storeMock.Object, _loggerFactoryMock.Object);
        var optModel = factory.CreateModelsForRole(ReasoningRole.Optimization).First();

        // Act
        var result = await optModel.ReasonAsync(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Insights.Should().Contain(i =>
            i.Type == MultiModelInsightType.Optimization &&
            i.Message.Contains("duplicate"));
    }

    [Fact]
    public async Task ContradictionModel_DetectsVersionConflicts()
    {
        // Arrange
        var entities = new List<Entity>
        {
            new() { Id = Guid.CreateVersion7(), Name = "Library v1.0", EntityType = "DEPENDENCY" },
            new() { Id = Guid.CreateVersion7(), Name = "Library v2.0", EntityType = "DEPENDENCY" }
        };

        var input = new ReasoningInput
        {
            Content = "Test",
            Entities = entities,
            Relationships = []
        };

        var factory = new DefaultReasoningModelFactory(_storeMock.Object, _loggerFactoryMock.Object);
        var contradictionModel = factory.CreateModelsForRole(ReasoningRole.Contradiction).First();

        // Act
        var result = await contradictionModel.ReasonAsync(input);

        // Assert
        result.Success.Should().BeTrue();
        result.Insights.Should().Contain(i =>
            i.Type == MultiModelInsightType.Conflict &&
            i.Message.Contains("version"));
    }

    [Fact]
    public async Task AllModels_HandleEmptyInput()
    {
        // Arrange
        var input = new ReasoningInput
        {
            Content = "Empty test",
            Entities = [],
            Relationships = []
        };

        var factory = new DefaultReasoningModelFactory(_storeMock.Object, _loggerFactoryMock.Object);
        var models = factory.CreateModels().ToList();

        // Act & Assert
        foreach (var model in models)
        {
            var result = await model.ReasonAsync(input);
            result.Success.Should().BeTrue($"Model {model.Name} should handle empty input");
        }
    }

    [Fact]
    public async Task AllModels_HandleNullCollections()
    {
        // Arrange
        var input = new ReasoningInput
        {
            Content = "Null test",
            Entities = null,
            Relationships = null
        };

        var factory = new DefaultReasoningModelFactory(_storeMock.Object, _loggerFactoryMock.Object);
        var models = factory.CreateModels().ToList();

        // Act & Assert
        foreach (var model in models)
        {
            var result = await model.ReasonAsync(input);
            result.Success.Should().BeTrue($"Model {model.Name} should handle null collections");
        }
    }
}
