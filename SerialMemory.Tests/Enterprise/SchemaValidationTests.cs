using SerialMemory.Core.Enterprise;
using Xunit;

namespace SerialMemory.Tests.Enterprise;

public class SchemaValidationTests
{
    private readonly MemorySchemaValidator _memoryValidator = new();
    private readonly EntitySchemaValidator _entityValidator = new();
    private readonly RelationshipSchemaValidator _relationshipValidator = new();

    // ==========================================
    // MEMORY SCHEMA TESTS
    // ==========================================

    [Fact]
    public void MemorySchema_Valid_ReturnsSuccess()
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test content",
            Layer = "L0_RAW",
            Confidence = 0.9f,
            HalfLifeDays = 30,
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void MemorySchema_EmptyId_ReturnsError()
    {
        var memory = new MemorySchema
        {
            Id = Guid.Empty,
            Content = "Test",
            Layer = "L0_RAW",
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "Id" && e.Code == "EMPTY_ID");
    }

    [Fact]
    public void MemorySchema_EmptyContent_ReturnsError()
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "",
            Layer = "L0_RAW",
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "Content" && e.Code == "EMPTY_CONTENT");
    }

    [Fact]
    public void MemorySchema_InvalidLayer_ReturnsError()
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "INVALID_LAYER",
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "Layer" && e.Code == "INVALID_LAYER");
    }

    [Theory]
    [InlineData("L0_RAW")]
    [InlineData("L1_CONTEXT")]
    [InlineData("L2_SUMMARY")]
    [InlineData("L3_KNOWLEDGE")]
    [InlineData("L4_HEURISTIC")]
    public void MemorySchema_ValidLayers_Pass(string layer)
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = layer,
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void MemorySchema_InvalidConfidence_ReturnsError(float confidence)
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "L0_RAW",
            Confidence = confidence,
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "Confidence" && e.Code == "INVALID_CONFIDENCE");
    }

    [Fact]
    public void MemorySchema_InvalidHash_ReturnsError()
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "L0_RAW",
            ContentHash = "invalid-hash",
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "ContentHash" && e.Code == "INVALID_HASH");
    }

    [Fact]
    public void MemorySchema_ValidHash_Passes()
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "L0_RAW",
            ContentHash = "a".PadRight(64, 'f'), // 64 hex chars
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(384)]
    [InlineData(768)]
    [InlineData(1024)]
    public void MemorySchema_ValidEmbeddingDimensions_Pass(int dim)
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "L0_RAW",
            Embedding = new float[dim],
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MemorySchema_InvalidEmbeddingDimension_ReturnsError()
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "L0_RAW",
            Embedding = new float[100], // Invalid dimension
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "Embedding" && e.Code == "INVALID_EMBEDDING_DIM");
    }

    [Fact]
    public void MemorySchema_EmbeddingWithNaN_ReturnsError()
    {
        var embedding = new float[384];
        embedding[100] = float.NaN;

        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "L0_RAW",
            Embedding = embedding,
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "INVALID_EMBEDDING_VALUES");
    }

    [Fact]
    public void MemorySchema_SelfReference_ReturnsError()
    {
        var id = Guid.CreateVersion7();
        var memory = new MemorySchema
        {
            Id = id,
            Content = "Test",
            Layer = "L0_RAW",
            CausalParents = [id], // Self-reference
            TenantId = "tenant1"
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "SELF_REFERENCE");
    }

    [Fact]
    public void MemorySchema_MissingTenant_ReturnsError()
    {
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "L0_RAW",
            TenantId = ""
        };

        var result = _memoryValidator.Validate(memory);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "TenantId" && e.Code == "MISSING_TENANT");
    }

    // ==========================================
    // ENTITY SCHEMA TESTS
    // ==========================================

    [Fact]
    public void EntitySchema_Valid_ReturnsSuccess()
    {
        var entity = new EntitySchema
        {
            Id = Guid.CreateVersion7(),
            Name = "John Doe",
            EntityType = "PERSON",
            TenantId = "tenant1"
        };

        var result = _entityValidator.Validate(entity);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void EntitySchema_EmptyName_ReturnsError()
    {
        var entity = new EntitySchema
        {
            Id = Guid.CreateVersion7(),
            Name = "",
            EntityType = "PERSON",
            TenantId = "tenant1"
        };

        var result = _entityValidator.Validate(entity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "Name" && e.Code == "EMPTY_NAME");
    }

    [Theory]
    [InlineData("PERSON")]
    [InlineData("ORG")]
    [InlineData("GPE")]
    [InlineData("DATE")]
    [InlineData("TECHNOLOGY")]
    public void EntitySchema_ValidEntityTypes_Pass(string type)
    {
        var entity = new EntitySchema
        {
            Id = Guid.CreateVersion7(),
            Name = "Test",
            EntityType = type,
            TenantId = "tenant1"
        };

        var result = _entityValidator.Validate(entity);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void EntitySchema_InvalidEntityType_ReturnsError()
    {
        var entity = new EntitySchema
        {
            Id = Guid.CreateVersion7(),
            Name = "Test",
            EntityType = "INVALID_TYPE",
            TenantId = "tenant1"
        };

        var result = _entityValidator.Validate(entity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "EntityType" && e.Code == "INVALID_TYPE");
    }

    // ==========================================
    // RELATIONSHIP SCHEMA TESTS
    // ==========================================

    [Fact]
    public void RelationshipSchema_Valid_ReturnsSuccess()
    {
        var relationship = new RelationshipSchema
        {
            Id = Guid.CreateVersion7(),
            SourceId = Guid.CreateVersion7(),
            TargetId = Guid.CreateVersion7(),
            RelationshipType = "works_at",
            Confidence = 0.9f,
            TenantId = "tenant1"
        };

        var result = _relationshipValidator.Validate(relationship);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RelationshipSchema_SelfReference_ReturnsError()
    {
        var id = Guid.CreateVersion7();
        var relationship = new RelationshipSchema
        {
            Id = Guid.CreateVersion7(),
            SourceId = id,
            TargetId = id, // Same as source
            RelationshipType = "relates_to",
            TenantId = "tenant1"
        };

        var result = _relationshipValidator.Validate(relationship);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "SELF_REFERENCE");
    }

    [Fact]
    public void RelationshipSchema_EmptySource_ReturnsError()
    {
        var relationship = new RelationshipSchema
        {
            Id = Guid.CreateVersion7(),
            SourceId = Guid.Empty,
            TargetId = Guid.CreateVersion7(),
            RelationshipType = "relates_to",
            TenantId = "tenant1"
        };

        var result = _relationshipValidator.Validate(relationship);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "SourceId" && e.Code == "EMPTY_SOURCE");
    }

    // ==========================================
    // VALIDATION PIPELINE TESTS
    // ==========================================

    [Fact]
    public void ValidationPipeline_EnsureValid_ValidMemory_NoException()
    {
        var pipeline = new SchemaValidationPipeline();
        var memory = new MemorySchema
        {
            Id = Guid.CreateVersion7(),
            Content = "Test",
            Layer = "L0_RAW",
            TenantId = "tenant1"
        };

        var ex = Record.Exception(() => pipeline.EnsureValid(memory));

        Assert.Null(ex);
    }

    [Fact]
    public void ValidationPipeline_EnsureValid_InvalidMemory_ThrowsException()
    {
        var pipeline = new SchemaValidationPipeline();
        var memory = new MemorySchema
        {
            Id = Guid.Empty,
            Content = "",
            Layer = "INVALID",
            TenantId = ""
        };

        var ex = Assert.Throws<SchemaValidationException>(() => pipeline.EnsureValid(memory));

        Assert.Equal("Memory", ex.SchemaType);
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void ValidationResult_Merge_CombinesErrors()
    {
        var result1 = ValidationResult.Invalid("field1", "error1");
        var result2 = ValidationResult.Invalid("field2", "error2");

        var merged = result1.Merge(result2);

        Assert.False(merged.IsValid);
        Assert.Equal(2, merged.Errors.Count);
    }
}
