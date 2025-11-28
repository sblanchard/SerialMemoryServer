using FluentAssertions;
using Moq;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using SerialMemory.Infrastructure.Services;
using Xunit;

namespace SerialMemory.Tests.Services;

/// <summary>
/// Unit tests for the engineering reasoning service.
/// Tests all rule-based inference: power integrity, signal integrity,
/// dependency corruption, and thermal risk.
/// </summary>
public class EngineeringReasoningServiceTests
{
    private readonly Mock<IKnowledgeGraphStore> _storeMock;
    private readonly EngineeringReasoningService _service;

    public EngineeringReasoningServiceTests()
    {
        _storeMock = new Mock<IKnowledgeGraphStore>();
        _service = new EngineeringReasoningService(_storeMock.Object);
    }

    #region Power Integrity Tests

    [Fact]
    public async Task DetectPowerIntegrityIssues_VoltageMismatch_ReturnsConflict()
    {
        // Arrange: Component requires 5V but power rail supplies 3.3V
        var componentId = Guid.CreateVersion7();
        var powerRailId = Guid.CreateVersion7();
        var voltageReqId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = componentId, Name = "MCU", EntityType = "COMPONENT" },
            new() { Id = powerRailId, Name = "3.3V Rail", EntityType = "POWER_RAIL" },
            new() { Id = voltageReqId, Name = "5V", EntityType = "VOLTAGE" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = powerRailId, TargetEntityId = componentId, RelationshipType = "POWERS" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = componentId, TargetEntityId = voltageReqId, RelationshipType = "REQUIRES" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectPowerIntegrityIssuesAsync();

        // Assert
        result.Should().ContainSingle(i =>
            i.Category == InsightCategory.PowerIntegrity &&
            i.Severity == InsightSeverity.Conflict &&
            i.Title == "Voltage Mismatch Detected");
    }

    [Fact]
    public async Task DetectPowerIntegrityIssues_OvercurrentRisk_ReturnsWarningOrRisk()
    {
        // Arrange: Power rail capacity 500mA, components draw 600mA total
        var railId = Guid.CreateVersion7();
        var comp1Id = Guid.CreateVersion7();
        var comp2Id = Guid.CreateVersion7();
        var current1Id = Guid.CreateVersion7();
        var current2Id = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = railId, Name = "VCC 500mA", EntityType = "POWER_RAIL" },
            new() { Id = comp1Id, Name = "Sensor1", EntityType = "COMPONENT" },
            new() { Id = comp2Id, Name = "Sensor2", EntityType = "COMPONENT" },
            new() { Id = current1Id, Name = "300mA", EntityType = "CURRENT" },
            new() { Id = current2Id, Name = "350mA", EntityType = "CURRENT" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = railId, TargetEntityId = comp1Id, RelationshipType = "POWERS" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = railId, TargetEntityId = comp2Id, RelationshipType = "POWERS" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = comp1Id, TargetEntityId = current1Id, RelationshipType = "USES" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = comp2Id, TargetEntityId = current2Id, RelationshipType = "USES" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectPowerIntegrityIssuesAsync();

        // Assert
        result.Should().Contain(i =>
            i.Category == InsightCategory.PowerIntegrity &&
            i.Title == "Overcurrent Risk");
    }

    [Fact]
    public async Task DetectPowerIntegrityIssues_LongPowerChain_ReturnsWarning()
    {
        // Arrange: Power chain of 4 hops: Source -> Reg1 -> Reg2 -> MCU
        var sourceId = Guid.CreateVersion7();
        var reg1Id = Guid.CreateVersion7();
        var reg2Id = Guid.CreateVersion7();
        var mcuId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = sourceId, Name = "12V Input", EntityType = "POWER_RAIL" },
            new() { Id = reg1Id, Name = "5V Regulator", EntityType = "COMPONENT" },
            new() { Id = reg2Id, Name = "3.3V Regulator", EntityType = "COMPONENT" },
            new() { Id = mcuId, Name = "MCU", EntityType = "COMPONENT" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = sourceId, TargetEntityId = reg1Id, RelationshipType = "POWERS" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = reg1Id, TargetEntityId = reg2Id, RelationshipType = "POWERS" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = reg2Id, TargetEntityId = mcuId, RelationshipType = "POWERS" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectPowerIntegrityIssuesAsync();

        // Assert
        result.Should().Contain(i =>
            i.Category == InsightCategory.PowerIntegrity &&
            i.Title == "Long Power Chain Detected");
    }

