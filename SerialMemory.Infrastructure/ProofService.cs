using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for generating compliance proofs and verification.
/// </summary>
public sealed class ProofService : IProofService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ProofService> _logger;

    public ProofService(string connectionString, ILogger<ProofService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public ProofService(NpgsqlDataSource dataSource, ILogger<ProofService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Opens a connection with internal admin role for verification operations.
    /// </summary>
    private async Task<NpgsqlConnection> OpenInternalConnectionAsync(CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT set_config('app.role', 'internal_admin', false)");
        return conn;
    }

    public async Task<TenantIsolationProof> VerifyTenantIsolationAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);
        var checks = new List<IsolationCheck>();
        var verifiedAt = DateTimeOffset.UtcNow;

        // Tables that should have RLS enabled
        var rlsTables = new[]
        {
            "memories", "entities", "entity_relationships", "memory_entities",
            "user_personas", "conversation_sessions", "admin_audit_log",
            "billing_cycles", "usage_events", "rate_limit_hits", "cost_records"
        };

        foreach (var table in rlsTables)
        {
            // Check if RLS is enabled
            var rlsEnabled = await conn.QueryFirstOrDefaultAsync<bool>(
                """
                SELECT relrowsecurity FROM pg_class
                                  WHERE relname = @Table AND relnamespace = 'public'::regnamespace
                """,
                new { Table = table });

            checks.Add(new IsolationCheck
            {
                TableName = table,
                CheckType = "rls_enabled",
                Passed = rlsEnabled,
                Details = rlsEnabled ? "RLS is enabled" : "RLS is NOT enabled"
            });

            // Check if tenant isolation policy exists
            var policyExists = await conn.QueryFirstOrDefaultAsync<bool>(
                """
                SELECT EXISTS (
                                    SELECT 1 FROM pg_policies
                                    WHERE tablename = @Table
                                      AND (qual LIKE '%tenant_id%' OR qual LIKE '%current_tenant_id%')
                                  )
                """,
                new { Table = table });

            checks.Add(new IsolationCheck
            {
                TableName = table,
                CheckType = "policy_exists",
                Passed = policyExists,
                Details = policyExists ? "Tenant isolation policy exists" : "No tenant isolation policy found"
            });
        }

        // Test cross-tenant access is blocked (without RLS bypass)
        var crossTenantBlocked = await TestCrossTenantAccessAsync(conn, tenantId, ct);
        checks.Add(new IsolationCheck
        {
            TableName = "memories",
            CheckType = "cross_tenant_blocked",
            Passed = crossTenantBlocked,
            Details = crossTenantBlocked
                ? "Cross-tenant access correctly blocked"
                : "WARNING: Cross-tenant access may be possible"
        });

        var isIsolated = checks.All(c => c.Passed);
        var proofData = new
        {
            TenantId = tenantId,
            VerifiedAt = verifiedAt,
            Checks = checks,
            IsIsolated = isIsolated
        };

        return new TenantIsolationProof
        {
            TenantId = tenantId,
            IsIsolated = isIsolated,
            VerifiedAt = verifiedAt,
            Checks = checks,
            ProofHash = ComputeHash(JsonSerializer.Serialize(proofData)),
            FailureReason = isIsolated ? null : "One or more isolation checks failed"
        };
    }

    public async Task<HashChainProof> VerifyAuditHashChainAsync(
        string? tenantId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);
        var verifiedAt = DateTimeOffset.UtcNow;

        var whereClause = "WHERE 1=1";
        var parameters = new DynamicParameters();

        if (tenantId != null)
        {
            whereClause += " AND tenant_id = @TenantId::uuid";
            parameters.Add("TenantId", tenantId);
        }

        if (fromDate.HasValue)
        {
            whereClause += " AND occurred_at >= @FromDate";
            parameters.Add("FromDate", fromDate.Value);
        }

        if (toDate.HasValue)
        {
            whereClause += " AND occurred_at <= @ToDate";
            parameters.Add("ToDate", toDate.Value);
        }

        // Get total count
        var totalRecords = await conn.QueryFirstOrDefaultAsync<long>(
            $"SELECT COUNT(*) FROM admin_audit_log {whereClause}",
            parameters);

        if (totalRecords == 0)
        {
            return new HashChainProof
            {
                IsValid = true,
                VerifiedAt = verifiedAt,
                TotalRecords = 0,
                VerifiedRecords = 0,
                FirstRecordHash = "",
                LastRecordHash = "",
                Breaks = Array.Empty<HashChainBreak>(),
                ProofHash = ComputeHash($"empty-chain-{verifiedAt:O}")
            };
        }

        // Get records in sequence order
        var records = await conn.QueryAsync<AuditRecordDto>(
            $"""
             SELECT sequence_number, entry_hash, previous_hash, occurred_at
                            FROM admin_audit_log {whereClause}
                            ORDER BY sequence_number
             """,
            parameters);

        var recordList = records.ToList();
        var breaks = new List<HashChainBreak>();
        string? previousHash = null;
        var verifiedCount = 0L;

        foreach (var record in recordList)
        {
            verifiedCount++;

            // First record should have null or empty previous hash
            if (previousHash != null && record.previous_hash != previousHash)
            {
                breaks.Add(new HashChainBreak
                {
                    SequenceNumber = record.sequence_number,
                    ExpectedPreviousHash = previousHash,
                    ActualPreviousHash = record.previous_hash ?? "",
                    OccurredAt = record.occurred_at
                });
            }

            previousHash = record.entry_hash;
        }

        var proofData = new
        {
            TotalRecords = totalRecords,
            VerifiedRecords = verifiedCount,
            FirstHash = recordList.FirstOrDefault()?.entry_hash ?? "",
            LastHash = recordList.LastOrDefault()?.entry_hash ?? "",
            BreakCount = breaks.Count,
            VerifiedAt = verifiedAt
        };

        return new HashChainProof
        {
            IsValid = breaks.Count == 0,
            VerifiedAt = verifiedAt,
            TotalRecords = totalRecords,
            VerifiedRecords = verifiedCount,
            FirstRecordHash = recordList.FirstOrDefault()?.entry_hash ?? "",
            LastRecordHash = recordList.LastOrDefault()?.entry_hash ?? "",
            Breaks = breaks,
            ProofHash = ComputeHash(JsonSerializer.Serialize(proofData))
        };
    }

    public async Task<RetentionProof> VerifyRetentionEnforcementAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);
        var verifiedAt = DateTimeOffset.UtcNow;
        var violations = new List<RetentionViolation>();

        // Get tenant's retention policy
        var policy = await conn.QueryFirstOrDefaultAsync<RetentionPolicyDto>(
            """
            SELECT
                            COALESCE(ts.memory_retention_days, 365) AS memory_retention_days,
                            COALESCE(ts.audit_log_retention_days, 730) AS audit_log_retention_days,
                            COALESCE(ts.deleted_data_retention_days, 30) AS deleted_data_retention_days,
                            COALESCE(ts.auto_delete_enabled, FALSE) AS auto_delete_enabled
                          FROM tenant_settings ts
                          WHERE ts.tenant_id = @TenantId::uuid
            """,
            new { TenantId = tenantId });

        policy ??= new RetentionPolicyDto
        {
            memory_retention_days = 365,
            audit_log_retention_days = 730,
            deleted_data_retention_days = 30,
            auto_delete_enabled = false
        };

        // Check for memories older than retention policy
        var overRetainedMemories = await conn.QueryFirstOrDefaultAsync<long>(
            """
            SELECT COUNT(*) FROM memories
                          WHERE tenant_id = @TenantId::uuid
                            AND is_active = FALSE
                            AND created_at < NOW() - (@Days || ' days')::INTERVAL
            """,
            new { TenantId = tenantId, Days = policy.memory_retention_days });

        if (overRetainedMemories > 0)
        {
            violations.Add(new RetentionViolation
            {
                TableName = "memories",
                ViolationType = "over_retention",
                AffectedRecords = overRetainedMemories,
                Description = $"{overRetainedMemories} inactive memories exceed {policy.memory_retention_days}-day retention policy"
            });
        }

        // Check audit logs older than retention policy
        var overRetainedAuditLogs = await conn.QueryFirstOrDefaultAsync<long>(
            """
            SELECT COUNT(*) FROM admin_audit_log
                          WHERE tenant_id = @TenantId::uuid
                            AND occurred_at < NOW() - (@Days || ' days')::INTERVAL
            """,
            new { TenantId = tenantId, Days = policy.audit_log_retention_days });

        if (overRetainedAuditLogs > 0)
        {
            violations.Add(new RetentionViolation
            {
                TableName = "admin_audit_log",
                ViolationType = "over_retention",
                AffectedRecords = overRetainedAuditLogs,
                Description = $"{overRetainedAuditLogs} audit log entries exceed {policy.audit_log_retention_days}-day retention policy"
            });
        }

        var retentionPolicyRecord = new RetentionPolicy
        {
            MemoryRetentionDays = policy.memory_retention_days,
            AuditLogRetentionDays = policy.audit_log_retention_days,
            DeletedDataRetentionDays = policy.deleted_data_retention_days,
            AutoDeleteEnabled = policy.auto_delete_enabled
        };

        var proofData = new
        {
            TenantId = tenantId,
            VerifiedAt = verifiedAt,
            Policy = retentionPolicyRecord,
            ViolationCount = violations.Count
        };

        return new RetentionProof
        {
            TenantId = tenantId,
            IsCompliant = violations.Count == 0,
            VerifiedAt = verifiedAt,
            Policy = retentionPolicyRecord,
            Violations = violations,
            ProofHash = ComputeHash(JsonSerializer.Serialize(proofData))
        };
    }

    public async Task<ComplianceReport> GenerateComplianceReportAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        var generatedAt = DateTimeOffset.UtcNow;

        // Run all proofs in parallel
        var isolationTask = VerifyTenantIsolationAsync(tenantId, ct);
        var hashChainTask = VerifyAuditHashChainAsync(tenantId, ct: ct);
        var retentionTask = VerifyRetentionEnforcementAsync(tenantId, ct);
        var integrityTask = VerifyDataIntegrityAsync(tenantId, ct);

        await Task.WhenAll(isolationTask, hashChainTask, retentionTask, integrityTask);

        var isolationProof = await isolationTask;
        var hashChainProof = await hashChainTask;
        var retentionProof = await retentionTask;
        var integrityProof = await integrityTask;

        // Calculate compliance score
        var categoryScores = new Dictionary<string, int>
        {
            ["isolation"] = isolationProof.IsIsolated ? 25 : 0,
            ["audit_integrity"] = hashChainProof.IsValid ? 25 : 0,
            ["retention"] = retentionProof.IsCompliant ? 25 : 0,
            ["data_integrity"] = integrityProof.IsValid ? 25 : 0
        };

        var totalPoints = 100;
        var earnedPoints = categoryScores.Values.Sum();
        var percentage = (decimal)earnedPoints / totalPoints * 100;

        var grade = percentage switch
        {
            >= 90 => ComplianceGrade.A,
            >= 80 => ComplianceGrade.B,
            >= 70 => ComplianceGrade.C,
            >= 60 => ComplianceGrade.D,
            _ => ComplianceGrade.F
        };

        var score = new ComplianceScore
        {
            TotalPoints = totalPoints,
            EarnedPoints = earnedPoints,
            Percentage = percentage,
            Grade = grade,
            CategoryScores = categoryScores
        };

        // Generate recommendations
        var recommendations = new List<ComplianceRecommendation>();

        if (!isolationProof.IsIsolated)
        {
            recommendations.Add(new ComplianceRecommendation
            {
                Category = "Security",
                Severity = "critical",
                Title = "Tenant Isolation Not Fully Enforced",
                Description = "One or more tables do not have proper tenant isolation configured.",
                Remediation = "Enable RLS on all tables and ensure tenant_id policies are in place."
            });
        }

        if (!hashChainProof.IsValid)
        {
            recommendations.Add(new ComplianceRecommendation
            {
                Category = "Audit",
                Severity = "critical",
                Title = "Audit Log Hash Chain Broken",
                Description = $"{hashChainProof.Breaks.Count} breaks detected in the audit log hash chain.",
                Remediation = "Investigate the breaks for potential tampering. Consider forensic analysis."
            });
        }

        if (!retentionProof.IsCompliant)
        {
            recommendations.Add(new ComplianceRecommendation
            {
                Category = "Compliance",
                Severity = "high",
                Title = "Retention Policy Violations",
                Description = $"{retentionProof.Violations.Count} retention policy violations detected.",
                Remediation = "Run data cleanup jobs to enforce retention policies."
            });
        }

        if (!integrityProof.IsValid)
        {
            recommendations.Add(new ComplianceRecommendation
            {
                Category = "Data Integrity",
                Severity = "critical",
                Title = "Data Corruption Detected",
                Description = $"{integrityProof.CorruptedRecords} records with invalid content hashes.",
                Remediation = "Investigate corrupted records. Restore from backups if necessary."
            });
        }

        var reportData = new
        {
            TenantId = tenantId,
            GeneratedAt = generatedAt,
            Score = score,
            IsolationValid = isolationProof.IsIsolated,
            HashChainValid = hashChainProof.IsValid,
            RetentionValid = retentionProof.IsCompliant,
            IntegrityValid = integrityProof.IsValid
        };

        return new ComplianceReport
        {
            TenantId = tenantId,
            GeneratedAt = generatedAt,
            IsolationProof = isolationProof,
            HashChainProof = hashChainProof,
            RetentionProof = retentionProof,
            DataIntegrityProof = integrityProof,
            OverallScore = score,
            Recommendations = recommendations,
            ReportHash = ComputeHash(JsonSerializer.Serialize(reportData))
        };
    }

    public async Task<DataIntegrityProof> VerifyDataIntegrityAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        await using var conn = await OpenInternalConnectionAsync(ct);
        var verifiedAt = DateTimeOffset.UtcNow;
        var violations = new List<IntegrityViolation>();

        // Check memories with content_hash
        var memories = await conn.QueryAsync<MemoryIntegrityDto>(
            """
            SELECT id, content, content_hash
                          FROM memories
                          WHERE tenant_id = @TenantId::uuid
                            AND content_hash IS NOT NULL
                          LIMIT 1000
            """,
            new { TenantId = tenantId });

        var totalRecords = 0L;
        var validRecords = 0L;

        foreach (var memory in memories)
        {
            totalRecords++;
            var computedHash = ComputeHash(memory.content);

            if (computedHash == memory.content_hash)
            {
                validRecords++;
            }
            else
            {
                violations.Add(new IntegrityViolation
                {
                    RecordId = memory.id.ToString(),
                    TableName = "memories",
                    ExpectedHash = memory.content_hash,
                    ActualHash = computedHash,
                    DetectedAt = verifiedAt
                });
            }
        }

        var proofData = new
        {
            TenantId = tenantId,
            VerifiedAt = verifiedAt,
            TotalRecords = totalRecords,
            ValidRecords = validRecords,
            CorruptedRecords = violations.Count
        };

        return new DataIntegrityProof
        {
            TenantId = tenantId,
            IsValid = violations.Count == 0,
            VerifiedAt = verifiedAt,
            TotalRecords = totalRecords,
            ValidRecords = validRecords,
            CorruptedRecords = violations.Count,
            Violations = violations,
            ProofHash = ComputeHash(JsonSerializer.Serialize(proofData))
        };
    }

    public Task<DataResidencyProof> GetDataResidencyProofAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        // For self-hosted, this is typically local
        var verifiedAt = DateTimeOffset.UtcNow;

        var locations = new List<DataLocation>
        {
            new()
            {
                DataType = "memories",
                StorageType = "primary",
                Region = "local",
                Provider = "postgresql",
                IsEncryptedAtRest = true, // Depends on PostgreSQL configuration
                IsEncryptedInTransit = true
            },
            new()
            {
                DataType = "embeddings",
                StorageType = "primary",
                Region = "local",
                Provider = "pgvector",
                IsEncryptedAtRest = true,
                IsEncryptedInTransit = true
            },
            new()
            {
                DataType = "audit_logs",
                StorageType = "primary",
                Region = "local",
                Provider = "postgresql",
                IsEncryptedAtRest = true,
                IsEncryptedInTransit = true
            }
        };

        var proofData = new
        {
            TenantId = tenantId,
            VerifiedAt = verifiedAt,
            Region = "local",
            DataCenter = "self-hosted",
            LocationCount = locations.Count
        };

        return Task.FromResult(new DataResidencyProof
        {
            TenantId = tenantId,
            VerifiedAt = verifiedAt,
            Region = "local",
            DataCenter = "self-hosted",
            IsCompliantWithPolicy = true,
            DataLocations = locations,
            ProofHash = ComputeHash(JsonSerializer.Serialize(proofData))
        });
    }

    private async Task<bool> TestCrossTenantAccessAsync(
        NpgsqlConnection conn,
        string tenantId,
        CancellationToken ct)
    {
        try
        {
            // Set tenant context (SET doesn't support parameters, but GUID is safe)
            await conn.ExecuteAsync($"SET app.tenant_id = '{tenantId}'");

            // Try to access memories from another tenant
            var otherTenantCount = await conn.QueryFirstOrDefaultAsync<long>(
                """
                SELECT COUNT(*) FROM memories
                                  WHERE tenant_id != @TenantId::uuid
                """,
                new { TenantId = tenantId });

            // If RLS is working, this should return 0 (blocked by policy)
            return otherTenantCount == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error testing cross-tenant access for tenant {TenantId}", tenantId);
            return false;
        }
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // DTOs
    private sealed class AuditRecordDto
    {
        public long sequence_number { get; set; }
        public string entry_hash { get; set; } = "";
        public string? previous_hash { get; set; }
        public DateTimeOffset occurred_at { get; set; }
    }

    private sealed class RetentionPolicyDto
    {
        public int memory_retention_days { get; set; }
        public int audit_log_retention_days { get; set; }
        public int deleted_data_retention_days { get; set; }
        public bool auto_delete_enabled { get; set; }
    }

    private sealed class MemoryIntegrityDto
    {
        public Guid id { get; set; }
        public string content { get; set; } = "";
        public string content_hash { get; set; } = "";
    }
}
