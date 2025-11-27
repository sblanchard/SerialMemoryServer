using System.Security.Cryptography;
using System.Text;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Privacy-safe audit logging service.
/// NEVER stores user content - only hashes, timestamps, counts, and tenant IDs.
/// </summary>
public interface IPrivacyAuditService
{
    /// <summary>Log a memory operation WITHOUT storing content.</summary>
    Task<PrivacyAuditEntry> LogMemoryOperationAsync(
        string tenantId,
        PrivacyAuditOperation operation,
        Guid memoryId,
        string contentHash,
        int? contentLength = null,
        CancellationToken ct = default);

    /// <summary>Log an access event.</summary>
    Task<PrivacyAuditEntry> LogAccessAsync(
        string tenantId,
        PrivacyAuditOperation operation,
        int itemCount,
        string? queryHash = null,
        CancellationToken ct = default);

    /// <summary>Log a batch operation.</summary>
    Task<PrivacyAuditEntry> LogBatchOperationAsync(
        string tenantId,
        PrivacyAuditOperation operation,
        int itemCount,
        int successCount,
        int failCount,
        CancellationToken ct = default);

    /// <summary>Get audit entries for a tenant.</summary>
    Task<List<PrivacyAuditEntry>> GetAuditTrailAsync(
        string tenantId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>Get aggregated statistics.</summary>
    Task<PrivacyAuditStats> GetStatsAsync(
        string tenantId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default);

    /// <summary>Verify audit chain integrity.</summary>
    Task<PrivacyAuditProof> VerifyIntegrityAsync(
        string tenantId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default);

    /// <summary>Generate cryptographic proof of audit trail.</summary>
    Task<PrivacyAuditProof> GenerateProofAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);
}

/// <summary>Operations that can be audited.</summary>
public enum PrivacyAuditOperation
{
    // Memory operations
    MemoryCreate,
    MemoryRead,
    MemoryUpdate,
    MemoryDelete,
    MemorySearch,
    MemoryExport,

    // Entity operations
    EntityCreate,
    EntityRead,
    EntityUpdate,
    EntityDelete,

    // Batch operations
    BatchIngest,
    BatchExport,
    BatchReembed,

    // Admin operations
    AdminAccess,
    SettingsChange,
    SchemaChange,

    // Security operations
    IntegrityCheck,
    ContradictionScan,
    HallucinationScan
}

/// <summary>
/// Privacy-safe audit entry. NEVER contains user content.
/// </summary>
public sealed class PrivacyAuditEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public long SequenceNumber { get; init; }
    public string TenantId { get; init; } = "";
    public PrivacyAuditOperation Operation { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    // Hashes only - never raw content
    public string? ContentHash { get; init; }
    public string? QueryHash { get; init; }
    public Guid? TargetMemoryId { get; init; }

    // Counts only - no content
    public int? ItemCount { get; init; }
    public int? ContentLength { get; init; }
    public int? SuccessCount { get; init; }
    public int? FailCount { get; init; }

    // Chain integrity
    public string PreviousHash { get; init; } = "";
    public string EntryHash { get; init; } = "";

    /// <summary>Compute entry hash for chain integrity.</summary>
    public static string ComputeEntryHash(
        string tenantId,
        PrivacyAuditOperation operation,
        DateTimeOffset timestamp,
        string? contentHash,
        string? queryHash,
        Guid? targetMemoryId,
        int? itemCount,
        string previousHash)
    {
        var content = string.Join("|",
            tenantId,
            operation.ToString(),
            timestamp.ToString("O"),
            contentHash ?? "",
            queryHash ?? "",
            targetMemoryId?.ToString() ?? "",
            itemCount?.ToString() ?? "",
            previousHash);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>Aggregated privacy-safe statistics.</summary>
public sealed class PrivacyAuditStats
{
    public string TenantId { get; init; } = "";
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }

    // Counts only
    public int TotalOperations { get; init; }
    public Dictionary<string, int> OperationCounts { get; init; } = [];
    public int TotalMemoriesAccessed { get; init; }
    public int TotalSearches { get; init; }
    public int TotalExports { get; init; }

    // Time-based aggregates
    public Dictionary<string, int> HourlyCounts { get; init; } = [];
    public Dictionary<string, int> DailyCounts { get; init; } = [];
}

/// <summary>Cryptographic proof of audit trail integrity.</summary>
public sealed class PrivacyAuditProof
{
    public Guid ProofId { get; init; } = Guid.CreateVersion7();
    public string TenantId { get; init; } = "";
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    // Proof data
    public bool IsValid { get; init; }
    public int TotalEntries { get; init; }
    public int VerifiedEntries { get; init; }
    public string FirstEntryHash { get; init; } = "";
    public string LastEntryHash { get; init; } = "";
    public string MerkleRoot { get; init; } = "";

    // Integrity issues (if any)
    public List<PrivacyAuditIssue> Issues { get; init; } = [];

    // Signature for external verification
    public string ProofSignature { get; init; } = "";
}

/// <summary>Integrity issue in audit trail.</summary>
public sealed class PrivacyAuditIssue
{
    public long SequenceNumber { get; init; }
    public string IssueType { get; init; } = "";
    public string ExpectedHash { get; init; } = "";
    public string ActualHash { get; init; } = "";
}
