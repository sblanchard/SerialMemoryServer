namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for validating payloads and protecting against abuse.
/// Enforces size limits, content validation, and resource quotas.
/// </summary>
public interface IPayloadValidationService
{
    /// <summary>
    /// Validates a memory content payload.
    /// </summary>
    Task<PayloadValidationResult> ValidateMemoryContentAsync(
        string tenantId,
        string content,
        CancellationToken ct = default);

    /// <summary>
    /// Validates an embedding payload.
    /// </summary>
    Task<PayloadValidationResult> ValidateEmbeddingAsync(
        string tenantId,
        float[] embedding,
        CancellationToken ct = default);

    /// <summary>
    /// Validates an export request.
    /// </summary>
    Task<PayloadValidationResult> ValidateExportRequestAsync(
        string tenantId,
        ExportValidationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Validates a batch operation.
    /// </summary>
    Task<PayloadValidationResult> ValidateBatchOperationAsync(
        string tenantId,
        int itemCount,
        long totalSizeBytes,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the payload limits for a tenant.
    /// </summary>
    Task<PayloadLimits> GetLimitsAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Result of payload validation.
/// </summary>
public sealed record PayloadValidationResult
{
    public required bool IsValid { get; init; }
    public IReadOnlyList<PayloadViolation>? Violations { get; init; }

    public static PayloadValidationResult Success() => new() { IsValid = true };

    public static PayloadValidationResult Failure(params PayloadViolation[] violations) => new()
    {
        IsValid = false,
        Violations = violations
    };
}

/// <summary>
/// A payload validation violation.
/// </summary>
public sealed record PayloadViolation
{
    public required PayloadViolationType Type { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public object? ActualValue { get; init; }
    public object? MaxAllowed { get; init; }
}

/// <summary>
/// Types of payload violations.
/// </summary>
public enum PayloadViolationType
{
    ContentTooLarge,
    EmbeddingDimensionMismatch,
    EmbeddingValueOutOfRange,
    ExportTooLarge,
    BatchTooLarge,
    BatchItemCountExceeded,
    ContentContainsProhibitedPatterns,
    ContentEncodingInvalid,
    QuotaExceeded
}

/// <summary>
/// Export validation request.
/// </summary>
public sealed record ExportValidationRequest
{
    public bool IncludeMemories { get; init; } = true;
    public bool IncludeEntities { get; init; } = true;
    public bool IncludeRelationships { get; init; } = true;
    public bool IncludeEvents { get; init; } = false;
    public int EstimatedRecordCount { get; init; }
}

/// <summary>
/// Payload limits for a tenant.
/// </summary>
public sealed record PayloadLimits
{
    public required int MaxMemoryContentBytes { get; init; }
    public required int MaxMemoryContentChars { get; init; }
    public required int ExpectedEmbeddingDimension { get; init; }
    public required int MaxBatchSize { get; init; }
    public required long MaxBatchTotalBytes { get; init; }
    public required long MaxExportRecords { get; init; }
    public required long MaxExportSizeBytes { get; init; }

    public static PayloadLimits Free => new()
    {
        MaxMemoryContentBytes = 32 * 1024,        // 32 KB
        MaxMemoryContentChars = 10_000,           // ~10K chars
        ExpectedEmbeddingDimension = 768,         // nomic-embed-text
        MaxBatchSize = 10,
        MaxBatchTotalBytes = 256 * 1024,          // 256 KB
        MaxExportRecords = 1_000,
        MaxExportSizeBytes = 10 * 1024 * 1024     // 10 MB
    };

    public static PayloadLimits Pro => new()
    {
        MaxMemoryContentBytes = 128 * 1024,       // 128 KB
        MaxMemoryContentChars = 50_000,           // ~50K chars
        ExpectedEmbeddingDimension = 768,
        MaxBatchSize = 100,
        MaxBatchTotalBytes = 5 * 1024 * 1024,     // 5 MB
        MaxExportRecords = 50_000,
        MaxExportSizeBytes = 500 * 1024 * 1024    // 500 MB
    };

    public static PayloadLimits Enterprise => new()
    {
        MaxMemoryContentBytes = 512 * 1024,       // 512 KB
        MaxMemoryContentChars = 200_000,          // ~200K chars
        ExpectedEmbeddingDimension = 768,
        MaxBatchSize = 1000,
        MaxBatchTotalBytes = 50 * 1024 * 1024,    // 50 MB
        MaxExportRecords = 1_000_000,
        MaxExportSizeBytes = 5L * 1024 * 1024 * 1024  // 5 GB
    };

    public static PayloadLimits SelfHosted => new()
    {
        MaxMemoryContentBytes = 1024 * 1024,      // 1 MB
        MaxMemoryContentChars = 500_000,          // ~500K chars
        ExpectedEmbeddingDimension = 768,
        MaxBatchSize = 10000,
        MaxBatchTotalBytes = 100 * 1024 * 1024,   // 100 MB
        MaxExportRecords = 10_000_000,
        MaxExportSizeBytes = 50L * 1024 * 1024 * 1024 // 50 GB
    };
}