    #endregion

    #region Signal Integrity Tests

    [Fact]
    public async Task DetectSignalIntegrityIssues_ClockMismatch_ReturnsConflict()
    {
        // Arrange: Device1 operates at 48MHz, Device2 at 24MHz
        var dev1Id = Guid.CreateVersion7();
        var dev2Id = Guid.CreateVersion7();
        var freq1Id = Guid.CreateVersion7();
        var freq2Id = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = dev1Id, Name = "USB Controller", EntityType = "COMPONENT" },
            new() { Id = dev2Id, Name = "Audio Codec", EntityType = "COMPONENT" },
            new() { Id = freq1Id, Name = "48 MHz", EntityType = "FREQUENCY" },
            new() { Id = freq2Id, Name = "24 MHz", EntityType = "FREQUENCY" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dev1Id, TargetEntityId = dev2Id, RelationshipType = "COMMUNICATES_VIA" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dev1Id, TargetEntityId = freq1Id, RelationshipType = "OPERATES_AT" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dev2Id, TargetEntityId = freq2Id, RelationshipType = "OPERATES_AT" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectSignalIntegrityIssuesAsync();

        // Assert
        result.Should().Contain(i =>
            i.Category == InsightCategory.SignalIntegrity &&
            i.Title == "Clock Frequency Mismatch");
    }

    [Fact]
    public async Task DetectSignalIntegrityIssues_ProtocolMismatch_ReturnsConflict()
    {
        // Arrange: Device1 uses SPI, Device2 uses I2C
        var dev1Id = Guid.CreateVersion7();
        var dev2Id = Guid.CreateVersion7();
        var proto1Id = Guid.CreateVersion7();
        var proto2Id = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = dev1Id, Name = "Sensor", EntityType = "COMPONENT" },
            new() { Id = dev2Id, Name = "MCU", EntityType = "COMPONENT" },
            new() { Id = proto1Id, Name = "SPI", EntityType = "PROTOCOL" },
            new() { Id = proto2Id, Name = "I2C", EntityType = "PROTOCOL" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dev1Id, TargetEntityId = dev2Id, RelationshipType = "COMMUNICATES_VIA" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dev1Id, TargetEntityId = proto1Id, RelationshipType = "USES" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dev2Id, TargetEntityId = proto2Id, RelationshipType = "USES" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectSignalIntegrityIssuesAsync();

        // Assert
        result.Should().Contain(i =>
            i.Category == InsightCategory.ProtocolMismatch &&
            i.Title == "Protocol Incompatibility");
    }

    [Fact]
    public async Task DetectSignalIntegrityIssues_BaudRateMismatch_ReturnsConflict()
    {
        // Arrange: Device1 at 9600 baud, Device2 at 115200 baud
        var dev1Id = Guid.CreateVersion7();
        var dev2Id = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new()
            {
                Id = dev1Id,
                Name = "GPS Module",
                EntityType = "COMPONENT",
                Metadata = new Dictionary<string, object> { ["baud_rate"] = 9600 }
            },
            new()
            {
                Id = dev2Id,
                Name = "MCU UART",
                EntityType = "COMPONENT",
                Metadata = new Dictionary<string, object> { ["baud_rate"] = 115200 }
            }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = dev1Id, TargetEntityId = dev2Id, RelationshipType = "COMMUNICATES_VIA" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectSignalIntegrityIssuesAsync();

        // Assert
        result.Should().Contain(i =>
            i.Category == InsightCategory.SignalIntegrity &&
            i.Title == "Baud Rate Mismatch");
    }

