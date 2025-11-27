using System.Text.RegularExpressions;

namespace SerialMemory.Core.Models;

/// <summary>
/// Canonical entity types for the knowledge graph.
/// Includes both generic NLP types and software engineering types.
/// </summary>
public enum EntityTypeEnum
{
    // Generic NLP types
    PERSON,
    ORG,
    TEAM,
    DATE,
    EVENT,
    PRODUCT,

    // Software engineering types
    REPO,
    PROJECT,
    SERVICE,
    MODULE,
    API,
    DATABASE,
    TABLE,
    MESSAGE,
    CONFIG,
    ENV,
    VERSION,
    TECH,

    // Infrastructure types
    DEPLOYMENT,
    INCIDENT,

    // Process types
    BUG,
    TEST,

    // Legacy/custom (for backward compatibility)
    GPE,      // Geographic/Political Entity (legacy NLP type)
    CUSTOM    // User-defined types
}

/// <summary>
/// Canonical relationship types for the knowledge graph.
/// </summary>
public enum RelationshipTypeEnum
{
    // Ownership/responsibility
    OWNS,
    MAINTAINS,
    WORKS_AT,
    WORKS_ON,

    // Software dependencies
    IMPLEMENTS,
    CALLS,
    DEPENDS_ON,
    USES,
    INTEGRATES_WITH,

    // Runtime/infrastructure
    DEPLOYED_TO,
    RUNS_ON,
    EMITS,
    CONSUMES,
    CONFIGURED_BY,
    TRIGGERS,

    // Lifecycle
    VERSIONED_AS,
    CAUSED,
    FIXED_BY,
    TESTS,
    RELATED_TO,

    // Legacy inverse types (for compatibility)
    OWNED_BY,
    MAINTAINED_BY,
    EMPLOYS,
    WORKED_ON_BY,
    IMPLEMENTED_BY,
    CALLED_BY,
    DEPENDENCY_OF,
    USED_BY,
    INTEGRATED_BY,
    HOSTS,
    RUNS,
    EMITTED_BY,
    CONSUMED_BY,
    CONFIGURES,
    TRIGGERED_BY,
    VERSION_OF,
    CAUSED_BY,
    FIXES,
    TESTED_BY,

    // Custom
    CUSTOM
}

/// <summary>
/// Category groupings for entity types.
/// </summary>
public enum EntityCategory
{
    Generic,
    Software,
    Infrastructure,
    Process,
    Engineering,  // Physical/hardware engineering
    Custom
}

/// <summary>
/// Category groupings for relationship types.
/// </summary>
public enum RelationshipCategory
{
    Ownership,
    Dependency,
    Runtime,
    Lifecycle,
    Physical,     // Physical engineering connections
    Custom
}

/// <summary>
/// Metadata for an entity type.
/// </summary>
public sealed class EntityTypeInfo
{
    public required string TypeName { get; init; }
    public required EntityCategory Category { get; init; }
    public required string Description { get; init; }
    public string? Icon { get; init; }
}

/// <summary>
/// Metadata for a relationship type.
/// </summary>
public sealed class RelationshipTypeInfo
{
    public required string TypeName { get; init; }
    public required RelationshipCategory Category { get; init; }
    public required string Description { get; init; }
    public string? InverseType { get; init; }
}

