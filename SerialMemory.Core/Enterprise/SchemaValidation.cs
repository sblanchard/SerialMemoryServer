using System.Text.RegularExpressions;

namespace SerialMemory.Core.Enterprise;

/// <summary>
/// Validation result with detailed errors.
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid { get; init; }
    public List<ValidationError> Errors { get; init; } = [];

    public static ValidationResult Valid() => new() { IsValid = true };

    public static ValidationResult Invalid(params ValidationError[] errors) =>
        new() { IsValid = false, Errors = errors.ToList() };

    public static ValidationResult Invalid(string field, string message) =>
        new() { IsValid = false, Errors = [new ValidationError { Field = field, Message = message }] };

    public ValidationResult Merge(ValidationResult other)
    {
        return new ValidationResult
        {
            IsValid = IsValid && other.IsValid,
            Errors = Errors.Concat(other.Errors).ToList()
        };
    }
}

public sealed class ValidationError
{
    public string Field { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Code { get; init; }
    public object? AttemptedValue { get; init; }
}

/// <summary>
/// Strict schema validator with invariant enforcement.
/// </summary>
public interface ISchemaValidator<T>
{
    ValidationResult Validate(T instance);
    Task<ValidationResult> ValidateAsync(T instance, CancellationToken ct = default);
}

/// <summary>
/// Memory schema validator - enforces all memory invariants.
/// </summary>
public sealed class MemorySchemaValidator : ISchemaValidator<MemorySchema>
{
    private static readonly Regex ContentHashPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ValidLayers = ["L0_RAW", "L1_CONTEXT", "L2_SUMMARY", "L3_KNOWLEDGE", "L4_HEURISTIC"];

    public ValidationResult Validate(MemorySchema memory)
    {
        var errors = new List<ValidationError>();

        // ID validation
        if (memory.Id == Guid.Empty)
        {
            errors.Add(new ValidationError { Field = "Id", Message = "Memory ID cannot be empty", Code = "EMPTY_ID" });
        }

        // Content validation
        if (string.IsNullOrWhiteSpace(memory.Content))
        {
            errors.Add(new ValidationError { Field = "Content", Message = "Content cannot be empty", Code = "EMPTY_CONTENT" });
        }
        else if (memory.Content.Length > 1_000_000)
        {
            errors.Add(new ValidationError { Field = "Content", Message = "Content exceeds maximum length of 1MB", Code = "CONTENT_TOO_LONG", AttemptedValue = memory.Content.Length });
        }

        // Content hash validation
        if (!string.IsNullOrEmpty(memory.ContentHash) && !ContentHashPattern.IsMatch(memory.ContentHash))
        {
            errors.Add(new ValidationError { Field = "ContentHash", Message = "Content hash must be valid SHA-256 (64 hex chars)", Code = "INVALID_HASH", AttemptedValue = memory.ContentHash });
        }

        // Layer validation
        if (string.IsNullOrWhiteSpace(memory.Layer))
        {
            errors.Add(new ValidationError { Field = "Layer", Message = "Layer is required", Code = "MISSING_LAYER" });
        }
        else if (!ValidLayers.Contains(memory.Layer))
        {
            errors.Add(new ValidationError { Field = "Layer", Message = $"Invalid layer. Must be one of: {string.Join(", ", ValidLayers)}", Code = "INVALID_LAYER", AttemptedValue = memory.Layer });
        }

        // Confidence validation
        if (memory.Confidence < 0 || memory.Confidence > 1)
        {
            errors.Add(new ValidationError { Field = "Confidence", Message = "Confidence must be between 0.0 and 1.0", Code = "INVALID_CONFIDENCE", AttemptedValue = memory.Confidence });
        }

        // Half-life validation
        if (memory.HalfLifeDays < 1 || memory.HalfLifeDays > 365 * 10)
        {
            errors.Add(new ValidationError { Field = "HalfLifeDays", Message = "Half-life must be between 1 and 3650 days", Code = "INVALID_HALFLIFE", AttemptedValue = memory.HalfLifeDays });
        }

        // Embedding validation
        if (memory.Embedding != null)
        {
            if (memory.Embedding.Length != 384 && memory.Embedding.Length != 768 && memory.Embedding.Length != 1024 && memory.Embedding.Length != 1536)
            {
                errors.Add(new ValidationError { Field = "Embedding", Message = "Embedding must be 384, 768, 1024, or 1536 dimensions", Code = "INVALID_EMBEDDING_DIM", AttemptedValue = memory.Embedding.Length });
            }

            if (memory.Embedding.Any(float.IsNaN) || memory.Embedding.Any(float.IsInfinity))
            {
                errors.Add(new ValidationError { Field = "Embedding", Message = "Embedding contains invalid values (NaN or Infinity)", Code = "INVALID_EMBEDDING_VALUES" });
            }
        }

        // Tenant validation
        if (string.IsNullOrWhiteSpace(memory.TenantId))
        {
            errors.Add(new ValidationError { Field = "TenantId", Message = "Tenant ID is required", Code = "MISSING_TENANT" });
        }
        else if (memory.TenantId.Length > 100)
        {
            errors.Add(new ValidationError { Field = "TenantId", Message = "Tenant ID exceeds maximum length of 100", Code = "TENANT_TOO_LONG", AttemptedValue = memory.TenantId.Length });
        }

        // Causal parents validation
        if (memory.CausalParents != null)
        {
            if (memory.CausalParents.Contains(memory.Id))
            {
                errors.Add(new ValidationError { Field = "CausalParents", Message = "Memory cannot be its own causal parent", Code = "SELF_REFERENCE" });
            }

            if (memory.CausalParents.Count != memory.CausalParents.Distinct().Count())
            {
                errors.Add(new ValidationError { Field = "CausalParents", Message = "Causal parents contains duplicates", Code = "DUPLICATE_PARENTS" });
            }
        }

        return errors.Count == 0 ? ValidationResult.Valid() : ValidationResult.Invalid(errors.ToArray());
    }