    #endregion

    #region Dependency Corruption Tests

    [Fact]
    public async Task DetectDependencyCorruption_ServiceDependsOnIncidentAffectedService_ReturnsRisk()
    {
        // Arrange: ServiceA depends on ServiceB, ServiceB is affected by an incident
        var serviceAId = Guid.CreateVersion7();
        var serviceBId = Guid.CreateVersion7();
        var incidentId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = serviceAId, Name = "Payment Service", EntityType = "SERVICE" },
            new() { Id = serviceBId, Name = "Auth Service", EntityType = "SERVICE" },
            new() { Id = incidentId, Name = "INC-2024-001", EntityType = "INCIDENT" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = serviceAId, TargetEntityId = serviceBId, RelationshipType = "DEPENDS_ON" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = incidentId, TargetEntityId = serviceBId, RelationshipType = "CAUSED" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectDependencyCorruptionAsync();

        // Assert
        result.Should().Contain(i =>
            i.Category == InsightCategory.DependencyCorruption &&
            i.Severity == InsightSeverity.Risk &&
            i.Title == "Cascading Dependency Risk");
    }

    [Fact]
    public async Task DetectDependencyCorruption_TransitiveDependencyToIncident_ReturnsWarning()
    {
        // Arrange: A -> B -> C, where C is affected by incident
        var aId = Guid.CreateVersion7();
        var bId = Guid.CreateVersion7();
        var cId = Guid.CreateVersion7();
        var incidentId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = aId, Name = "Frontend", EntityType = "SERVICE" },
            new() { Id = bId, Name = "API Gateway", EntityType = "SERVICE" },
            new() { Id = cId, Name = "Database", EntityType = "DATABASE" },
            new() { Id = incidentId, Name = "DB Outage", EntityType = "INCIDENT" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = aId, TargetEntityId = bId, RelationshipType = "CALLS" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = bId, TargetEntityId = cId, RelationshipType = "DEPENDS_ON" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = incidentId, TargetEntityId = cId, RelationshipType = "AFFECTS" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectDependencyCorruptionAsync();

        // Assert
        result.Should().Contain(i => i.Category == InsightCategory.DependencyCorruption);
    }

    #endregion

    #region Thermal Risk Tests

    [Fact]
    public async Task DetectThermalRisk_TemperatureExceedsMax_ReturnsRisk()
    {
        // Arrange: Component operating at 90°C, max rating 85°C
        var compId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new()
            {
                Id = compId,
                Name = "Power MOSFET",
                EntityType = "COMPONENT",
                Metadata = new Dictionary<string, object>
                {
                    ["operating_temp_c"] = 90.0,
                    ["max_temp_c"] = 85.0
                }
            }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntityRelationship>());

        // Act
        var result = await _service.DetectThermalRiskAsync();

