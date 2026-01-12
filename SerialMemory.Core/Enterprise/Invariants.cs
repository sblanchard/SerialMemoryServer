using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace SerialMemory.Core.Enterprise;

/// <summary>
/// Invariant enforcement - fail fast on violations.
/// All methods throw on failure.
/// </summary>
public static class Invariant
{
    // ==========================================
    // NULL CHECKS
    // ==========================================

    [StackTraceHidden]
    public static T NotNull<T>(
        T? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : class
    {
        if (value is null)
            throw new InvariantViolationException($"{paramName} cannot be null");
        return value;
    }

    [StackTraceHidden]
    public static T NotNull<T>(
        T? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : struct
    {
        if (!value.HasValue)
            throw new InvariantViolationException($"{paramName} cannot be null");
        return value.Value;
    }

    [StackTraceHidden]
    public static string NotNullOrEmpty(
        string? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvariantViolationException($"{paramName} cannot be null or empty");
        return value;
    }

    [StackTraceHidden]
    public static string NotNullOrWhiteSpace(
        string? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvariantViolationException($"{paramName} cannot be null, empty, or whitespace");
        return value;
    }

    // ==========================================
    // GUID CHECKS
    // ==========================================

    [StackTraceHidden]
    public static Guid NotEmpty(
        Guid value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value == Guid.Empty)
            throw new InvariantViolationException($"{paramName} cannot be empty GUID");
        return value;
    }

    // ==========================================
    // RANGE CHECKS
    // ==========================================

    [StackTraceHidden]
    public static T InRange<T>(
        T value,
        T min,
        T max,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
            throw new InvariantViolationException($"{paramName} must be between {min} and {max}, but was {value}");
        return value;
    }

    [StackTraceHidden]
    public static T GreaterThan<T>(
        T value,
        T threshold,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
    {
        if (value.CompareTo(threshold) <= 0)
            throw new InvariantViolationException($"{paramName} must be greater than {threshold}, but was {value}");
        return value;
    }

    [StackTraceHidden]
    public static T GreaterThanOrEqual<T>(
        T value,
        T threshold,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
    {
        if (value.CompareTo(threshold) < 0)
            throw new InvariantViolationException($"{paramName} must be >= {threshold}, but was {value}");
        return value;
    }

    [StackTraceHidden]
    public static T LessThan<T>(
        T value,
        T threshold,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
    {
        if (value.CompareTo(threshold) >= 0)
            throw new InvariantViolationException($"{paramName} must be less than {threshold}, but was {value}");
        return value;
    }

    [StackTraceHidden]
    public static float Probability(
        float value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value < 0 || value > 1 || float.IsNaN(value))
            throw new InvariantViolationException($"{paramName} must be a valid probability [0,1], but was {value}");
        return value;
    }

    [StackTraceHidden]
    public static int Positive(
        int value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value <= 0)
            throw new InvariantViolationException($"{paramName} must be positive, but was {value}");
        return value;
    }

    [StackTraceHidden]
    public static int NonNegative(
        int value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value < 0)
            throw new InvariantViolationException($"{paramName} must be non-negative, but was {value}");
        return value;
    }

    // ==========================================
    // COLLECTION CHECKS
    // ==========================================

    [StackTraceHidden]
    public static IReadOnlyList<T> NotEmpty<T>(
        IReadOnlyList<T>? collection,
        [CallerArgumentExpression(nameof(collection))] string? paramName = null)
    {
        if (collection is null || collection.Count == 0)
            throw new InvariantViolationException($"{paramName} cannot be null or empty");
        return collection;
    }

    [StackTraceHidden]
    public static IReadOnlyList<T> MaxLength<T>(
        IReadOnlyList<T> collection,
        int maxLength,
        [CallerArgumentExpression(nameof(collection))] string? paramName = null)
    {
        if (collection.Count > maxLength)
            throw new InvariantViolationException($"{paramName} exceeds maximum length {maxLength}, was {collection.Count}");
        return collection;
    }

    [StackTraceHidden]
    public static string MaxLength(
        string value,
        int maxLength,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value.Length > maxLength)
            throw new InvariantViolationException($"{paramName} exceeds maximum length {maxLength}, was {value.Length}");
        return value;
    }

    // ==========================================
    // BOOLEAN CHECKS
    // ==========================================

    [StackTraceHidden]
    public static void That(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvariantViolationException(message);
    }

    [StackTraceHidden]
    public static void That(
        bool condition,
        Func<string> messageFactory)
    {
        if (!condition)
            throw new InvariantViolationException(messageFactory());
    }

    // ==========================================
    // HASH CHECKS
    // ==========================================

    [StackTraceHidden]
    public static void ContentHashMatches(string content, string expectedHash)
    {
        var actualHash = ComputeHash(content);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ContentIntegrityViolationException(expectedHash, actualHash);
    }