    public Task<ValidationResult> ValidateAsync(MemorySchema memory, CancellationToken ct = default)
    {
        return Task.FromResult(Validate(memory));
    }
}

/// <summary>
/// Entity schema validator.
/// </summary>
public sealed class EntitySchemaValidator : ISchemaValidator<EntitySchema>
{
    private static readonly HashSet<string> ValidEntityTypes = ["PERSON", "ORG", "GPE", "DATE", "MONEY", "PRODUCT", "EVENT", "WORK_OF_ART", "LAW", "LANGUAGE", "QUANTITY", "ORDINAL", "CARDINAL", "PERCENT", "TIME", "FAC", "LOC", "NORP", "TECHNOLOGY", "CONCEPT"];
    private static readonly Regex NamePattern = new("^[\\w\\s\\-\\.,'()]+$", RegexOptions.Compiled);

    public ValidationResult Validate(EntitySchema entity)
    {
        var errors = new List<ValidationError>();

        // ID validation
        if (entity.Id == Guid.Empty)
        {
            errors.Add(new ValidationError { Field = "Id", Message = "Entity ID cannot be empty", Code = "EMPTY_ID" });
        }

        // Name validation
        if (string.IsNullOrWhiteSpace(entity.Name))
        {
            errors.Add(new ValidationError { Field = "Name", Message = "Entity name cannot be empty", Code = "EMPTY_NAME" });
        }
        else if (entity.Name.Length > 500)
        {
            errors.Add(new ValidationError { Field = "Name", Message = "Entity name exceeds maximum length of 500", Code = "NAME_TOO_LONG", AttemptedValue = entity.Name.Length });
        }
        else if (!NamePattern.IsMatch(entity.Name))
        {
            errors.Add(new ValidationError { Field = "Name", Message = "Entity name contains invalid characters", Code = "INVALID_NAME_CHARS", AttemptedValue = entity.Name });
        }

        // Entity type validation
        if (string.IsNullOrWhiteSpace(entity.EntityType))
        {
            errors.Add(new ValidationError { Field = "EntityType", Message = "Entity type is required", Code = "MISSING_TYPE" });
        }
        else if (!ValidEntityTypes.Contains(entity.EntityType.ToUpperInvariant()))
        {
            errors.Add(new ValidationError { Field = "EntityType", Message = $"Invalid entity type. Must be one of: {string.Join(", ", ValidEntityTypes)}", Code = "INVALID_TYPE", AttemptedValue = entity.EntityType });
        }

        // Tenant validation
        if (string.IsNullOrWhiteSpace(entity.TenantId))
        {
            errors.Add(new ValidationError { Field = "TenantId", Message = "Tenant ID is required", Code = "MISSING_TENANT" });
        }

        return errors.Count == 0 ? ValidationResult.Valid() : ValidationResult.Invalid(errors.ToArray());
    }

