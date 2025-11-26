using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for tamper-evident audit logging of admin tool invocations.
/// Uses SHA-256 hash chaining for integrity verification.
/// </summary>
public sealed class AdminAuditService : IAdminAuditService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<AdminAuditService> _logger;

    public AdminAuditService(string connectionString, ILogger<AdminAuditService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public AdminAuditService(NpgsqlDataSource dataSource, ILogger<AdminAuditService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<AdminActionEntry> LogActionAsync(
        Guid tenantId,
        string userId,
        string toolName,
        object? parameters,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        // Compute hash of parameters
        var paramsJson = parameters != null
            ? JsonSerializer.Serialize(parameters)
            : "";
        var paramsHash = ComputeSha256Hash(paramsJson);

        // Serialize metadata
        var metadataJson = metadata != null
            ? JsonSerializer.Serialize(metadata)
            : null;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Use the SQL function for append (handles hash chaining)
        var dto = await conn.QueryFirstAsync<AdminActionDto>(
            "SELECT * FROM append_admin_action(@TenantId, @UserId, @ToolName, @ParamsHash, @Metadata::jsonb)",
            new
            {
                TenantId = tenantId,
                UserId = userId,
                ToolName = toolName,
                ParamsHash = paramsHash,
                Metadata = metadataJson
            });

        _logger.LogDebug(
            "Logged admin action {ActionId} for tenant {TenantId}: {ToolName} by {UserId}",
            dto.id, tenantId, toolName, userId);

        return MapToEntry(dto);
    }

    public async Task CompleteActionAsync(
        Guid actionId,
        int executionMs,
        bool success,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(
            "SELECT complete_admin_action(@ActionId, @ExecutionMs, @Success, @ErrorMessage)",
            new
            {
                ActionId = actionId,
                ExecutionMs = executionMs,
                Success = success,
                ErrorMessage = errorMessage
            });

        if (success)
        {
            _logger.LogDebug("Completed admin action {ActionId} in {Ms}ms", actionId, executionMs);
        }
        else
        {
            _logger.LogWarning(
                "Admin action {ActionId} failed in {Ms}ms: {Error}",
                actionId, executionMs, errorMessage);
        }
    }

    public async Task<AdminChainVerificationResult> VerifyChainAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var dto = await conn.QueryFirstAsync<VerificationDto>(
            "SELECT * FROM verify_admin_action_chain(@TenantId)",
            new { TenantId = tenantId });

        _logger.LogInformation(
            "Verified admin action chain for tenant {TenantId}: valid={IsValid}, total={Total}, verified={Verified}",
            tenantId, dto.is_valid, dto.total_entries, dto.verified_entries);

        return new AdminChainVerificationResult
        {
            IsValid = dto.is_valid,
            TotalEntries = dto.total_entries,
            VerifiedEntries = dto.verified_entries,
            FirstBrokenId = dto.first_broken_id,
            FirstBrokenReason = dto.first_broken_reason
        };
    }

    public async Task<IReadOnlyList<AdminActionEntry>> GetRecentActionsAsync(
        Guid tenantId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var dtos = await conn.QueryAsync<AdminActionDto>(
            "SELECT * FROM get_recent_admin_actions(@TenantId, @Limit)",
            new { TenantId = tenantId, Limit = limit });

        return dtos.Select(MapToEntry).ToList();
    }

    public async Task<IReadOnlyList<AdminActionEntry>> GetActionsByUserAsync(
        Guid tenantId,
        string userId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var dtos = await conn.QueryAsync<AdminActionDto>(
            "SELECT * FROM get_admin_actions_by_user(@TenantId, @UserId, @Limit)",
            new { TenantId = tenantId, UserId = userId, Limit = limit });

        return dtos.Select(MapToEntry).ToList();
    }

    public async Task<IReadOnlyList<AdminActionEntry>> GetActionsByToolAsync(
        Guid tenantId,
        string toolName,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var dtos = await conn.QueryAsync<AdminActionDto>(
            "SELECT * FROM get_admin_actions_by_tool(@TenantId, @ToolName, @Limit)",
            new { TenantId = tenantId, ToolName = toolName, Limit = limit });

        return dtos.Select(MapToEntry).ToList();
    }

    public async Task<IReadOnlyList<AdminActionEntry>> GetActionsInRangeAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var dtos = await conn.QueryAsync<AdminActionDto>(
            "SELECT * FROM get_admin_actions_in_range(@TenantId, @From, @To)",
            new { TenantId = tenantId, From = from, To = to });

        return dtos.Select(MapToEntry).ToList();
    }

    /// <summary>
    /// Computes SHA-256 hash of a string.
    /// </summary>
    private static string ComputeSha256Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static AdminActionEntry MapToEntry(AdminActionDto dto)
    {
        Dictionary<string, object>? metadata = null;
        if (!string.IsNullOrEmpty(dto.metadata))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(dto.metadata);
            }
            catch
            {
                // Ignore deserialization errors
            }
        }

        return new AdminActionEntry
        {
            Id = dto.id,
            TenantId = dto.tenant_id,
            UserId = dto.user_id,
            ToolName = dto.tool_name,
            ParamsHash = dto.params_hash,
            Timestamp = dto.timestamp,
            PreviousHash = dto.prev_hash,
            Hash = dto.hash,
            ExecutionMs = dto.execution_ms,
            Success = dto.success,
            ErrorMessage = dto.error_message,
            Metadata = metadata
        };
    }

    private sealed class AdminActionDto
    {
        public Guid id { get; set; }
        public Guid tenant_id { get; set; }
        public string user_id { get; set; } = "";
        public string tool_name { get; set; } = "";
        public string params_hash { get; set; } = "";
        public DateTimeOffset timestamp { get; set; }
        public string prev_hash { get; set; } = "";
        public string hash { get; set; } = "";
        public int? execution_ms { get; set; }
        public bool success { get; set; } = true;
        public string? error_message { get; set; }
        public string? metadata { get; set; }
    }

    private sealed class VerificationDto
    {
        public bool is_valid { get; set; }
        public int total_entries { get; set; }
        public int verified_entries { get; set; }
        public Guid? first_broken_id { get; set; }
        public string? first_broken_reason { get; set; }
    }
}
