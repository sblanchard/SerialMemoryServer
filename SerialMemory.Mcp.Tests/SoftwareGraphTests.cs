using SerialMemory.Core.Models;
using SerialMemory.ML;
using Xunit;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Tests for the software-aware graph schema.
/// Validates entity types, relationship types, and extraction logic.
/// </summary>
public class SoftwareGraphTests
{
    #region Entity Type Validation Tests

    [Theory]
    [InlineData("PERSON", true)]
    [InlineData("ORG", true)]
    [InlineData("TEAM", true)]
    [InlineData("DATE", true)]
    [InlineData("EVENT", true)]
    [InlineData("PRODUCT", true)]
    [InlineData("GPE", true)] // Legacy type
    public void IsValidEntityType_GenericTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidEntityType(type));
    }

    [Theory]
    [InlineData("REPO", true)]
    [InlineData("PROJECT", true)]
    [InlineData("SERVICE", true)]
    [InlineData("MODULE", true)]
    [InlineData("API", true)]
    [InlineData("DATABASE", true)]
    [InlineData("TABLE", true)]
    [InlineData("MESSAGE", true)]
    [InlineData("CONFIG", true)]
    [InlineData("ENV", true)]
    [InlineData("VERSION", true)]
    [InlineData("TECH", true)]
    public void IsValidEntityType_SoftwareTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidEntityType(type));
    }

    [Theory]
    [InlineData("DEPLOYMENT", true)]
    [InlineData("INCIDENT", true)]
    [InlineData("BUG", true)]
    [InlineData("TEST", true)]
    public void IsValidEntityType_ProcessTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidEntityType(type));
    }

    [Theory]
    [InlineData("COMPONENT", true)]
    [InlineData("ASSEMBLY", true)]
    [InlineData("MATERIAL", true)]
    [InlineData("SIGNAL", true)]
    [InlineData("POWER_RAIL", true)]
    [InlineData("VOLTAGE", true)]
    [InlineData("CURRENT", true)]
    [InlineData("FREQUENCY", true)]
    [InlineData("TEMPERATURE", true)]
    [InlineData("PRESSURE", true)]
    [InlineData("FORCE", true)]
    [InlineData("TORQUE", true)]
    [InlineData("DIMENSION", true)]
    [InlineData("TOLERANCE", true)]
    [InlineData("STANDARD", true)]
    [InlineData("TOOL", true)]
    [InlineData("LOCATION", true)]
    public void IsValidEntityType_EngineeringTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidEntityType(type));
    }

    [Theory]
    [InlineData("PCB", true)]
    [InlineData("SCHEMATIC", true)]
    [InlineData("BOM", true)]
    [InlineData("PART_NUMBER", true)]
    [InlineData("DATASHEET", true)]
    [InlineData("FIRMWARE", true)]
    [InlineData("REGISTER", true)]
    [InlineData("PIN", true)]
    [InlineData("CONNECTOR", true)]
    [InlineData("PROTOCOL", true)]
    [InlineData("MANUFACTURER", true)]
    [InlineData("SENSOR", true)]
    [InlineData("ACTUATOR", true)]
    [InlineData("ENCLOSURE", true)]
    [InlineData("CABLE", true)]
    [InlineData("ANTENNA", true)]
    public void IsValidEntityType_HardwareTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidEntityType(type));
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("UNKNOWN")]
    [InlineData("FOOBAR")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidEntityType_InvalidTypes_ReturnsFalse(string? type)
    {
        Assert.False(SoftwareGraphTypes.IsValidEntityType(type));
    }

    [Theory]
    [InlineData("person", true)]
    [InlineData("Person", true)]
    [InlineData("PERSON", true)]
    [InlineData("  PERSON  ", true)]
    public void IsValidEntityType_IsCaseInsensitive(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidEntityType(type));
    }

    #endregion

    #region Relationship Type Validation Tests

    [Theory]
    [InlineData("OWNS", true)]
    [InlineData("MAINTAINS", true)]
    [InlineData("WORKS_AT", true)]
    [InlineData("WORKS_ON", true)]
    public void IsValidRelationshipType_OwnershipTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("IMPLEMENTS", true)]
    [InlineData("CALLS", true)]
    [InlineData("DEPENDS_ON", true)]
    [InlineData("USES", true)]
    [InlineData("INTEGRATES_WITH", true)]
    public void IsValidRelationshipType_DependencyTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("DEPLOYED_TO", true)]
    [InlineData("RUNS_ON", true)]
    [InlineData("EMITS", true)]
    [InlineData("CONSUMES", true)]
    [InlineData("CONFIGURED_BY", true)]
    [InlineData("TRIGGERS", true)]
    public void IsValidRelationshipType_RuntimeTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("VERSIONED_AS", true)]
    [InlineData("CAUSED", true)]
    [InlineData("FIXED_BY", true)]
    [InlineData("TESTS", true)]
    [InlineData("RELATED_TO", true)]
    public void IsValidRelationshipType_LifecycleTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("OWNED_BY", true)]
    [InlineData("CALLED_BY", true)]
    [InlineData("DEPENDENCY_OF", true)]
    [InlineData("HOSTS", true)]
    public void IsValidRelationshipType_InverseTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("CONNECTS_TO", true)]
    [InlineData("MOUNTED_ON", true)]
    [InlineData("FEEDS", true)]
    [InlineData("CONVERTS", true)]
    [InlineData("MEASURES", true)]
    [InlineData("CONTROLS", true)]
    [InlineData("CARRIES", true)]
    [InlineData("REQUIRES", true)]
    [InlineData("LIMITED_BY", true)]
    [InlineData("COMPLIES_WITH", true)]
    [InlineData("FAILS_UNDER", true)]
    [InlineData("CALIBRATED_BY", true)]
    [InlineData("PART_OF", true)]
    public void IsValidRelationshipType_PhysicalTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("POWERS", true)]
    [InlineData("GROUNDS", true)]
    [InlineData("READS_FROM", true)]
    [InlineData("WRITES_TO", true)]
    [InlineData("DRIVES", true)]
    [InlineData("CONTAINED_IN", true)]
    [InlineData("ATTACHED_TO", true)]
    [InlineData("COMPATIBLE_WITH", true)]
    [InlineData("REPLACES", true)]
    [InlineData("ALTERNATE_OF", true)]
    [InlineData("COMMUNICATES_VIA", true)]
    [InlineData("TRANSMITS", true)]
    [InlineData("RECEIVES", true)]
    [InlineData("OPERATES_AT", true)]
    [InlineData("RATED_FOR", true)]
    public void IsValidRelationshipType_AdditionalPhysicalTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("MANUFACTURED_BY", true)]
    [InlineData("SPECIFIED_IN", true)]
    [InlineData("REVISION_OF", true)]
    [InlineData("INTERFACES_WITH", true)]
    [InlineData("ROUTES_TO", true)]
    [InlineData("LOCATED_AT", true)]
    public void IsValidRelationshipType_DocumentationPhysicalTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("CONNECTED_BY", true)]
    [InlineData("MOUNTS", true)]
    [InlineData("FED_BY", true)]
    [InlineData("CONTROLLED_BY", true)]
    [InlineData("POWERED_BY", true)]
    [InlineData("GROUNDED_BY", true)]
    [InlineData("CONTAINS", true)]
    [InlineData("HAS_PART", true)]
    public void IsValidRelationshipType_PhysicalInverseTypes_ReturnsTrue(string type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidRelationshipType_InvalidTypes_ReturnsFalse(string? type)
    {
        Assert.False(SoftwareGraphTypes.IsValidRelationshipType(type));
    }

    #endregion

    #region Normalization Tests

    [Theory]
    [InlineData("person", "PERSON")]
    [InlineData("Person", "PERSON")]
    [InlineData("  PERSON  ", "PERSON")]
    [InlineData("service", "SERVICE")]
    public void NormalizeEntityType_ReturnsUppercaseTrimmed(string input, string expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.NormalizeEntityType(input));
    }

    [Theory]
    [InlineData("owns", "OWNS")]
    [InlineData("Depends_On", "DEPENDS_ON")]
    [InlineData("  CALLS  ", "CALLS")]
    public void NormalizeRelationshipType_ReturnsUppercaseTrimmed(string input, string expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.NormalizeRelationshipType(input));
    }

    [Theory]
    [InlineData("  John  Smith  ", "John Smith")]
    [InlineData("UserService", "UserService")]
    [InlineData("  Multiple   Spaces   Here  ", "Multiple Spaces Here")]
    public void NormalizeEntityName_CollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.NormalizeEntityName(input));
    }

    #endregion

    #region Entity Type Metadata Tests

    [Fact]
    public void GetEntityTypeInfo_ReturnsCorrectMetadata()
    {
        var info = SoftwareGraphTypes.GetEntityTypeInfo("SERVICE");

        Assert.NotNull(info);
        Assert.Equal("SERVICE", info.TypeName);
        Assert.Equal(EntityCategory.Software, info.Category);
        Assert.Equal("Microservice or backend service", info.Description);
        Assert.Equal("server", info.Icon);
    }

    [Fact]
    public void GetEntityTypeInfo_InvalidType_ReturnsNull()
    {
        var info = SoftwareGraphTypes.GetEntityTypeInfo("INVALID");
        Assert.Null(info);
    }

    [Fact]
    public void GetEntityTypesByCategory_Software_ReturnsExpectedTypes()
    {
        var softwareTypes = SoftwareGraphTypes.GetEntityTypesByCategory(EntityCategory.Software).ToList();

        Assert.Contains(softwareTypes, t => t.TypeName == "REPO");
        Assert.Contains(softwareTypes, t => t.TypeName == "SERVICE");
        Assert.Contains(softwareTypes, t => t.TypeName == "API");
        Assert.Contains(softwareTypes, t => t.TypeName == "DATABASE");
        Assert.Contains(softwareTypes, t => t.TypeName == "MODULE");
        Assert.True(softwareTypes.Count >= 12);
    }

    [Fact]
    public void GetEntityTypesByCategory_Engineering_ReturnsExpectedTypes()
    {
        var engineeringTypes = SoftwareGraphTypes.GetEntityTypesByCategory(EntityCategory.Engineering).ToList();

        Assert.Contains(engineeringTypes, t => t.TypeName == "COMPONENT");
        Assert.Contains(engineeringTypes, t => t.TypeName == "PCB");
        Assert.Contains(engineeringTypes, t => t.TypeName == "SENSOR");
        Assert.Contains(engineeringTypes, t => t.TypeName == "VOLTAGE");
        Assert.Contains(engineeringTypes, t => t.TypeName == "FREQUENCY");
        Assert.True(engineeringTypes.Count >= 30, $"Expected at least 30 engineering types, got {engineeringTypes.Count}");
    }

    [Fact]
    public void GetEngineeringEntityTypes_ReturnsAllEngineeringTypes()
    {
        var engineeringTypes = SoftwareGraphTypes.GetEngineeringEntityTypes().ToList();

        Assert.Contains("COMPONENT", engineeringTypes);
        Assert.Contains("ASSEMBLY", engineeringTypes);
        Assert.Contains("MATERIAL", engineeringTypes);
        Assert.Contains("SIGNAL", engineeringTypes);
        Assert.Contains("PCB", engineeringTypes);
        Assert.Contains("SENSOR", engineeringTypes);
        Assert.Contains("ACTUATOR", engineeringTypes);
        Assert.DoesNotContain("SERVICE", engineeringTypes);  // Software type
        Assert.DoesNotContain("PERSON", engineeringTypes);   // Generic type
    }

    [Fact]
    public void GetPhysicalRelationshipTypes_ReturnsAllPhysicalTypes()
    {
        var physicalTypes = SoftwareGraphTypes.GetPhysicalRelationshipTypes().ToList();

        Assert.Contains("CONNECTS_TO", physicalTypes);
        Assert.Contains("MOUNTED_ON", physicalTypes);
        Assert.Contains("POWERS", physicalTypes);
        Assert.Contains("CONTROLS", physicalTypes);
        Assert.Contains("PART_OF", physicalTypes);
        Assert.DoesNotContain("DEPENDS_ON", physicalTypes);  // Dependency type
        Assert.DoesNotContain("OWNS", physicalTypes);        // Ownership type
    }

    [Theory]
    [InlineData("COMPONENT", true)]
    [InlineData("SENSOR", true)]
    [InlineData("PCB", true)]
    [InlineData("VOLTAGE", true)]
    [InlineData("SERVICE", false)]  // Software, not engineering
    [InlineData("PERSON", false)]   // Generic, not engineering
    [InlineData("INVALID", false)]
    [InlineData(null, false)]
    public void IsEngineeringEntityType_ReturnsCorrectResult(string? type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsEngineeringEntityType(type));
    }

    [Theory]
    [InlineData("CONNECTS_TO", true)]
    [InlineData("MOUNTED_ON", true)]
    [InlineData("POWERS", true)]
    [InlineData("PART_OF", true)]
    [InlineData("DEPENDS_ON", false)]  // Dependency, not physical
    [InlineData("CALLS", false)]       // Dependency, not physical
    [InlineData("INVALID", false)]
    [InlineData(null, false)]
    public void IsPhysicalRelationshipType_ReturnsCorrectResult(string? type, bool expected)
    {
        Assert.Equal(expected, SoftwareGraphTypes.IsPhysicalRelationshipType(type));
    }

    #endregion

    #region Relationship Type Metadata Tests

    [Fact]
    public void GetRelationshipTypeInfo_ReturnsCorrectMetadata()
    {
        var info = SoftwareGraphTypes.GetRelationshipTypeInfo("DEPENDS_ON");

        Assert.NotNull(info);
        Assert.Equal("DEPENDS_ON", info.TypeName);
        Assert.Equal(RelationshipCategory.Dependency, info.Category);
        Assert.Equal("DEPENDENCY_OF", info.InverseType);
    }

    [Fact]
    public void GetInverseRelationshipType_ReturnsCorrectInverse()
    {
        Assert.Equal("OWNED_BY", SoftwareGraphTypes.GetInverseRelationshipType("OWNS"));
        Assert.Equal("CALLED_BY", SoftwareGraphTypes.GetInverseRelationshipType("CALLS"));
        Assert.Equal("HOSTS", SoftwareGraphTypes.GetInverseRelationshipType("DEPLOYED_TO"));
        Assert.Equal("RELATED_TO", SoftwareGraphTypes.GetInverseRelationshipType("RELATED_TO"));
    }

    [Fact]
    public void GetInverseRelationshipType_PhysicalTypes_ReturnsCorrectInverse()
    {
        Assert.Equal("CONNECTED_BY", SoftwareGraphTypes.GetInverseRelationshipType("CONNECTS_TO"));
        Assert.Equal("MOUNTS", SoftwareGraphTypes.GetInverseRelationshipType("MOUNTED_ON"));
        Assert.Equal("POWERED_BY", SoftwareGraphTypes.GetInverseRelationshipType("POWERS"));
        Assert.Equal("CONTROLLED_BY", SoftwareGraphTypes.GetInverseRelationshipType("CONTROLS"));
        Assert.Equal("HAS_PART", SoftwareGraphTypes.GetInverseRelationshipType("PART_OF"));
        Assert.Equal("FED_BY", SoftwareGraphTypes.GetInverseRelationshipType("FEEDS"));
    }

    [Fact]
    public void GetRelationshipTypeInfo_PhysicalType_ReturnsCorrectMetadata()
    {
        var info = SoftwareGraphTypes.GetRelationshipTypeInfo("CONNECTS_TO");

        Assert.NotNull(info);
        Assert.Equal("CONNECTS_TO", info.TypeName);
        Assert.Equal(RelationshipCategory.Physical, info.Category);
        Assert.Equal("CONNECTED_BY", info.InverseType);
    }

    [Fact]
    public void GetRelationshipTypesByCategory_Physical_ReturnsExpectedTypes()
    {
        var physicalTypes = SoftwareGraphTypes.GetRelationshipTypesByCategory(RelationshipCategory.Physical).ToList();

        Assert.Contains(physicalTypes, t => t.TypeName == "CONNECTS_TO");
        Assert.Contains(physicalTypes, t => t.TypeName == "MOUNTED_ON");
        Assert.Contains(physicalTypes, t => t.TypeName == "POWERS");
        Assert.Contains(physicalTypes, t => t.TypeName == "CONTROLS");
        Assert.True(physicalTypes.Count >= 30, $"Expected at least 30 physical relationship types, got {physicalTypes.Count}");
    }

    #endregion

    #region Validation Tests

    [Fact]
    public void ValidateEntity_ValidInput_ReturnsNoErrors()
    {
        var errors = SoftwareGraphTypes.ValidateEntity("UserService", "SERVICE");
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateEntity_EmptyName_ReturnsError()
    {
        var errors = SoftwareGraphTypes.ValidateEntity("", "SERVICE");
        Assert.Contains(errors, e => e.Contains("name is required"));
    }

    [Fact]
    public void ValidateEntity_InvalidType_ReturnsError()
    {
        var errors = SoftwareGraphTypes.ValidateEntity("MyEntity", "INVALID_TYPE");
        Assert.Contains(errors, e => e.Contains("Invalid entity type"));
    }

    [Fact]
    public void ValidateEntity_TooLongName_ReturnsError()
    {
        var longName = new string('A', 501);
        var errors = SoftwareGraphTypes.ValidateEntity(longName, "SERVICE");
        Assert.Contains(errors, e => e.Contains("exceeds maximum length"));
    }

    [Fact]
    public void ValidateRelationship_ValidInput_ReturnsNoErrors()
    {
        var errors = SoftwareGraphTypes.ValidateRelationship("DEPENDS_ON");
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateRelationship_InvalidType_ReturnsError()
    {
        var errors = SoftwareGraphTypes.ValidateRelationship("INVALID_TYPE");
        Assert.Contains(errors, e => e.Contains("Invalid relationship type"));
    }

    #endregion

    #region Software Entity Extraction Tests

    [Fact]
    public async Task ExtractEntities_ServiceNames_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The UserService and AuthenticationHandler communicate via REST API.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Text == "UserService" && e.Label == "SERVICE");
        Assert.Contains(entities, e => e.Text == "AuthenticationHandler" && e.Label == "SERVICE");
    }

    [Fact]
    public async Task ExtractEntities_Technologies_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "We use React for frontend and ASP.NET for the backend with PostgreSQL database.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Text == "React" && e.Label == "TECH");
        Assert.Contains(entities, e => e.Text == "ASP.NET" && e.Label == "TECH");
        Assert.Contains(entities, e => e.Label == "DATABASE");
    }

    [Fact]
    public async Task ExtractEntities_Versions_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "Updated to version 2.0.1 and released v3.5.0-beta.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "VERSION");
    }

    [Fact]
    public async Task ExtractEntities_Environments_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "Deploy to production environment, test in staging first.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "ENV");
    }

    [Fact]
    public async Task ExtractEntities_ConfigFiles_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "Edit the appsettings.json and docker-compose.yaml files.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Text.EndsWith(".json") && e.Label == "CONFIG");
        Assert.Contains(entities, e => e.Text.EndsWith(".yaml") && e.Label == "CONFIG");
    }

    [Fact]
    public async Task ExtractEntities_BugReferences_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "Fixed bug JIRA-1234 and closes issue #5678.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "BUG");
    }

    #endregion

    #region Software Relationship Extraction Tests

    [Fact]
    public async Task ExtractRelationships_DependsOn_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "UserService depends on AuthService for authentication.";

        var relationships = await extractor.ExtractRelationshipsAsync(text);

        Assert.Contains(relationships, r =>
            r.SourceEntity == "UserService" &&
            r.TargetEntity == "AuthService" &&
            r.RelationType == "DEPENDS_ON");
    }

    [Fact]
    public async Task ExtractRelationships_Calls_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The frontend calls PaymentAPI for processing.";

        var relationships = await extractor.ExtractRelationshipsAsync(text);

        Assert.Contains(relationships, r =>
            r.RelationType == "CALLS");
    }

    [Fact]
    public async Task ExtractRelationships_DeployedTo_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "WebApp deployed to AWS and runs on Kubernetes.";

        var relationships = await extractor.ExtractRelationshipsAsync(text);

        Assert.True(relationships.Any(r =>
            r.RelationType is "DEPLOYED_TO" or "RUNS_ON"));
    }

    #endregion

    #region Backward Compatibility Tests

    [Fact]
    public void LegacyEntityTypes_StillSupported()
    {
        // GPE (Geographic Political Entity) is a legacy NLP type
        Assert.True(SoftwareGraphTypes.IsValidEntityType("GPE"));

        var info = SoftwareGraphTypes.GetEntityTypeInfo("GPE");
        Assert.NotNull(info);
        Assert.Equal(EntityCategory.Generic, info.Category);
    }

    [Fact]
    public async Task ExtractEntities_GenericEntities_StillWork()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "John Smith works at Acme Corp in New York, NY.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        // Should still extract person names and organizations
        Assert.Contains(entities, e => e.Label == "PERSON");
        Assert.Contains(entities, e => e.Label == "ORG");
    }

    [Fact]
    public void AllValidTypes_AreInCanonicalSet()
    {
        var allEntityTypes = SoftwareGraphTypes.GetAllEntityTypes();
        var allRelationshipTypes = SoftwareGraphTypes.GetAllRelationshipTypes();

        // Verify counts match expected canonical list (expanded with engineering types)
        Assert.True(allEntityTypes.Count >= 50, $"Expected at least 50 entity types (with engineering), got {allEntityTypes.Count}");
        Assert.True(allRelationshipTypes.Count >= 60, $"Expected at least 60 relationship types (with physical), got {allRelationshipTypes.Count}");
    }

    [Fact]
    public void GetEntityTypeInfo_EngineeringType_ReturnsCorrectMetadata()
    {
        var info = SoftwareGraphTypes.GetEntityTypeInfo("COMPONENT");

        Assert.NotNull(info);
        Assert.Equal("COMPONENT", info.TypeName);
        Assert.Equal(EntityCategory.Engineering, info.Category);
        Assert.Equal("Physical component", info.Description);
    }

    [Fact]
    public void GetEntityTypeInfo_SensorType_ReturnsCorrectMetadata()
    {
        var info = SoftwareGraphTypes.GetEntityTypeInfo("SENSOR");

        Assert.NotNull(info);
        Assert.Equal("SENSOR", info.TypeName);
        Assert.Equal(EntityCategory.Engineering, info.Category);
        Assert.Equal("Sensor device", info.Description);
        Assert.Equal("eye", info.Icon);
    }

    [Fact]
    public void GetEntityTypeInfo_PCBType_ReturnsCorrectMetadata()
    {
        var info = SoftwareGraphTypes.GetEntityTypeInfo("PCB");

        Assert.NotNull(info);
        Assert.Equal("PCB", info.TypeName);
        Assert.Equal(EntityCategory.Engineering, info.Category);
        Assert.Equal("Printed circuit board", info.Description);
    }

    [Fact]
    public void ValidateEntity_EngineeringType_ReturnsNoErrors()
    {
        var errors = SoftwareGraphTypes.ValidateEntity("LM358 Op-Amp", "COMPONENT");
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateEntity_SensorType_ReturnsNoErrors()
    {
        var errors = SoftwareGraphTypes.ValidateEntity("BME280 Temperature Sensor", "SENSOR");
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateRelationship_PhysicalType_ReturnsNoErrors()
    {
        var errors = SoftwareGraphTypes.ValidateRelationship("CONNECTS_TO");
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateRelationship_MountedOnType_ReturnsNoErrors()
    {
        var errors = SoftwareGraphTypes.ValidateRelationship("MOUNTED_ON");
        Assert.Empty(errors);
    }

    #endregion

    #region Hardware Entity Extraction Tests

    [Fact]
    public async Task ExtractEntities_PartNumbers_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The STM32F407VGT6 microcontroller communicates with the ESP32-WROOM-32 module via SPI.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "PART_NUMBER");
    }

    [Fact]
    public async Task ExtractEntities_Protocols_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The sensor uses I2C for configuration and SPI for data transfer. UART is used for debugging.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "PROTOCOL" && e.Text == "I2C");
        Assert.Contains(entities, e => e.Label == "PROTOCOL" && e.Text == "SPI");
        Assert.Contains(entities, e => e.Label == "PROTOCOL" && e.Text == "UART");
    }

    [Fact]
    public async Task ExtractEntities_VoltagesAndCurrents_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The regulator outputs 3.3V at up to 500mA. Input can be 5V to 12V.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "VOLTAGE");
        Assert.Contains(entities, e => e.Label == "CURRENT");
    }

    [Fact]
    public async Task ExtractEntities_Frequencies_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The MCU runs at 168MHz with a 16MHz external crystal.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "FREQUENCY");
    }

    [Fact]
    public async Task ExtractEntities_Components_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "Add a 10k resistor and 100nF capacitor to the power supply.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "COMPONENT");
    }

    [Fact]
    public async Task ExtractEntities_Sensors_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The BME280 is a temperature sensor that also measures humidity and pressure.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "SENSOR");
    }

    [Fact]
    public async Task ExtractEntities_Standards_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The board is RoHS compliant and meets IPC-2221 standards.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "STANDARD" && e.Text.Contains("ROHS"));
    }

    [Fact]
    public async Task ExtractEntities_Manufacturers_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "Texas Instruments makes excellent op-amps. STMicroelectronics makes great MCUs.";

        var entities = await extractor.ExtractEntitiesAsync(text);

        Assert.Contains(entities, e => e.Label == "MANUFACTURER");
    }

    #endregion

    #region Hardware Relationship Extraction Tests

    [Fact]
    public async Task ExtractRelationships_ConnectsTo_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The sensor connects to the MCU via the I2C bus.";

        var relationships = await extractor.ExtractRelationshipsAsync(text);

        Assert.Contains(relationships, r => r.RelationType == "CONNECTS_TO");
    }

    [Fact]
    public async Task ExtractRelationships_MountedOn_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The capacitor is mounted on the main PCB near the power input.";

        var relationships = await extractor.ExtractRelationshipsAsync(text);

        Assert.Contains(relationships, r => r.RelationType == "MOUNTED_ON");
    }

    [Fact]
    public async Task ExtractRelationships_Controls_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The MCU controls the motor driver through PWM signals.";

        var relationships = await extractor.ExtractRelationshipsAsync(text);

        Assert.Contains(relationships, r => r.RelationType == "CONTROLS");
    }

    [Fact]
    public async Task ExtractRelationships_CommunicatesVia_ExtractsCorrectly()
    {
        var extractor = new SoftwareEntityExtractionService();
        var text = "The ESP32 communicates via WiFi with the cloud server.";

        var relationships = await extractor.ExtractRelationshipsAsync(text);

        Assert.Contains(relationships, r => r.RelationType == "COMMUNICATES_VIA");
    }

    #endregion

    #region Custom Type Registration Tests

    [Fact]
    public void RegisterCustomEntityType_AddsNewType()
    {
        SoftwareGraphTypes.RegisterCustomEntityType("CUSTOM_TYPE", "A custom entity type for testing");

        Assert.True(SoftwareGraphTypes.IsValidEntityType("CUSTOM_TYPE"));

        var info = SoftwareGraphTypes.GetEntityTypeInfo("CUSTOM_TYPE");
        Assert.NotNull(info);
        Assert.Equal(EntityCategory.Custom, info.Category);
    }

    [Fact]
    public void RegisterCustomRelationshipType_AddsNewType()
    {
        SoftwareGraphTypes.RegisterCustomRelationshipType("CUSTOM_REL", "A custom relationship", "CUSTOM_REL_INVERSE");

        Assert.True(SoftwareGraphTypes.IsValidRelationshipType("CUSTOM_REL"));
        Assert.Equal("CUSTOM_REL_INVERSE", SoftwareGraphTypes.GetInverseRelationshipType("CUSTOM_REL"));
    }

    #endregion
}