/// <summary>
/// Provides validation and normalization for entity and relationship types.
/// </summary>
public static partial class SoftwareGraphTypes
{
    private static readonly HashSet<string> ValidEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic
        "PERSON", "ORG", "TEAM", "DATE", "EVENT", "PRODUCT",
        // Software
        "REPO", "PROJECT", "SERVICE", "MODULE", "API", "DATABASE",
        "TABLE", "MESSAGE", "CONFIG", "ENV", "VERSION", "TECH",
        // Infrastructure
        "DEPLOYMENT", "INCIDENT",
        // Process
        "BUG", "TEST",
        // Engineering (physical/hardware)
        "COMPONENT", "ASSEMBLY", "MATERIAL", "SIGNAL", "POWER_RAIL",
        "VOLTAGE", "CURRENT", "FREQUENCY", "TEMPERATURE", "PRESSURE",
        "FORCE", "TORQUE", "DIMENSION", "TOLERANCE", "STANDARD", "TOOL", "LOCATION",
        "PCB", "SCHEMATIC", "BOM", "PART_NUMBER", "DATASHEET", "FIRMWARE",
        "REGISTER", "PIN", "CONNECTOR", "PROTOCOL", "MANUFACTURER",
        "SENSOR", "ACTUATOR", "ENCLOSURE", "CABLE", "ANTENNA",
        // Legacy
        "GPE"
    };

    private static readonly HashSet<string> ValidRelationshipTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ownership
        "OWNS", "MAINTAINS", "WORKS_AT", "WORKS_ON",
        // Dependencies
        "IMPLEMENTS", "CALLS", "DEPENDS_ON", "USES", "INTEGRATES_WITH",
        // Runtime
        "DEPLOYED_TO", "RUNS_ON", "EMITS", "CONSUMES", "CONFIGURED_BY", "TRIGGERS",
        // Lifecycle
        "VERSIONED_AS", "CAUSED", "FIXED_BY", "TESTS", "RELATED_TO",
        // Physical/engineering connections
        "CONNECTS_TO", "MOUNTED_ON", "FEEDS", "CONVERTS", "MEASURES",
        "CONTROLS", "CARRIES", "REQUIRES", "LIMITED_BY", "COMPLIES_WITH",
        "FAILS_UNDER", "CALIBRATED_BY", "PART_OF",
        "POWERS", "GROUNDS", "READS_FROM", "WRITES_TO", "DRIVES",
        "CONTAINED_IN", "ATTACHED_TO", "COMPATIBLE_WITH", "REPLACES", "ALTERNATE_OF",
        "COMMUNICATES_VIA", "TRANSMITS", "RECEIVES", "OPERATES_AT", "RATED_FOR",
        "MANUFACTURED_BY", "SPECIFIED_IN", "REVISION_OF", "INTERFACES_WITH",
        "ROUTES_TO", "LOCATED_AT",
        // Inverse types (software)
        "OWNED_BY", "MAINTAINED_BY", "EMPLOYS", "WORKED_ON_BY",
        "IMPLEMENTED_BY", "CALLED_BY", "DEPENDENCY_OF", "USED_BY", "INTEGRATED_BY",
        "HOSTS", "RUNS", "EMITTED_BY", "CONSUMED_BY", "CONFIGURES", "TRIGGERED_BY",
        "VERSION_OF", "CAUSED_BY", "FIXES", "TESTED_BY",
        // Inverse types (physical)
        "CONNECTED_BY", "MOUNTS", "FED_BY", "CONVERTED_BY", "MEASURED_BY",
        "CONTROLLED_BY", "CARRIED_BY", "REQUIRED_BY", "LIMITS", "COMPLIANCE_FOR",
        "POWERED_BY", "GROUNDED_BY", "READ_BY", "WRITTEN_BY", "DRIVEN_BY",
        "CONTAINS", "HAS_ATTACHED", "REPLACED_BY", "HAS_ALTERNATE",
        "USED_BY_COMM", "OPERATING_SPEC_FOR", "RATING_OF", "MANUFACTURES",
        "SPECIFIES", "HAS_REVISION", "HAS_PART", "INTERFACED_BY", "ROUTED_FROM", "LOCATION_OF"
    };

    private static readonly Dictionary<string, EntityTypeInfo> EntityTypeMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic
        ["PERSON"] = new() { TypeName = "PERSON", Category = EntityCategory.Generic, Description = "Individual human being", Icon = "user" },
        ["ORG"] = new() { TypeName = "ORG", Category = EntityCategory.Generic, Description = "Organization or company", Icon = "building" },
        ["TEAM"] = new() { TypeName = "TEAM", Category = EntityCategory.Generic, Description = "Team within an organization", Icon = "users" },
        ["DATE"] = new() { TypeName = "DATE", Category = EntityCategory.Generic, Description = "Date or time reference", Icon = "calendar" },
        ["EVENT"] = new() { TypeName = "EVENT", Category = EntityCategory.Generic, Description = "Event or occurrence", Icon = "calendar-check" },
        ["PRODUCT"] = new() { TypeName = "PRODUCT", Category = EntityCategory.Generic, Description = "Product or offering", Icon = "package" },
        ["GPE"] = new() { TypeName = "GPE", Category = EntityCategory.Generic, Description = "Geographic/Political Entity", Icon = "map-pin" },

        // Software
        ["REPO"] = new() { TypeName = "REPO", Category = EntityCategory.Software, Description = "Code repository", Icon = "git-branch" },
        ["PROJECT"] = new() { TypeName = "PROJECT", Category = EntityCategory.Software, Description = "Software project", Icon = "folder" },
        ["SERVICE"] = new() { TypeName = "SERVICE", Category = EntityCategory.Software, Description = "Microservice or backend service", Icon = "server" },
        ["MODULE"] = new() { TypeName = "MODULE", Category = EntityCategory.Software, Description = "Code module, package, or library", Icon = "box" },
        ["API"] = new() { TypeName = "API", Category = EntityCategory.Software, Description = "API endpoint or interface", Icon = "plug" },
        ["DATABASE"] = new() { TypeName = "DATABASE", Category = EntityCategory.Software, Description = "Database instance or cluster", Icon = "database" },
        ["TABLE"] = new() { TypeName = "TABLE", Category = EntityCategory.Software, Description = "Database table or collection", Icon = "table" },
        ["MESSAGE"] = new() { TypeName = "MESSAGE", Category = EntityCategory.Software, Description = "Message queue topic or event", Icon = "message-square" },
        ["CONFIG"] = new() { TypeName = "CONFIG", Category = EntityCategory.Software, Description = "Configuration file or setting", Icon = "settings" },
        ["ENV"] = new() { TypeName = "ENV", Category = EntityCategory.Software, Description = "Environment (dev, staging, prod)", Icon = "cloud" },
        ["VERSION"] = new() { TypeName = "VERSION", Category = EntityCategory.Software, Description = "Version or release tag", Icon = "tag" },
        ["TECH"] = new() { TypeName = "TECH", Category = EntityCategory.Software, Description = "Technology, language, or framework", Icon = "cpu" },

        // Infrastructure
        ["DEPLOYMENT"] = new() { TypeName = "DEPLOYMENT", Category = EntityCategory.Infrastructure, Description = "Deployment or release", Icon = "upload-cloud" },
        ["INCIDENT"] = new() { TypeName = "INCIDENT", Category = EntityCategory.Infrastructure, Description = "Incident or outage", Icon = "alert-triangle" },

        // Process
        ["BUG"] = new() { TypeName = "BUG", Category = EntityCategory.Process, Description = "Bug or defect", Icon = "bug" },
        ["TEST"] = new() { TypeName = "TEST", Category = EntityCategory.Process, Description = "Test case or suite", Icon = "check-square" },

        // Engineering (physical/hardware)
        ["COMPONENT"] = new() { TypeName = "COMPONENT", Category = EntityCategory.Engineering, Description = "Physical component", Icon = "cpu" },
        ["ASSEMBLY"] = new() { TypeName = "ASSEMBLY", Category = EntityCategory.Engineering, Description = "Assembly of components", Icon = "layers" },
        ["MATERIAL"] = new() { TypeName = "MATERIAL", Category = EntityCategory.Engineering, Description = "Material specification", Icon = "box" },
        ["SIGNAL"] = new() { TypeName = "SIGNAL", Category = EntityCategory.Engineering, Description = "Electrical/data signal", Icon = "activity" },
        ["POWER_RAIL"] = new() { TypeName = "POWER_RAIL", Category = EntityCategory.Engineering, Description = "Power supply rail", Icon = "zap" },
        ["VOLTAGE"] = new() { TypeName = "VOLTAGE", Category = EntityCategory.Engineering, Description = "Voltage specification", Icon = "zap" },
        ["CURRENT"] = new() { TypeName = "CURRENT", Category = EntityCategory.Engineering, Description = "Current specification", Icon = "zap" },
        ["FREQUENCY"] = new() { TypeName = "FREQUENCY", Category = EntityCategory.Engineering, Description = "Frequency specification", Icon = "radio" },
        ["TEMPERATURE"] = new() { TypeName = "TEMPERATURE", Category = EntityCategory.Engineering, Description = "Temperature specification", Icon = "thermometer" },
        ["PRESSURE"] = new() { TypeName = "PRESSURE", Category = EntityCategory.Engineering, Description = "Pressure specification", Icon = "gauge" },
        ["FORCE"] = new() { TypeName = "FORCE", Category = EntityCategory.Engineering, Description = "Force specification", Icon = "move" },
        ["TORQUE"] = new() { TypeName = "TORQUE", Category = EntityCategory.Engineering, Description = "Torque specification", Icon = "rotate-cw" },
        ["DIMENSION"] = new() { TypeName = "DIMENSION", Category = EntityCategory.Engineering, Description = "Physical dimension", Icon = "maximize" },
        ["TOLERANCE"] = new() { TypeName = "TOLERANCE", Category = EntityCategory.Engineering, Description = "Tolerance specification", Icon = "sliders" },
        ["STANDARD"] = new() { TypeName = "STANDARD", Category = EntityCategory.Engineering, Description = "Engineering standard", Icon = "file-text" },
        ["TOOL"] = new() { TypeName = "TOOL", Category = EntityCategory.Engineering, Description = "Tool or equipment", Icon = "tool" },
        ["LOCATION"] = new() { TypeName = "LOCATION", Category = EntityCategory.Engineering, Description = "Physical location", Icon = "map-pin" },
        ["PCB"] = new() { TypeName = "PCB", Category = EntityCategory.Engineering, Description = "Printed circuit board", Icon = "grid" },
        ["SCHEMATIC"] = new() { TypeName = "SCHEMATIC", Category = EntityCategory.Engineering, Description = "Schematic diagram", Icon = "file" },
        ["BOM"] = new() { TypeName = "BOM", Category = EntityCategory.Engineering, Description = "Bill of materials", Icon = "list" },
        ["PART_NUMBER"] = new() { TypeName = "PART_NUMBER", Category = EntityCategory.Engineering, Description = "Part number/SKU", Icon = "hash" },
        ["DATASHEET"] = new() { TypeName = "DATASHEET", Category = EntityCategory.Engineering, Description = "Component datasheet", Icon = "file-text" },
        ["FIRMWARE"] = new() { TypeName = "FIRMWARE", Category = EntityCategory.Engineering, Description = "Firmware/embedded software", Icon = "hard-drive" },
        ["REGISTER"] = new() { TypeName = "REGISTER", Category = EntityCategory.Engineering, Description = "Hardware register", Icon = "database" },
        ["PIN"] = new() { TypeName = "PIN", Category = EntityCategory.Engineering, Description = "Pin/pinout specification", Icon = "circle" },
        ["CONNECTOR"] = new() { TypeName = "CONNECTOR", Category = EntityCategory.Engineering, Description = "Physical connector", Icon = "link" },
        ["PROTOCOL"] = new() { TypeName = "PROTOCOL", Category = EntityCategory.Engineering, Description = "Communication protocol", Icon = "wifi" },
        ["MANUFACTURER"] = new() { TypeName = "MANUFACTURER", Category = EntityCategory.Engineering, Description = "Component manufacturer", Icon = "briefcase" },
        ["SENSOR"] = new() { TypeName = "SENSOR", Category = EntityCategory.Engineering, Description = "Sensor device", Icon = "eye" },
        ["ACTUATOR"] = new() { TypeName = "ACTUATOR", Category = EntityCategory.Engineering, Description = "Actuator device", Icon = "settings" },
        ["ENCLOSURE"] = new() { TypeName = "ENCLOSURE", Category = EntityCategory.Engineering, Description = "Physical enclosure", Icon = "box" },
        ["CABLE"] = new() { TypeName = "CABLE", Category = EntityCategory.Engineering, Description = "Cable/wire harness", Icon = "link-2" },
        ["ANTENNA"] = new() { TypeName = "ANTENNA", Category = EntityCategory.Engineering, Description = "RF antenna", Icon = "radio" }
    };

    private static readonly Dictionary<string, RelationshipTypeInfo> RelationshipTypeMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ownership
        ["OWNS"] = new() { TypeName = "OWNS", Category = RelationshipCategory.Ownership, Description = "Entity owns another", InverseType = "OWNED_BY" },
        ["MAINTAINS"] = new() { TypeName = "MAINTAINS", Category = RelationshipCategory.Ownership, Description = "Entity maintains another", InverseType = "MAINTAINED_BY" },
        ["WORKS_AT"] = new() { TypeName = "WORKS_AT", Category = RelationshipCategory.Ownership, Description = "Person works at organization", InverseType = "EMPLOYS" },
        ["WORKS_ON"] = new() { TypeName = "WORKS_ON", Category = RelationshipCategory.Ownership, Description = "Person works on project", InverseType = "WORKED_ON_BY" },

        // Dependencies
        ["IMPLEMENTS"] = new() { TypeName = "IMPLEMENTS", Category = RelationshipCategory.Dependency, Description = "Implements interface", InverseType = "IMPLEMENTED_BY" },
        ["CALLS"] = new() { TypeName = "CALLS", Category = RelationshipCategory.Dependency, Description = "Calls another service", InverseType = "CALLED_BY" },
        ["DEPENDS_ON"] = new() { TypeName = "DEPENDS_ON", Category = RelationshipCategory.Dependency, Description = "Depends on another", InverseType = "DEPENDENCY_OF" },
        ["USES"] = new() { TypeName = "USES", Category = RelationshipCategory.Dependency, Description = "Uses technology", InverseType = "USED_BY" },
        ["INTEGRATES_WITH"] = new() { TypeName = "INTEGRATES_WITH", Category = RelationshipCategory.Dependency, Description = "Integrates with another", InverseType = "INTEGRATED_BY" },

        // Runtime
        ["DEPLOYED_TO"] = new() { TypeName = "DEPLOYED_TO", Category = RelationshipCategory.Runtime, Description = "Deployed to environment", InverseType = "HOSTS" },
        ["RUNS_ON"] = new() { TypeName = "RUNS_ON", Category = RelationshipCategory.Runtime, Description = "Runs on infrastructure", InverseType = "RUNS" },
        ["EMITS"] = new() { TypeName = "EMITS", Category = RelationshipCategory.Runtime, Description = "Emits events", InverseType = "EMITTED_BY" },
        ["CONSUMES"] = new() { TypeName = "CONSUMES", Category = RelationshipCategory.Runtime, Description = "Consumes events", InverseType = "CONSUMED_BY" },
        ["CONFIGURED_BY"] = new() { TypeName = "CONFIGURED_BY", Category = RelationshipCategory.Runtime, Description = "Configured by config", InverseType = "CONFIGURES" },
        ["TRIGGERS"] = new() { TypeName = "TRIGGERS", Category = RelationshipCategory.Runtime, Description = "Triggers another", InverseType = "TRIGGERED_BY" },

        // Lifecycle
        ["VERSIONED_AS"] = new() { TypeName = "VERSIONED_AS", Category = RelationshipCategory.Lifecycle, Description = "Has version", InverseType = "VERSION_OF" },
        ["CAUSED"] = new() { TypeName = "CAUSED", Category = RelationshipCategory.Lifecycle, Description = "Caused incident", InverseType = "CAUSED_BY" },
        ["FIXED_BY"] = new() { TypeName = "FIXED_BY", Category = RelationshipCategory.Lifecycle, Description = "Fixed by commit", InverseType = "FIXES" },
        ["TESTS"] = new() { TypeName = "TESTS", Category = RelationshipCategory.Lifecycle, Description = "Tests entity", InverseType = "TESTED_BY" },
        ["RELATED_TO"] = new() { TypeName = "RELATED_TO", Category = RelationshipCategory.Lifecycle, Description = "Related to another", InverseType = "RELATED_TO" },

        // Physical/Engineering connections
        ["CONNECTS_TO"] = new() { TypeName = "CONNECTS_TO", Category = RelationshipCategory.Physical, Description = "Physical connection", InverseType = "CONNECTED_BY" },
        ["MOUNTED_ON"] = new() { TypeName = "MOUNTED_ON", Category = RelationshipCategory.Physical, Description = "Mounted on", InverseType = "MOUNTS" },
        ["FEEDS"] = new() { TypeName = "FEEDS", Category = RelationshipCategory.Physical, Description = "Feeds/supplies", InverseType = "FED_BY" },
        ["CONVERTS"] = new() { TypeName = "CONVERTS", Category = RelationshipCategory.Physical, Description = "Converts signal/power", InverseType = "CONVERTED_BY" },
        ["MEASURES"] = new() { TypeName = "MEASURES", Category = RelationshipCategory.Physical, Description = "Measures quantity", InverseType = "MEASURED_BY" },
        ["CONTROLS"] = new() { TypeName = "CONTROLS", Category = RelationshipCategory.Physical, Description = "Controls device", InverseType = "CONTROLLED_BY" },
        ["CARRIES"] = new() { TypeName = "CARRIES", Category = RelationshipCategory.Physical, Description = "Carries signal/current", InverseType = "CARRIED_BY" },
        ["REQUIRES"] = new() { TypeName = "REQUIRES", Category = RelationshipCategory.Physical, Description = "Requires specification", InverseType = "REQUIRED_BY" },
        ["LIMITED_BY"] = new() { TypeName = "LIMITED_BY", Category = RelationshipCategory.Physical, Description = "Limited by constraint", InverseType = "LIMITS" },
        ["COMPLIES_WITH"] = new() { TypeName = "COMPLIES_WITH", Category = RelationshipCategory.Physical, Description = "Complies with standard", InverseType = "COMPLIANCE_FOR" },
        ["FAILS_UNDER"] = new() { TypeName = "FAILS_UNDER", Category = RelationshipCategory.Physical, Description = "Fails under condition", InverseType = "FAILURE_CAUSE_FOR" },
        ["CALIBRATED_BY"] = new() { TypeName = "CALIBRATED_BY", Category = RelationshipCategory.Physical, Description = "Calibrated by tool/process", InverseType = "CALIBRATES" },
        ["PART_OF"] = new() { TypeName = "PART_OF", Category = RelationshipCategory.Physical, Description = "Part of assembly", InverseType = "HAS_PART" },
        ["POWERS"] = new() { TypeName = "POWERS", Category = RelationshipCategory.Physical, Description = "Provides power", InverseType = "POWERED_BY" },
        ["GROUNDS"] = new() { TypeName = "GROUNDS", Category = RelationshipCategory.Physical, Description = "Provides ground", InverseType = "GROUNDED_BY" },
        ["READS_FROM"] = new() { TypeName = "READS_FROM", Category = RelationshipCategory.Physical, Description = "Reads from", InverseType = "READ_BY" },
        ["WRITES_TO"] = new() { TypeName = "WRITES_TO", Category = RelationshipCategory.Physical, Description = "Writes to", InverseType = "WRITTEN_BY" },
        ["DRIVES"] = new() { TypeName = "DRIVES", Category = RelationshipCategory.Physical, Description = "Drives actuator", InverseType = "DRIVEN_BY" },
        ["CONTAINED_IN"] = new() { TypeName = "CONTAINED_IN", Category = RelationshipCategory.Physical, Description = "Contained in", InverseType = "CONTAINS" },
        ["ATTACHED_TO"] = new() { TypeName = "ATTACHED_TO", Category = RelationshipCategory.Physical, Description = "Attached to", InverseType = "HAS_ATTACHED" },
        ["COMPATIBLE_WITH"] = new() { TypeName = "COMPATIBLE_WITH", Category = RelationshipCategory.Physical, Description = "Compatible with", InverseType = "COMPATIBLE_WITH" },
        ["REPLACES"] = new() { TypeName = "REPLACES", Category = RelationshipCategory.Physical, Description = "Replaces part", InverseType = "REPLACED_BY" },
        ["ALTERNATE_OF"] = new() { TypeName = "ALTERNATE_OF", Category = RelationshipCategory.Physical, Description = "Alternate part", InverseType = "HAS_ALTERNATE" },
        ["COMMUNICATES_VIA"] = new() { TypeName = "COMMUNICATES_VIA", Category = RelationshipCategory.Physical, Description = "Communicates via protocol", InverseType = "USED_BY_COMM" },
        ["TRANSMITS"] = new() { TypeName = "TRANSMITS", Category = RelationshipCategory.Physical, Description = "Transmits signal", InverseType = "RECEIVES" },
        ["RECEIVES"] = new() { TypeName = "RECEIVES", Category = RelationshipCategory.Physical, Description = "Receives signal", InverseType = "TRANSMITS" },
        ["OPERATES_AT"] = new() { TypeName = "OPERATES_AT", Category = RelationshipCategory.Physical, Description = "Operates at spec", InverseType = "OPERATING_SPEC_FOR" },
        ["RATED_FOR"] = new() { TypeName = "RATED_FOR", Category = RelationshipCategory.Physical, Description = "Rated for spec", InverseType = "RATING_OF" },
        ["MANUFACTURED_BY"] = new() { TypeName = "MANUFACTURED_BY", Category = RelationshipCategory.Physical, Description = "Made by manufacturer", InverseType = "MANUFACTURES" },
        ["SPECIFIED_IN"] = new() { TypeName = "SPECIFIED_IN", Category = RelationshipCategory.Physical, Description = "Specified in doc", InverseType = "SPECIFIES" },
        ["REVISION_OF"] = new() { TypeName = "REVISION_OF", Category = RelationshipCategory.Physical, Description = "Revision of design", InverseType = "HAS_REVISION" },
        ["INTERFACES_WITH"] = new() { TypeName = "INTERFACES_WITH", Category = RelationshipCategory.Physical, Description = "Interfaces with", InverseType = "INTERFACED_BY" },
        ["ROUTES_TO"] = new() { TypeName = "ROUTES_TO", Category = RelationshipCategory.Physical, Description = "Routes to", InverseType = "ROUTED_FROM" },
        ["LOCATED_AT"] = new() { TypeName = "LOCATED_AT", Category = RelationshipCategory.Physical, Description = "Located at position", InverseType = "LOCATION_OF" }
    };

    // Regex for normalizing whitespace
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Validates if a string is a known entity type.
    /// </summary>
    public static bool IsValidEntityType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return ValidEntityTypes.Contains(type.Trim());
    }

    /// <summary>
    /// Validates if a string is a known relationship type.
    /// </summary>
    public static bool IsValidRelationshipType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return ValidRelationshipTypes.Contains(type.Trim());
    }

    /// <summary>
    /// Normalizes an entity type to uppercase.
    /// </summary>
    public static string NormalizeEntityType(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return type.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Normalizes a relationship type to uppercase.
    /// </summary>
    public static string NormalizeRelationshipType(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return type.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Normalizes an entity name (trim, collapse whitespace).
    /// </summary>
    public static string NormalizeEntityName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return WhitespaceRegex().Replace(name.Trim(), " ");
    }

    /// <summary>
    /// Gets metadata for an entity type.
    /// </summary>
    public static EntityTypeInfo? GetEntityTypeInfo(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        return EntityTypeMetadata.GetValueOrDefault(type.Trim());
    }

    /// <summary>
    /// Gets metadata for a relationship type.
    /// </summary>
    public static RelationshipTypeInfo? GetRelationshipTypeInfo(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        return RelationshipTypeMetadata.GetValueOrDefault(type.Trim());
    }

    /// <summary>
    /// Gets the inverse relationship type (e.g., OWNS -> OWNED_BY).
    /// </summary>
    public static string? GetInverseRelationshipType(string type)
    {
        var info = GetRelationshipTypeInfo(type);
        return info?.InverseType;
    }

    /// <summary>
    /// Gets all valid entity types.
    /// </summary>
    public static IReadOnlySet<string> GetAllEntityTypes() => ValidEntityTypes;

    /// <summary>
    /// Gets all valid relationship types.
    /// </summary>
    public static IReadOnlySet<string> GetAllRelationshipTypes() => ValidRelationshipTypes;

    /// <summary>
    /// Gets entity types by category.
    /// </summary>
    public static IEnumerable<EntityTypeInfo> GetEntityTypesByCategory(EntityCategory category)
    {
        return EntityTypeMetadata.Values.Where(t => t.Category == category);
    }

    /// <summary>
    /// Gets relationship types by category.
    /// </summary>
    public static IEnumerable<RelationshipTypeInfo> GetRelationshipTypesByCategory(RelationshipCategory category)
    {
        return RelationshipTypeMetadata.Values.Where(t => t.Category == category);
    }

    /// <summary>
    /// Gets all software entity types.
    /// </summary>
    public static IEnumerable<string> GetSoftwareEntityTypes()
    {
        return GetEntityTypesByCategory(EntityCategory.Software).Select(t => t.TypeName);
    }

    /// <summary>
    /// Gets all engineering (physical/hardware) entity types.
    /// </summary>
    public static IEnumerable<string> GetEngineeringEntityTypes()
    {
        return GetEntityTypesByCategory(EntityCategory.Engineering).Select(t => t.TypeName);
    }

    /// <summary>
    /// Gets all physical relationship types.
    /// </summary>
    public static IEnumerable<string> GetPhysicalRelationshipTypes()
    {
        return GetRelationshipTypesByCategory(RelationshipCategory.Physical).Select(t => t.TypeName);
    }

    /// <summary>
    /// Checks if an entity type is an engineering/physical type.
    /// </summary>
    public static bool IsEngineeringEntityType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        var info = GetEntityTypeInfo(type);
        return info?.Category == EntityCategory.Engineering;
    }

    /// <summary>
    /// Checks if a relationship type is a physical connection type.
    /// </summary>
    public static bool IsPhysicalRelationshipType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        var info = GetRelationshipTypeInfo(type);
        return info?.Category == RelationshipCategory.Physical;
    }

    /// <summary>
    /// Validates an entity and returns any errors.
    /// </summary>
    public static List<string> ValidateEntity(string? name, string? entityType)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(name))
            errors.Add("Entity name is required");
        else if (name.Length > 500)
            errors.Add("Entity name exceeds maximum length of 500 characters");

        if (string.IsNullOrWhiteSpace(entityType))
            errors.Add("Entity type is required");
        else if (!IsValidEntityType(entityType))
            errors.Add($"Invalid entity type '{entityType}'. Valid types: {string.Join(", ", ValidEntityTypes.OrderBy(t => t))}");

        return errors;
    }

    /// <summary>
    /// Validates a relationship and returns any errors.
    /// </summary>
    public static List<string> ValidateRelationship(string? relationshipType)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(relationshipType))
            errors.Add("Relationship type is required");
        else if (!IsValidRelationshipType(relationshipType))
            errors.Add($"Invalid relationship type '{relationshipType}'. Valid types: {string.Join(", ", ValidRelationshipTypes.OrderBy(t => t))}");

        return errors;
    }

    /// <summary>
    /// Adds a custom entity type at runtime.
    /// </summary>
    public static void RegisterCustomEntityType(string typeName, string description)
    {
        var normalized = NormalizeEntityType(typeName);
        ValidEntityTypes.Add(normalized);
        EntityTypeMetadata[normalized] = new EntityTypeInfo
        {
            TypeName = normalized,
            Category = EntityCategory.Custom,
            Description = description,
            Icon = "circle"
        };
    }

    /// <summary>
    /// Adds a custom relationship type at runtime.
    /// </summary>
    public static void RegisterCustomRelationshipType(string typeName, string description, string? inverseType = null)
    {
        var normalized = NormalizeRelationshipType(typeName);
        ValidRelationshipTypes.Add(normalized);
        RelationshipTypeMetadata[normalized] = new RelationshipTypeInfo
        {
            TypeName = normalized,
            Category = RelationshipCategory.Custom,
            Description = description,
            InverseType = inverseType
        };
    }
}

/// <summary>
/// Result of entity/relationship validation.
/// </summary>
public sealed class GraphValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; init; } = [];
    public string? NormalizedValue { get; init; }

    public static GraphValidationResult Success(string normalizedValue) => new()
    {
        NormalizedValue = normalizedValue
    };

    public static GraphValidationResult Failure(params string[] errors) => new()
    {
        Errors = [.. errors]
    };
}
