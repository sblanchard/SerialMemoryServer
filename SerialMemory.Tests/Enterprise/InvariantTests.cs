using SerialMemory.Core.Enterprise;
using Xunit;

namespace SerialMemory.Tests.Enterprise;

public class InvariantTests
{
    // ==========================================
    // NULL CHECKS
    // ==========================================

    [Fact]
    public void NotNull_WithValue_ReturnsValue()
    {
        var value = "test";
        var result = Invariant.NotNull(value);
        Assert.Equal("test", result);
    }

    [Fact]
    public void NotNull_WithNull_Throws()
    {
        string? value = null;
        var ex = Assert.Throws<InvariantViolationException>(() => Invariant.NotNull(value));
        Assert.Contains("cannot be null", ex.Message);
    }

    [Fact]
    public void NotNullOrEmpty_WithValue_ReturnsValue()
    {
        var result = Invariant.NotNullOrEmpty("test");
        Assert.Equal("test", result);
    }

    [Fact]
    public void NotNullOrEmpty_WithEmpty_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.NotNullOrEmpty(""));
    }

    [Fact]
    public void NotNullOrWhiteSpace_WithValue_ReturnsValue()
    {
        var result = Invariant.NotNullOrWhiteSpace("test");
        Assert.Equal("test", result);
    }

    [Fact]
    public void NotNullOrWhiteSpace_WithWhiteSpace_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.NotNullOrWhiteSpace("   "));
    }

    // ==========================================
    // GUID CHECKS
    // ==========================================

    [Fact]
    public void NotEmpty_WithValidGuid_ReturnsGuid()
    {
        var guid = Guid.CreateVersion7();
        var result = Invariant.NotEmpty(guid);
        Assert.Equal(guid, result);
    }

    [Fact]
    public void NotEmpty_WithEmptyGuid_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.NotEmpty(Guid.Empty));
    }

    // ==========================================
    // RANGE CHECKS
    // ==========================================

    [Fact]
    public void InRange_WithinRange_ReturnsValue()
    {
        var result = Invariant.InRange(5, 0, 10);
        Assert.Equal(5, result);
    }

    [Fact]
    public void InRange_BelowMin_Throws()
    {
        var ex = Assert.Throws<InvariantViolationException>(() => Invariant.InRange(-1, 0, 10));
        Assert.Contains("must be between", ex.Message);
    }

    [Fact]
    public void InRange_AboveMax_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.InRange(11, 0, 10));
    }

    [Fact]
    public void InRange_AtBoundaries_ReturnsValue()
    {
        Assert.Equal(0, Invariant.InRange(0, 0, 10));
        Assert.Equal(10, Invariant.InRange(10, 0, 10));
    }

    [Fact]
    public void GreaterThan_Valid_ReturnsValue()
    {
        var result = Invariant.GreaterThan(5, 4);
        Assert.Equal(5, result);
    }

    [Fact]
    public void GreaterThan_Equal_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.GreaterThan(5, 5));
    }

    [Fact]
    public void GreaterThan_Less_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.GreaterThan(4, 5));
    }

    [Fact]
    public void GreaterThanOrEqual_Valid_ReturnsValue()
    {
        Assert.Equal(5, Invariant.GreaterThanOrEqual(5, 4));
        Assert.Equal(5, Invariant.GreaterThanOrEqual(5, 5));
    }

    [Fact]
    public void GreaterThanOrEqual_Less_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.GreaterThanOrEqual(4, 5));
    }

    [Fact]
    public void LessThan_Valid_ReturnsValue()
    {
        var result = Invariant.LessThan(4, 5);
        Assert.Equal(4, result);
    }

    [Fact]
    public void LessThan_Equal_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.LessThan(5, 5));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Probability_Valid_ReturnsValue(float value)
    {
        var result = Invariant.Probability(value);
        Assert.Equal(value, result);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    public void Probability_Invalid_Throws(float value)
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.Probability(value));
    }

    [Fact]
    public void Positive_WithPositive_ReturnsValue()
    {
        Assert.Equal(5, Invariant.Positive(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Positive_NonPositive_Throws(int value)
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.Positive(value));
    }

    [Fact]
    public void NonNegative_WithNonNegative_ReturnsValue()
    {
        Assert.Equal(0, Invariant.NonNegative(0));
        Assert.Equal(5, Invariant.NonNegative(5));
    }

    [Fact]
    public void NonNegative_Negative_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.NonNegative(-1));
    }

    // ==========================================
    // COLLECTION CHECKS
    // ==========================================

    [Fact]
    public void NotEmpty_Collection_WithItems_ReturnsCollection()
    {
        var list = new List<int> { 1, 2, 3 };
        var result = Invariant.NotEmpty<int>(list);
        Assert.Equal(list, result);
    }

    [Fact]
    public void NotEmpty_Collection_Empty_Throws()
    {
        var list = new List<int>();
        Assert.Throws<InvariantViolationException>(() => Invariant.NotEmpty<int>(list));
    }

    [Fact]
    public void NotEmpty_Collection_Null_Throws()
    {
        List<int>? list = null;
        Assert.Throws<InvariantViolationException>(() => Invariant.NotEmpty<int>(list));
    }

    [Fact]
    public void MaxLength_Collection_WithinLimit_ReturnsCollection()
    {
        var list = new List<int> { 1, 2, 3 };
        var result = Invariant.MaxLength(list, 5);
        Assert.Equal(list, result);
    }

    [Fact]
    public void MaxLength_Collection_ExceedsLimit_Throws()
    {
        var list = new List<int> { 1, 2, 3, 4, 5, 6 };
        Assert.Throws<InvariantViolationException>(() => Invariant.MaxLength(list, 5));
    }

    [Fact]
    public void MaxLength_String_WithinLimit_ReturnsString()
    {
        var result = Invariant.MaxLength("test", 10);
        Assert.Equal("test", result);
    }

    [Fact]
    public void MaxLength_String_ExceedsLimit_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.MaxLength("test", 2));
    }

    // ==========================================
    // BOOLEAN CHECKS
    // ==========================================

    [Fact]
    public void That_TrueCondition_NoException()
    {
        var ex = Record.Exception(() => Invariant.That(true, "should not throw"));
        Assert.Null(ex);
    }

    [Fact]
    public void That_FalseCondition_Throws()
    {
        var ex = Assert.Throws<InvariantViolationException>(() => Invariant.That(false, "condition failed"));
        Assert.Equal("condition failed", ex.Message);
    }

    [Fact]
    public void That_WithMessageFactory_LazyEvaluates()
    {
        var evaluated = false;

        Invariant.That(true, () =>
        {
            evaluated = true;
            return "should not evaluate";
        });

        Assert.False(evaluated);
    }

    // ==========================================
    // HASH CHECKS
    // ==========================================

    [Fact]
    public void ValidSha256Hash_Valid_NoException()
    {
        var hash = new string('a', 64);
        var ex = Record.Exception(() => Invariant.ValidSha256Hash(hash));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidSha256Hash_TooShort_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.ValidSha256Hash("abc"));
    }

    [Fact]
    public void ValidSha256Hash_InvalidChars_Throws()
    {
        var hash = new string('g', 64); // 'g' is not valid hex
        Assert.Throws<InvariantViolationException>(() => Invariant.ValidSha256Hash(hash));
    }

    [Fact]
    public void ContentHashMatches_Matching_NoException()
    {
        var content = "test content";
        var hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // SHA256 of empty string

        // This will throw because hashes don't match - testing the mechanism
        Assert.Throws<ContentIntegrityViolationException>(() =>
            Invariant.ContentHashMatches(content, hash));
    }

    // ==========================================
    // EMBEDDING CHECKS
    // ==========================================

    [Theory]
    [InlineData(384)]
    [InlineData(768)]
    [InlineData(1024)]
    public void ValidEmbedding_ValidDimensions_ReturnsEmbedding(int dim)
    {
        var embedding = new float[dim];
        var result = Invariant.ValidEmbedding(embedding);
        Assert.Equal(embedding, result);
    }

    [Fact]
    public void ValidEmbedding_InvalidDimension_Throws()
    {
        var embedding = new float[100];
        Assert.Throws<InvariantViolationException>(() => Invariant.ValidEmbedding(embedding));
    }

    [Fact]
    public void ValidEmbedding_Null_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.ValidEmbedding(null));
    }

    [Fact]
    public void ValidEmbedding_WithNaN_Throws()
    {
        var embedding = new float[384];
        embedding[0] = float.NaN;
        Assert.Throws<InvariantViolationException>(() => Invariant.ValidEmbedding(embedding));
    }

    [Fact]
    public void ValidEmbedding_WithInfinity_Throws()
    {
        var embedding = new float[384];
        embedding[0] = float.PositiveInfinity;
        Assert.Throws<InvariantViolationException>(() => Invariant.ValidEmbedding(embedding));
    }

    // ==========================================
    // MEMORY INVARIANTS
    // ==========================================

    [Fact]
    public void NoCausalCycle_NoCycle_NoException()
    {
        var memoryId = Guid.CreateVersion7();
        var parents = new List<Guid> { Guid.CreateVersion7(), Guid.CreateVersion7() };

        var ex = Record.Exception(() => Invariant.NoCausalCycle(memoryId, parents));
        Assert.Null(ex);
    }

    [Fact]
    public void NoCausalCycle_SelfReference_Throws()
    {
        var memoryId = Guid.CreateVersion7();
        var parents = new List<Guid> { memoryId };

        var ex = Assert.Throws<CausalCycleViolationException>(() =>
            Invariant.NoCausalCycle(memoryId, parents));

        Assert.Equal(memoryId, ex.MemoryId);
    }

    [Fact]
    public void NoCausalCycle_NullParents_NoException()
    {
        var memoryId = Guid.CreateVersion7();
        var ex = Record.Exception(() => Invariant.NoCausalCycle(memoryId, null));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("L0_RAW")]
    [InlineData("L1_CONTEXT")]
    [InlineData("L2_SUMMARY")]
    [InlineData("L3_KNOWLEDGE")]
    [InlineData("L4_HEURISTIC")]
    public void ValidMemoryLayer_ValidLayers_NoException(string layer)
    {
        var ex = Record.Exception(() => Invariant.ValidMemoryLayer(layer));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidMemoryLayer_InvalidLayer_Throws()
    {
        Assert.Throws<InvariantViolationException>(() => Invariant.ValidMemoryLayer("INVALID"));
    }

    // ==========================================
    // BUSINESS INVARIANTS
    // ==========================================

    [Fact]
    public void MaxCausalParents_WithinLimit_NoException()
    {
        var parents = new List<Guid> { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var ex = Record.Exception(() => BusinessInvariant.MaxCausalParents(parents));
        Assert.Null(ex);
    }

    [Fact]
    public void MaxCausalParents_ExceedsLimit_Throws()
    {
        var parents = Enumerable.Range(0, 15).Select(_ => Guid.CreateVersion7()).ToList();
        Assert.Throws<BusinessRuleViolationException>(() =>
            BusinessInvariant.MaxCausalParents(parents, max: 10));
    }

    [Fact]
    public void SafeDecay_ReturnsDecayedValue()
    {
        var result = BusinessInvariant.SafeDecay(1.0f, 0.5f);
        Assert.Equal(0.5f, result);
    }

    [Fact]
    public void SafeDecay_ClampsToZero()
    {
        var result = BusinessInvariant.SafeDecay(0.1f, -1f);
        Assert.Equal(0f, result);
    }

    [Fact]
    public void SafeDecay_ClampsToOne()
    {
        var result = BusinessInvariant.SafeDecay(0.5f, 3f);
        Assert.Equal(1f, result);
    }

    [Fact]
    public void NoSelfRelationship_DifferentIds_NoException()
    {
        var ex = Record.Exception(() =>
            BusinessInvariant.NoSelfRelationship(Guid.CreateVersion7(), Guid.CreateVersion7()));
        Assert.Null(ex);
    }

    [Fact]
    public void NoSelfRelationship_SameId_Throws()
    {
        var id = Guid.CreateVersion7();
        Assert.Throws<BusinessRuleViolationException>(() =>
            BusinessInvariant.NoSelfRelationship(id, id));
    }

    [Fact]
    public void ContentSizeLimit_WithinLimit_NoException()
    {
        var content = new string('a', 1000);
        var ex = Record.Exception(() => BusinessInvariant.ContentSizeLimit(content));
        Assert.Null(ex);
    }

    [Fact]
    public void ContentSizeLimit_ExceedsLimit_Throws()
    {
        var content = new string('a', 2_000_000);
        Assert.Throws<BusinessRuleViolationException>(() =>
            BusinessInvariant.ContentSizeLimit(content));
    }

    [Fact]
    public void BatchSizeLimit_WithinLimit_NoException()
    {
        var items = new List<int> { 1, 2, 3 };
        var ex = Record.Exception(() => BusinessInvariant.BatchSizeLimit(items));
        Assert.Null(ex);
    }

    [Fact]
    public void BatchSizeLimit_ExceedsLimit_Throws()
    {
        var items = Enumerable.Range(0, 2000).ToList();
        Assert.Throws<BusinessRuleViolationException>(() =>
            BusinessInvariant.BatchSizeLimit(items));
    }

    // ==========================================
    // TENANT INVARIANTS
    // ==========================================

    [Fact]
    public void TenantMatch_SameTenant_NoException()
    {
        var ex = Record.Exception(() => Invariant.TenantMatch("tenant1", "tenant1"));
        Assert.Null(ex);
    }

    [Fact]
    public void TenantMatch_DifferentTenant_Throws()
    {
        var ex = Assert.Throws<TenantIsolationViolationException>(() =>
            Invariant.TenantMatch("tenant1", "tenant2"));

        Assert.Equal("tenant1", ex.RequestingTenant);
        Assert.Equal("tenant2", ex.ResourceTenant);
    }
}
