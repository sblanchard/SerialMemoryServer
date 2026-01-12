namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for scanning and detecting contradictions between memories.
/// FIX #2: Conflicts & Contradictions - Run Detection Scan must actually run.
/// </summary>
public interface IConflictScanner
{
    /// <summary>
    /// Run a full contradiction scan for the tenant.
    /// Compares memories using semantic similarity and content analysis.
    /// </summary>
    /// <param name="tenantId">Tenant to scan</param>
    /// <param name="threshold">Similarity threshold (0.0-1.0) for contradiction detection</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Scan results with detected contradictions</returns>
    Task<ConflictScanResult> RunScanAsync(
        Guid tenantId,
        float threshold = 0.85f,
        CancellationToken ct = default);

    /// <summary>
    /// Get existing conflicts for a tenant.
    /// </summary>
    Task<IReadOnlyList<ConflictRecord>> GetConflictsAsync(
        Guid tenantId,
        bool includeResolved = false,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve a conflict.
    /// </summary>
    Task<bool> ResolveConflictAsync(
        Guid tenantId,
        Guid conflictId,
        ScanConflictResolution action,
        string? resolution = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get conflict statistics for a tenant.
    /// </summary>
    Task<ConflictStats> GetStatsAsync(
        Guid tenantId,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a conflict scan operation.
/// </summary>
public sealed class ConflictScanResult
{
    public Guid ScanId { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public int MemoriesScanned { get; init; }
    public int ConflictsFound { get; init; }
    /// <summary>Alias for ConflictsFound for endpoint compatibility</summary>
    public int ConflictsDetected => ConflictsFound;
    public int NewConflicts { get; init; }
    public int DuplicateConflicts { get; init; }
    public List<ConflictRecord> Conflicts { get; init; } = [];
    public TimeSpan Duration => CompletedAt - StartedAt;
    public string Status { get; init; } = "completed";
}

/// <summary>
/// A detected conflict/contradiction between two memories.
/// </summary>
public sealed class ConflictRecord
{
    public Guid Id { get; init; }
    public Guid MemoryAId { get; init; }
    public Guid MemoryBId { get; init; }
    public string? MemoryAPreview { get; init; }
    public string? MemoryBPreview { get; init; }
    public float SimilarityScore { get; init; }
    public string ConflictType { get; init; } = "contradiction";
    public string? Description { get; init; }
    public float Severity { get; init; }
    public string Status { get; init; } = "unresolved";
    public string? Resolution { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}

/// <summary>
/// Action to take when resolving a scan-detected conflict.
/// </summary>
public enum ScanConflictResolution
{
    /// <summary>Keep memory A, invalidate memory B</summary>
    KeepA,
    /// <summary>Keep memory B, invalidate memory A</summary>
    KeepB,
    /// <summary>Keep both memories, mark conflict as accepted</summary>
    KeepBoth,
    /// <summary>Merge both memories into a new one</summary>
    Merge,
    /// <summary>Invalidate both memories</summary>
    InvalidateBoth,
    /// <summary>Mark as false positive</summary>
    Dismiss
}

/// <summary>
/// Statistics about conflicts for a tenant.
/// </summary>
public sealed class ConflictStats
{
    public int TotalConflicts { get; init; }
    public int UnresolvedCount { get; init; }
    public int ResolvedCount { get; init; }
    public int DismissedCount { get; init; }
    public float AvgSimilarity { get; init; }
    public float AvgSeverity { get; init; }
    public DateTimeOffset? LastScanAt { get; init; }
    public int ConflictsLast7Days { get; init; }
    public int ConflictsLast30Days { get; init; }
}
