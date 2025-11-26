namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for generating compliance proofs and verification.
/// Provides cryptographic proof of tenant isolation, audit integrity, and retention compliance.
/// </summary>
public interface IProofService
{
    /// <summary>
    /// Verifies tenant isolation by checking RLS policies are enforced.
    /// </summary>
    Task<TenantIsolationProof> VerifyTenantIsolationAsync(
        string tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies the integrity of the admin audit log hash chain.
    /// </summary>
    Task<HashChainProof> VerifyAuditHashChainAsync(
        string? tenantId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies retention policy enforcement for a tenant.
    /// </summary>
    Task<RetentionProof> VerifyRetentionEnforcementAsync(
        string tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a complete compliance report for a tenant.
    /// </summary>
    Task<ComplianceReport> GenerateComplianceReportAsync(
        string tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies data integrity using content hashes.
    /// </summary>
    Task<DataIntegrityProof> VerifyDataIntegrityAsync(
        string tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Gets proof of data residency (where data is stored).
    /// </summary>
    Task<DataResidencyProof> GetDataResidencyProofAsync(
        string tenantId,
        CancellationToken ct = default);
}

/// <summary>
/// Proof of tenant isolation enforcement.
/// </summary>
public sealed record TenantIsolationProof
{
    public required string TenantId { get; init; }
    public required bool IsIsolated { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }
    public required IReadOnlyList<IsolationCheck> Checks { get; init; }
    public required string ProofHash { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Individual isolation check result.
/// </summary>
public sealed record IsolationCheck
{
    public required string TableName { get; init; }
    public required string CheckType { get; init; }  // 'rls_enabled', 'policy_exists', 'cross_tenant_blocked'
    public required bool Passed { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// Proof of audit log hash chain integrity.
/// </summary>
public sealed record HashChainProof
{
    public required bool IsValid { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }
    public required long TotalRecords { get; init; }
    public required long VerifiedRecords { get; init; }
    public required string FirstRecordHash { get; init; }
    public required string LastRecordHash { get; init; }
    public required IReadOnlyList<HashChainBreak> Breaks { get; init; }
    public required string ProofHash { get; init; }
}

/// <summary>
/// A break in the hash chain.
/// </summary>
public sealed record HashChainBreak
{
    public required long SequenceNumber { get; init; }
    public required string ExpectedPreviousHash { get; init; }
    public required string ActualPreviousHash { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Proof of retention policy enforcement.
/// </summary>
public sealed record RetentionProof
{
    public required string TenantId { get; init; }
    public required bool IsCompliant { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }
    public required RetentionPolicy Policy { get; init; }
    public required IReadOnlyList<RetentionViolation> Violations { get; init; }
    public required string ProofHash { get; init; }
}

/// <summary>
/// Retention policy configuration.
/// </summary>
public sealed record RetentionPolicy
{
    public required int MemoryRetentionDays { get; init; }
    public required int AuditLogRetentionDays { get; init; }
    public required int DeletedDataRetentionDays { get; init; }
    public required bool AutoDeleteEnabled { get; init; }
}

/// <summary>
/// A retention policy violation.
/// </summary>
public sealed record RetentionViolation
{
    public required string TableName { get; init; }
    public required string ViolationType { get; init; }  // 'over_retention', 'under_retention', 'missing_policy'
    public required long AffectedRecords { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// Complete compliance report for a tenant.
/// </summary>
public sealed record ComplianceReport
{
    public required string TenantId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required TenantIsolationProof IsolationProof { get; init; }
    public required HashChainProof HashChainProof { get; init; }
    public required RetentionProof RetentionProof { get; init; }
    public required DataIntegrityProof DataIntegrityProof { get; init; }
    public required ComplianceScore OverallScore { get; init; }
    public required IReadOnlyList<ComplianceRecommendation> Recommendations { get; init; }
    public required string ReportHash { get; init; }
}

/// <summary>
/// Compliance score breakdown.
/// </summary>
public sealed record ComplianceScore
{
    public required int TotalPoints { get; init; }
    public required int EarnedPoints { get; init; }
    public required decimal Percentage { get; init; }
    public required ComplianceGrade Grade { get; init; }
    public required IReadOnlyDictionary<string, int> CategoryScores { get; init; }
}

/// <summary>
/// Compliance grade.
/// </summary>
public enum ComplianceGrade
{
    A,  // 90-100%
    B,  // 80-89%
    C,  // 70-79%
    D,  // 60-69%
    F   // Below 60%
}

/// <summary>
/// Compliance recommendation.
/// </summary>
public sealed record ComplianceRecommendation
{
    public required string Category { get; init; }
    public required string Severity { get; init; }  // 'critical', 'high', 'medium', 'low'
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Remediation { get; init; }
}

/// <summary>
/// Proof of data integrity.
/// </summary>
public sealed record DataIntegrityProof
{
    public required string TenantId { get; init; }
    public required bool IsValid { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }
    public required long TotalRecords { get; init; }
    public required long ValidRecords { get; init; }
    public required long CorruptedRecords { get; init; }
    public required IReadOnlyList<IntegrityViolation> Violations { get; init; }
    public required string ProofHash { get; init; }
}

/// <summary>
/// Data integrity violation.
/// </summary>
public sealed record IntegrityViolation
{
    public required string RecordId { get; init; }
    public required string TableName { get; init; }
    public required string ExpectedHash { get; init; }
    public required string ActualHash { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
}

/// <summary>
/// Proof of data residency.
/// </summary>
public sealed record DataResidencyProof
{
    public required string TenantId { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }
    public required string Region { get; init; }
    public required string DataCenter { get; init; }
    public required bool IsCompliantWithPolicy { get; init; }
    public required IReadOnlyList<DataLocation> DataLocations { get; init; }
    public required string ProofHash { get; init; }
}

/// <summary>
/// Location of data storage.
/// </summary>
public sealed record DataLocation
{
    public required string DataType { get; init; }  // 'memories', 'embeddings', 'audit_logs'
    public required string StorageType { get; init; }  // 'primary', 'backup', 'archive'
    public required string Region { get; init; }
    public required string Provider { get; init; }
    public required bool IsEncryptedAtRest { get; init; }
    public required bool IsEncryptedInTransit { get; init; }
}
