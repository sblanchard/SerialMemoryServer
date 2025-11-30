using System.Text.Json.Serialization;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for computing and verifying cryptographic integrity proofs.
/// Provides tamper-evident hashing and chain verification for memories.
/// </summary>
public interface IIntegrityService
{
    /// <summary>
    /// Computes an integrity proof for a memory record.
    /// </summary>
    Task<IntegrityProof> ComputeProofAsync(MemoryIntegrityRecord memory, CancellationToken ct = default);

    /// <summary>
    /// Verifies the integrity proof for a single memory.
    /// </summary>
    Task<IntegrityVerificationResult> VerifyProofAsync(Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Verifies the entire hash chain for a tenant.
    /// </summary>
    Task<ChainVerificationResult> VerifyChainAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the stored proof bundle for a memory.
    /// </summary>
    Task<IntegrityProof?> GetProofAsync(Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Recomputes and updates integrity hashes for a memory.
    /// </summary>
    Task<IntegrityProof> UpdateIntegrityAsync(Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Marks a memory as having invalid integrity.
    /// </summary>
    Task MarkInvalidAsync(Guid memoryId, string reason, CancellationToken ct = default);
}

/// <summary>
/// Record containing the minimal data needed for integrity computation.
/// </summary>
public sealed record MemoryIntegrityRecord
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Content { get; init; } = "";
    public string? Metadata { get; init; }
    public string? Source { get; init; }
    public string? PreviousChainHash { get; init; }
}

/// <summary>
/// Cryptographic integrity proof for a memory.
/// </summary>
public sealed record IntegrityProof
{
    public Guid MemoryId { get; init; }
    public Guid TenantId { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Hash algorithm used (SHA256 or BLAKE3).
    /// </summary>
    public string HashAlgorithm { get; init; } = "SHA256";

    /// <summary>
    /// SHA256/BLAKE3 hash of the canonical memory form.
    /// </summary>
    public string ContentHash { get; init; } = "";

    /// <summary>
    /// Running chain hash linking to previous memory.
    /// ChainHash = Hash(PreviousChainHash + ContentHash)
    /// </summary>
    public string ChainHash { get; init; } = "";

    /// <summary>
    /// Chain hash of the previous memory in sequence.
    /// "GENESIS" for the first memory.
    /// </summary>
    public string PreviousChainHash { get; init; } = "GENESIS";

    /// <summary>
    /// The canonical form used to compute the content hash.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? CanonicalForm { get; init; }

    /// <summary>
    /// When the proof was computed.
    /// </summary>
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Version of the proof format.
    /// </summary>
    public int ProofVersion { get; init; } = 1;
}

/// <summary>
/// Result of verifying a single memory's integrity.
/// </summary>
public sealed record IntegrityVerificationResult
{
    public Guid MemoryId { get; init; }
    public bool IsValid { get; init; }
    public string Status { get; init; } = "valid"; // valid, invalid, orphan, pending
    public string? ExpectedContentHash { get; init; }
    public string? ActualContentHash { get; init; }
    public string? ExpectedChainHash { get; init; }
    public string? ActualChainHash { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset VerifiedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result of verifying an entire tenant's hash chain.
/// </summary>
public sealed record ChainVerificationResult
{
    public Guid TenantId { get; init; }
    public Guid VerificationRunId { get; init; }
    public bool IsValid { get; init; }
    public int TotalMemories { get; init; }
    public int VerifiedCount { get; init; }
    public int InvalidCount { get; init; }
    public int OrphanCount { get; init; }
    public int PendingCount { get; init; }
    public IReadOnlyList<Guid> InvalidMemoryIds { get; init; } = [];
    public IReadOnlyList<Guid> OrphanMemoryIds { get; init; } = [];
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Duration => CompletedAt - StartedAt;
    public string? ErrorMessage { get; init; }
}