        // Assert
        result.Should().ContainSingle(i =>
            i.Category == InsightCategory.ThermalRisk &&
            i.Severity == InsightSeverity.Risk &&
            i.Title == "Temperature Exceeds Maximum Rating");
    }

    [Fact]
    public async Task DetectThermalRisk_LowThermalMargin_ReturnsWarning()
    {
        // Arrange: Component operating at 80°C, max rating 85°C (5°C margin < 10°C threshold)
        var compId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new()
            {
                Id = compId,
                Name = "Voltage Regulator",
                EntityType = "COMPONENT",
                Metadata = new Dictionary<string, object>
                {
                    ["operating_temp_c"] = 80.0,
                    ["max_temp_c"] = 85.0
                }
            }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntityRelationship>());

        // Act
        var result = await _service.DetectThermalRiskAsync();

        // Assert
        result.Should().ContainSingle(i =>
            i.Category == InsightCategory.ThermalRisk &&
            i.Severity == InsightSeverity.Warning &&
            i.Title == "Low Thermal Margin");
    }

    [Fact]
    public async Task DetectThermalRisk_ThermalProximityConcern_ReturnsWarning()
    {
        // Arrange: Low temp component mounted near high temp component
        var sensitiveId = Guid.CreateVersion7();
        var heatSourceId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new()
            {
                Id = sensitiveId,
                Name = "Electrolytic Cap",
                EntityType = "COMPONENT",
                Metadata = new Dictionary<string, object> { ["max_temp_c"] = 65.0 }
            },
            new()
            {
                Id = heatSourceId,
                Name = "Power Transistor",
                EntityType = "COMPONENT",
                Metadata = new Dictionary<string, object> { ["max_temp_c"] = 150.0 }
            }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = sensitiveId, TargetEntityId = heatSourceId, RelationshipType = "MOUNTED_ON" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.DetectThermalRiskAsync();

        // Assert
        result.Should().Contain(i =>
            i.Category == InsightCategory.ThermalRisk &&
            i.Title == "Thermal Proximity Concern");
    }

    #endregion

    #region Full Analysis Tests

    [Fact]
    public async Task AnalyzeAsync_CombinesAllRules_ReturnsAggregatedInsights()
    {
        // Arrange: Multiple issues across categories
        var compId = Guid.CreateVersion7();
        var railId = Guid.CreateVersion7();
        var voltageId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = compId, Name = "MCU (85°C max)", EntityType = "COMPONENT", Metadata = new Dictionary<string, object> { ["operating_temp_c"] = 90.0, ["max_temp_c"] = 85.0 } },
            new() { Id = railId, Name = "3.3V Rail", EntityType = "POWER_RAIL" },
            new() { Id = voltageId, Name = "5V", EntityType = "VOLTAGE" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = railId, TargetEntityId = compId, RelationshipType = "POWERS" },
            new() { Id = Guid.CreateVersion7(), SourceEntityId = compId, TargetEntityId = voltageId, RelationshipType = "REQUIRES" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.AnalyzeAsync();

        // Assert
        result.EntitiesAnalyzed.Should().Be(3);
        result.RelationshipsAnalyzed.Should().Be(2);
        result.Insights.Should().HaveCountGreaterThanOrEqualTo(2); // At least voltage mismatch and thermal risk
        result.Insights.Should().BeInDescendingOrder(i => (int)i.Severity); // Sorted by severity
    }

    [Fact]
    public async Task AnalyzeAsync_NoIssues_ReturnsEmptyInsights()
    {
        // Arrange: Valid configuration with no issues
        var compId = Guid.CreateVersion7();
        var railId = Guid.CreateVersion7();

        var entities = new List<Entity>
        {
            new() { Id = compId, Name = "LED", EntityType = "COMPONENT" },
            new() { Id = railId, Name = "3.3V Rail", EntityType = "POWER_RAIL" }
        };

        var relationships = new List<EntityRelationship>
        {
            new() { Id = Guid.CreateVersion7(), SourceEntityId = railId, TargetEntityId = compId, RelationshipType = "POWERS" }
        };

        _storeMock.Setup(s => s.GetAllEntitiesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        _storeMock.Setup(s => s.GetAllRelationshipsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var result = await _service.AnalyzeAsync();

        // Assert
        result.Insights.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeMemoryAsync_NoEntities_ReturnsEmptyResult()
    {
        // Arrange
        var memoryId = Guid.CreateVersion7();
        _storeMock.Setup(s => s.GetEntitiesForMemoryAsync(memoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entity>());

        // Act
        var result = await _service.AnalyzeMemoryAsync(memoryId);

        // Assert
        result.EntitiesAnalyzed.Should().Be(0);
        result.RelationshipsAnalyzed.Should().Be(0);
        result.Insights.Should().BeEmpty();
    }

    #endregion
}