    [StackTraceHidden]
    public static void ValidSha256Hash(
        string hash,
        [CallerArgumentExpression(nameof(hash))] string? paramName = null)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length != 64)
            throw new InvariantViolationException($"{paramName} must be a valid SHA-256 hash (64 hex chars)");

        foreach (var c in hash)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                throw new InvariantViolationException($"{paramName} contains invalid hex character: {c}");
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    // ==========================================
    // EMBEDDING CHECKS
    // ==========================================

    [StackTraceHidden]
    public static float[] ValidEmbedding(
        float[]? embedding,
        [CallerArgumentExpression(nameof(embedding))] string? paramName = null)
    {
        if (embedding is null)
            throw new InvariantViolationException($"{paramName} cannot be null");

        if (embedding.Length != 384 && embedding.Length != 768 && embedding.Length != 1024 && embedding.Length != 1536)
            throw new InvariantViolationException($"{paramName} must be 384, 768, 1024, or 1536 dimensions, was {embedding.Length}");

        for (int i = 0; i < embedding.Length; i++)
        {
            if (float.IsNaN(embedding[i]) || float.IsInfinity(embedding[i]))
                throw new InvariantViolationException($"{paramName}[{i}] contains invalid value: {embedding[i]}");
        }

        return embedding;
    }

    // ==========================================
    // MEMORY INVARIANTS
    // ==========================================

    [StackTraceHidden]
    public static void NoCausalCycle(Guid memoryId, IReadOnlyList<Guid>? causalParents)
    {
        if (causalParents is null) return;

        if (causalParents.Contains(memoryId))
            throw new CausalCycleViolationException(memoryId, memoryId);
    }

    [StackTraceHidden]
    public static void ValidMemoryLayer(string layer)
    {
        var validLayers = new[] { "L0_RAW", "L1_CONTEXT", "L2_SUMMARY", "L3_KNOWLEDGE", "L4_HEURISTIC" };
        if (!validLayers.Contains(layer))
            throw new InvariantViolationException($"Invalid memory layer: {layer}. Must be one of: {string.Join(", ", validLayers)}");
    }

    // ==========================================
    // TENANT INVARIANTS
    // ==========================================

    [StackTraceHidden]
    public static void TenantMatch(string expectedTenant, string actualTenant)
    {
        if (!string.Equals(expectedTenant, actualTenant, StringComparison.Ordinal))
            throw new TenantIsolationViolationException(expectedTenant, actualTenant);
    }
}

/// <summary>
/// Business rule invariant checks for domain logic.
/// </summary>
public static class BusinessInvariant
{
    /// <summary>Memory cannot have more than N causal parents.</summary>
    [StackTraceHidden]
    public static void MaxCausalParents(IReadOnlyList<Guid>? parents, int max = 10)
    {
        if (parents is not null && parents.Count > max)
            throw new BusinessRuleViolationException($"Memory cannot have more than {max} causal parents, has {parents.Count}");
    }

    /// <summary>Confidence decay cannot result in negative confidence.</summary>
    [StackTraceHidden]
    public static float SafeDecay(float confidence, float decayFactor)
    {
        var result = confidence * decayFactor;
        if (result < 0) result = 0;
        if (result > 1) result = 1;
        return result;
    }

    /// <summary>Relationship must connect different entities.</summary>
    [StackTraceHidden]
    public static void NoSelfRelationship(Guid sourceId, Guid targetId)
    {
        if (sourceId == targetId)
            throw new BusinessRuleViolationException("Entity cannot have a relationship with itself");
    }

    /// <summary>Content must be under size limit.</summary>
    [StackTraceHidden]
    public static void ContentSizeLimit(string content, int maxBytes = 1_000_000)
    {
        var size = Encoding.UTF8.GetByteCount(content);
        if (size > maxBytes)
            throw new BusinessRuleViolationException($"Content size {size} bytes exceeds limit of {maxBytes} bytes");
    }

    /// <summary>Batch operation must be within limits.</summary>
    [StackTraceHidden]
    public static void BatchSizeLimit<T>(IReadOnlyList<T> items, int maxItems = 1000)
    {
        if (items.Count > maxItems)
            throw new BusinessRuleViolationException($"Batch size {items.Count} exceeds limit of {maxItems}");
    }
}

// Exception types
public class InvariantViolationException : Exception
{
    public InvariantViolationException(string message) : base(message) { }
}

public sealed class ContentIntegrityViolationException : InvariantViolationException
{
    public string ExpectedHash { get; }
    public string ActualHash { get; }

    public ContentIntegrityViolationException(string expectedHash, string actualHash)
        : base($"Content integrity violation: expected hash {expectedHash}, actual {actualHash}")
    {
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
    }
}

public sealed class CausalCycleViolationException : InvariantViolationException
{
    public Guid MemoryId { get; }
    public Guid CycleTargetId { get; }

    public CausalCycleViolationException(Guid memoryId, Guid cycleTargetId)
        : base($"Causal cycle detected: memory {memoryId} cannot reference {cycleTargetId} as parent")
    {
        MemoryId = memoryId;
        CycleTargetId = cycleTargetId;
    }
}

public sealed class BusinessRuleViolationException : InvariantViolationException
{
    public BusinessRuleViolationException(string message) : base(message) { }
}

/// <summary>
/// Debug-only assertions (compiled out in Release builds).
/// </summary>
public static class DebugInvariant
{
    [Conditional("DEBUG")]
    public static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvariantViolationException($"[DEBUG] {message}");
    }

    [Conditional("DEBUG")]
    public static void NotNull<T>(T? value, string name) where T : class
    {
        if (value is null)
            throw new InvariantViolationException($"[DEBUG] {name} should not be null");
    }
}
