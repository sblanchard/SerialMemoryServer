using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for validating payloads and protecting against abuse.
/// </summary>
public sealed class PayloadValidationService : IPayloadValidationService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PayloadValidationService> _logger;

    public PayloadValidationService(string connectionString, ILogger<PayloadValidationService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public PayloadValidationService(NpgsqlDataSource dataSource, ILogger<PayloadValidationService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<PayloadValidationResult> ValidateMemoryContentAsync(
        string tenantId,
        string content,
        CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(tenantId, ct);
        var violations = new List<PayloadViolation>();

        // Check content is not null/empty
        if (string.IsNullOrWhiteSpace(content))
        {
            return PayloadValidationResult.Failure(new PayloadViolation
            {
                Type = PayloadViolationType.ContentEncodingInvalid,
                Code = "CONTENT_EMPTY",
                Message = "Memory content cannot be empty"
            });
        }

        // Check character length
        if (content.Length > limits.MaxMemoryContentChars)
        {
            violations.Add(new PayloadViolation
            {
                Type = PayloadViolationType.ContentTooLarge,
                Code = "CONTENT_TOO_LONG",
                Message = $"Memory content exceeds maximum character limit of {limits.MaxMemoryContentChars:N0}",
                ActualValue = content.Length,
                MaxAllowed = limits.MaxMemoryContentChars
            });
        }

        // Check byte size (UTF-8)
        var byteCount = Encoding.UTF8.GetByteCount(content);
        if (byteCount > limits.MaxMemoryContentBytes)
        {
            violations.Add(new PayloadViolation
            {
                Type = PayloadViolationType.ContentTooLarge,
                Code = "CONTENT_BYTES_EXCEEDED",
                Message = $"Memory content exceeds maximum byte size of {limits.MaxMemoryContentBytes:N0} bytes",
                ActualValue = byteCount,
                MaxAllowed = limits.MaxMemoryContentBytes
            });
        }

        // Check for prohibited patterns (potential injection attacks, etc.)
        var prohibitedPatterns = CheckProhibitedPatterns(content);
        if (prohibitedPatterns.Any())
        {
            violations.Add(new PayloadViolation
            {
                Type = PayloadViolationType.ContentContainsProhibitedPatterns,
                Code = "PROHIBITED_CONTENT",
                Message = "Memory content contains prohibited patterns",
                ActualValue = string.Join(", ", prohibitedPatterns)
            });

            _logger.LogWarning(
                "Tenant {TenantId} attempted to submit content with prohibited patterns: {Patterns}",
                tenantId, string.Join(", ", prohibitedPatterns));
        }

        return violations.Count == 0
            ? PayloadValidationResult.Success()
            : PayloadValidationResult.Failure(violations.ToArray());
    }

    public Task<PayloadValidationResult> ValidateEmbeddingAsync(
        string tenantId,
        float[] embedding,
        CancellationToken ct = default)
    {
        return ValidateEmbeddingInternalAsync(tenantId, embedding, ct);
    }

    private async Task<PayloadValidationResult> ValidateEmbeddingInternalAsync(
        string tenantId,
        float[] embedding,
        CancellationToken ct)
    {
        var limits = await GetLimitsAsync(tenantId, ct);
        var violations = new List<PayloadViolation>();

        // Check embedding is not null/empty
        if (embedding == null || embedding.Length == 0)
        {
            return PayloadValidationResult.Failure(new PayloadViolation
            {
                Type = PayloadViolationType.EmbeddingDimensionMismatch,
                Code = "EMBEDDING_EMPTY",
                Message = "Embedding cannot be empty"
            });
        }

        // Check dimension matches expected
        if (embedding.Length != limits.ExpectedEmbeddingDimension)
        {
            violations.Add(new PayloadViolation
            {
                Type = PayloadViolationType.EmbeddingDimensionMismatch,
                Code = "EMBEDDING_DIMENSION_MISMATCH",
                Message = $"Embedding dimension {embedding.Length} does not match expected {limits.ExpectedEmbeddingDimension}",
                ActualValue = embedding.Length,
                MaxAllowed = limits.ExpectedEmbeddingDimension
            });
        }

        // Check values are normalized (typically -1 to 1 or 0 to 1)
        var outOfRangeCount = 0;
        foreach (var value in embedding)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                violations.Add(new PayloadViolation
                {
                    Type = PayloadViolationType.EmbeddingValueOutOfRange,
                    Code = "EMBEDDING_INVALID_VALUE",
                    Message = "Embedding contains NaN or Infinity values"
                });
                break;
            }

            if (value < -10f || value > 10f)
            {
                outOfRangeCount++;
            }
        }

        if (outOfRangeCount > embedding.Length * 0.1)
        {
            violations.Add(new PayloadViolation
            {
                Type = PayloadViolationType.EmbeddingValueOutOfRange,
                Code = "EMBEDDING_UNNORMALIZED",
                Message = $"Embedding appears unnormalized: {outOfRangeCount} values outside [-10, 10] range",
                ActualValue = outOfRangeCount
            });
        }

        return violations.Count == 0
            ? PayloadValidationResult.Success()
            : PayloadValidationResult.Failure(violations.ToArray());
    }

    public async Task<PayloadValidationResult> ValidateExportRequestAsync(
        string tenantId,
        ExportValidationRequest request,
        CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(tenantId, ct);
        var violations = new List<PayloadViolation>();

        // Check if export would exceed record limit
        if (request.EstimatedRecordCount > limits.MaxExportRecords)
        {
            violations.Add(new PayloadViolation
            {
                Type = PayloadViolationType.ExportTooLarge,
                Code = "EXPORT_RECORDS_EXCEEDED",
                Message = $"Export would exceed maximum record limit of {limits.MaxExportRecords:N0}",
                ActualValue = request.EstimatedRecordCount,
                MaxAllowed = limits.MaxExportRecords
            });
        }

        // Get actual counts from database
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var actualCounts = await conn.QueryFirstOrDefaultAsync<dynamic>(
            """
            SELECT
                            (SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId::uuid) AS memory_count,
                            (SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId::uuid) AS entity_count,
                            (SELECT COUNT(*) FROM entity_relationships WHERE tenant_id = @TenantId::uuid) AS relationship_count
            """,
            new { TenantId = tenantId });

        if (actualCounts != null)
        {
            long totalRecords = 0;
            if (request.IncludeMemories) totalRecords += (long)(actualCounts.memory_count ?? 0);
            if (request.IncludeEntities) totalRecords += (long)(actualCounts.entity_count ?? 0);
            if (request.IncludeRelationships) totalRecords += (long)(actualCounts.relationship_count ?? 0);

            if (totalRecords > limits.MaxExportRecords)
            {
                violations.Add(new PayloadViolation
                {
                    Type = PayloadViolationType.ExportTooLarge,
                    Code = "EXPORT_TOO_LARGE",
                    Message = $"Export would include {totalRecords:N0} records, exceeding limit of {limits.MaxExportRecords:N0}",
                    ActualValue = totalRecords,
                    MaxAllowed = limits.MaxExportRecords
                });
            }
        }

        return violations.Count == 0
            ? PayloadValidationResult.Success()
            : PayloadValidationResult.Failure(violations.ToArray());
    }

    public async Task<PayloadValidationResult> ValidateBatchOperationAsync(
        string tenantId,
        int itemCount,
        long totalSizeBytes,
        CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(tenantId, ct);
        var violations = new List<PayloadViolation>();

        if (itemCount > limits.MaxBatchSize)
        {
            violations.Add(new PayloadViolation
            {
                Type = PayloadViolationType.BatchItemCountExceeded,
                Code = "BATCH_SIZE_EXCEEDED",
                Message = $"Batch size {itemCount} exceeds maximum of {limits.MaxBatchSize}",
                ActualValue = itemCount,
                MaxAllowed = limits.MaxBatchSize
            });
        }

        if (totalSizeBytes > limits.MaxBatchTotalBytes)
        {
            violations.Add(new PayloadViolation
            {
                Type = PayloadViolationType.BatchTooLarge,
                Code = "BATCH_BYTES_EXCEEDED",
                Message = $"Batch total size {totalSizeBytes:N0} bytes exceeds maximum of {limits.MaxBatchTotalBytes:N0} bytes",
                ActualValue = totalSizeBytes,
                MaxAllowed = limits.MaxBatchTotalBytes
            });
        }

        return violations.Count == 0
            ? PayloadValidationResult.Success()
            : PayloadValidationResult.Failure(violations.ToArray());
    }

    public async Task<PayloadLimits> GetLimitsAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var planName = await conn.QueryFirstOrDefaultAsync<string>(
            """
            SELECT ts.plan FROM tenant_settings ts
                          JOIN tenants t ON ts.tenant_id = t.id
                          WHERE t.id = @TenantId::uuid OR t.slug = @TenantId
            """,
            new { TenantId = tenantId });

        return planName?.ToLowerInvariant() switch
        {
            "free" => PayloadLimits.Free,
            "pro" => PayloadLimits.Pro,
            "enterprise" => PayloadLimits.Enterprise,
            "self" => PayloadLimits.SelfHosted,
            _ => PayloadLimits.Free
        };
    }

    private static IReadOnlyList<string> CheckProhibitedPatterns(string content)
    {
        var found = new List<string>();

        // Check for common injection patterns
        var patterns = new[]
        {
            ("SQL injection", new[] { "'; DROP", "1=1", "OR 1=1", "UNION SELECT", "/**/", "--" }),
            ("Script injection", new[] { "<script", "javascript:", "onerror=", "onload=", "eval(" }),
            ("Command injection", new[] { "$(", "`", "|", "&&", "||", ";", ">", "<" }),
            ("Path traversal", new[] { "../", "..\\", "%2e%2e", "%252e" })
        };

        foreach (var (category, keywords) in patterns)
        {
            foreach (var keyword in keywords)
            {
                if (content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(category);
                    break;
                }
            }
        }

        return found.Distinct().ToList();
    }
}