    public Task<ValidationResult> ValidateAsync(EntitySchema entity, CancellationToken ct = default)
    {
        return Task.FromResult(Validate(entity));
    }
}

/// <summary>
/// Relationship schema validator.
/// </summary>
public sealed class RelationshipSchemaValidator : ISchemaValidator<RelationshipSchema>
{
    public ValidationResult Validate(RelationshipSchema relationship)
    {
        var errors = new List<ValidationError>();

        // ID validation
        if (relationship.Id == Guid.Empty)
        {
            errors.Add(new ValidationError { Field = "Id", Message = "Relationship ID cannot be empty", Code = "EMPTY_ID" });
        }

        // Source/Target validation
        if (relationship.SourceId == Guid.Empty)
        {
            errors.Add(new ValidationError { Field = "SourceId", Message = "Source entity ID cannot be empty", Code = "EMPTY_SOURCE" });
        }

        if (relationship.TargetId == Guid.Empty)
        {
            errors.Add(new ValidationError { Field = "TargetId", Message = "Target entity ID cannot be empty", Code = "EMPTY_TARGET" });
        }

        if (relationship.SourceId == relationship.TargetId && relationship.SourceId != Guid.Empty)
        {
            errors.Add(new ValidationError { Field = "TargetId", Message = "Source and target cannot be the same entity", Code = "SELF_REFERENCE" });
        }

        // Relationship type validation
        if (string.IsNullOrWhiteSpace(relationship.RelationshipType))
        {
            errors.Add(new ValidationError { Field = "RelationshipType", Message = "Relationship type is required", Code = "MISSING_TYPE" });
        }
        else if (relationship.RelationshipType.Length > 100)
        {
            errors.Add(new ValidationError { Field = "RelationshipType", Message = "Relationship type exceeds maximum length of 100", Code = "TYPE_TOO_LONG", AttemptedValue = relationship.RelationshipType.Length });
        }

        // Confidence validation
        if (relationship.Confidence < 0 || relationship.Confidence > 1)
        {
            errors.Add(new ValidationError { Field = "Confidence", Message = "Confidence must be between 0.0 and 1.0", Code = "INVALID_CONFIDENCE", AttemptedValue = relationship.Confidence });
        }

        // Tenant validation
        if (string.IsNullOrWhiteSpace(relationship.TenantId))
        {
            errors.Add(new ValidationError { Field = "TenantId", Message = "Tenant ID is required", Code = "MISSING_TENANT" });
        }

        return errors.Count == 0 ? ValidationResult.Valid() : ValidationResult.Invalid(errors.ToArray());
    }

    public Task<ValidationResult> ValidateAsync(RelationshipSchema relationship, CancellationToken ct = default)
    {
        return Task.FromResult(Validate(relationship));
    }
}

// Schema DTOs for validation
public sealed class MemorySchema
{
    public Guid Id { get; init; }
    public string Content { get; init; } = "";
    public string? ContentHash { get; init; }
    public string Layer { get; init; } = "L0_RAW";
    public float Confidence { get; init; } = 1.0f;
    public int HalfLifeDays { get; init; } = 30;
    public float[]? Embedding { get; init; }
    public string TenantId { get; init; } = "";
    public List<Guid>? CausalParents { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed class EntitySchema
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string TenantId { get; init; } = "";
}

public sealed class RelationshipSchema
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public Guid TargetId { get; init; }
    public string RelationshipType { get; init; } = "";
    public float Confidence { get; init; } = 1.0f;
    public string TenantId { get; init; } = "";
}

/// <summary>
/// Composite validator that runs all registered validators.
/// </summary>
public sealed class SchemaValidationPipeline
{
    private readonly MemorySchemaValidator _memoryValidator = new();
    private readonly EntitySchemaValidator _entityValidator = new();
    private readonly RelationshipSchemaValidator _relationshipValidator = new();

    public ValidationResult ValidateMemory(MemorySchema memory) => _memoryValidator.Validate(memory);
    public ValidationResult ValidateEntity(EntitySchema entity) => _entityValidator.Validate(entity);
    public ValidationResult ValidateRelationship(RelationshipSchema relationship) => _relationshipValidator.Validate(relationship);

    public void EnsureValid(MemorySchema memory)
    {
        var result = ValidateMemory(memory);
        if (!result.IsValid)
        {
            throw new SchemaValidationException("Memory", result.Errors);
        }
    }

    public void EnsureValid(EntitySchema entity)
    {
        var result = ValidateEntity(entity);
        if (!result.IsValid)
        {
            throw new SchemaValidationException("Entity", result.Errors);
        }
    }

    public void EnsureValid(RelationshipSchema relationship)
    {
        var result = ValidateRelationship(relationship);
        if (!result.IsValid)
        {
            throw new SchemaValidationException("Relationship", result.Errors);
        }
    }
}

public sealed class SchemaValidationException : Exception
{
    public string SchemaType { get; }
    public List<ValidationError> Errors { get; }

    public SchemaValidationException(string schemaType, List<ValidationError> errors)
        : base($"Schema validation failed for {schemaType}: {string.Join(", ", errors.Select(e => $"{e.Field}: {e.Message}"))}")
    {
        SchemaType = schemaType;
        Errors = errors;
    }
}
