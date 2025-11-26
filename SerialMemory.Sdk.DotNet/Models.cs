namespace SerialMemory.Sdk;

#region Search Results

public sealed class SearchResult
{
    public required List<MemoryMatch> Memories { get; init; }
    public int TotalCount { get; init; }
    public string? Query { get; init; }
}

public sealed class MemoryMatch
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public float Score { get; init; }
    public float Confidence { get; init; }
    public string? Source { get; init; }
    public string? Layer { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<EntityInfo>? Entities { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

public sealed class EntityInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
}

#endregion

#region Ingest Results

public sealed class IngestResult
{
    public required string MemoryId { get; init; }
    public string? Message { get; init; }
    public int EntitiesExtracted { get; init; }
    public int RelationshipsExtracted { get; init; }
    public List<string>? Entities { get; init; }
}

#endregion

#region Update/Delete Results

public sealed class UpdateResult
{
    public required string MemoryId { get; init; }
    public string? Message { get; init; }
    public bool Success { get; init; }
}

public sealed class DeleteResult
{
    public required string MemoryId { get; init; }
    public string? Message { get; init; }
    public bool Success { get; init; }
}

#endregion

#region User Persona

public sealed class UserPersona
{
    public required string UserId { get; init; }
    public List<PersonaAttribute>? Preferences { get; init; }
    public List<PersonaAttribute>? Skills { get; init; }
    public List<PersonaAttribute>? Goals { get; init; }
    public List<PersonaAttribute>? Background { get; init; }
}

public sealed class PersonaAttribute
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public float Confidence { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class SetPersonaResult
{
    public string? Message { get; init; }
    public bool Success { get; init; }
}

#endregion

#region Tenant Limits

public sealed class TenantLimits
{
    public required string Plan { get; init; }
    public required string DisplayName { get; init; }

    // Credit limits
    public decimal MonthlyCredits { get; init; }
    public decimal CreditsUsed { get; init; }
    public decimal CreditsRemaining { get; init; }
    public int DaysUntilReset { get; init; }
    public DateTimeOffset CycleResetAt { get; init; }

    // Rate limits
    public int RateLimitRpm { get; init; }
    public int RateLimitRpd { get; init; }

    // Hard caps
    public int? MaxMemories { get; init; }
    public int? MaxEntities { get; init; }
    public int? MaxMemorySizeBytes { get; init; }

    // Current usage
    public int CurrentMemories { get; init; }
    public int CurrentEntities { get; init; }

    // Warnings
    public required List<LimitWarning> Warnings { get; init; }
}

public sealed class LimitWarning
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public decimal PercentUsed { get; init; }
    public required string Severity { get; init; }
}

/// <summary>
/// Usage warning raised when thresholds are crossed.
/// </summary>
public sealed class UsageWarning
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public decimal PercentUsed { get; init; }
    public required string Severity { get; init; }
}

#endregion
