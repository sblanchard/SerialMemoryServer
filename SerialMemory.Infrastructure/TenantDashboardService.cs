using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Implementation of tenant dashboard service.
/// All queries are strictly scoped to tenant_id to ensure tenant isolation.
/// </summary>
public sealed class TenantDashboardService : ITenantDashboardService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<TenantDashboardService> _logger;

    public TenantDashboardService(string connectionString, ILogger<TenantDashboardService> logger)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
    }

    public TenantDashboardService(NpgsqlDataSource dataSource, ILogger<TenantDashboardService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<UserInfoResult> GetCurrentUserAsync(
        Guid tenantId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get tenant info
        var tenant = await conn.QueryFirstOrDefaultAsync<TenantDto>(
            "SELECT id, name, slug FROM tenants WHERE id = @TenantId AND status = 'active'",
            new { TenantId = tenantId });

        if (tenant == null)
            throw new InvalidOperationException($"Tenant {tenantId} not found or inactive");

        // Get user role
        var userRole = await conn.QueryFirstOrDefaultAsync<TenantUserDto>(
            """
            SELECT role, created_at FROM tenant_users
                          WHERE tenant_id = @TenantId AND user_id = @UserId
            """,
            new { TenantId = tenantId, UserId = userId });

        // Get workspaces
        var workspaces = await conn.QueryAsync<WorkspaceDto>(
            """
            SELECT id, name, slug, is_default
                          FROM tenant_workspaces
                          WHERE tenant_id = @TenantId
                          ORDER BY is_default DESC, name
            """,
            new { TenantId = tenantId });

        // Determine scopes based on role
        var scopes = GetScopesForRole(userRole?.role ?? "member");

        return new UserInfoResult
        {
            UserId = userId,
            TenantId = tenantId,
            TenantName = tenant.name,
            TenantSlug = tenant.slug,
            Role = userRole?.role ?? "member",
            Scopes = scopes,
            Workspaces = workspaces.Select(w => new WorkspaceInfo
            {
                Id = w.id,
                Name = w.name,
                Slug = w.slug,
                IsDefault = w.is_default
            }).ToList(),
            JoinedAt = userRole?.created_at
        };
    }

    public async Task<TenantUsageResult> GetTenantUsageAsync(
        Guid tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get current billing cycle
        var cycle = await conn.QueryFirstOrDefaultAsync<BillingCycleDto>(
            """
            SELECT id, cycle_start, cycle_end, credits_allocated, credits_used
                          FROM billing_cycles
                          WHERE tenant_id = @TenantId::text AND workspace_id = @WorkspaceId AND is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        if (cycle == null)
        {
            // Return empty usage if no billing cycle exists
            return new TenantUsageResult
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                CreditsUsed = 0,
                CreditsAllocated = 0,
                CreditsRemaining = 0,
                CycleStart = DateTimeOffset.UtcNow,
                CycleEnd = DateTimeOffset.UtcNow.AddDays(30),
                DaysRemaining = 30,
                BreakdownByType = Array.Empty<UsageBreakdown>(),
                Last7Days = Array.Empty<DailyUsage>()
            };
        }

        // Get breakdown by event type for current cycle
        var breakdown = await conn.QueryAsync<UsageBreakdownDto>(
            """
            SELECT event_type, COUNT(*) as count, SUM(credits_consumed) as credits
                          FROM usage_events
                          WHERE tenant_id = @TenantId::text AND workspace_id = @WorkspaceId
                            AND event_timestamp >= @CycleStart AND event_timestamp < @CycleEnd
                          GROUP BY event_type
                          ORDER BY credits DESC
            """,
            new
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                CycleStart = cycle.cycle_start,
                CycleEnd = cycle.cycle_end
            });

        // Get last 7 days usage
        var sevenDaysAgo = DateTimeOffset.UtcNow.Date.AddDays(-6);
        var dailyUsage = await conn.QueryAsync<DailyUsageDto>(
            """
            SELECT DATE(event_timestamp) as date, COUNT(*) as event_count, SUM(credits_consumed) as credits_used
                          FROM usage_events
                          WHERE tenant_id = @TenantId::text AND workspace_id = @WorkspaceId
                            AND event_timestamp >= @SevenDaysAgo
                          GROUP BY DATE(event_timestamp)
                          ORDER BY date
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId, SevenDaysAgo = sevenDaysAgo });

        var daysRemaining = (int)Math.Ceiling((cycle.cycle_end - DateTimeOffset.UtcNow).TotalDays);
        if (daysRemaining < 0) daysRemaining = 0;

        return new TenantUsageResult
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            CreditsUsed = cycle.credits_used,
            CreditsAllocated = cycle.credits_allocated,
            CreditsRemaining = cycle.credits_allocated - cycle.credits_used,
            CycleStart = cycle.cycle_start,
            CycleEnd = cycle.cycle_end,
            DaysRemaining = daysRemaining,
            BreakdownByType = breakdown.Select(b => new UsageBreakdown
            {
                EventType = b.event_type,
                Count = b.count,
                Credits = b.credits
            }).ToList(),
            Last7Days = dailyUsage.Select(d => new DailyUsage
            {
                Date = DateOnly.FromDateTime(d.date),
                EventCount = d.event_count,
                CreditsUsed = d.credits_used
            }).ToList()
        };
    }

    public async Task<TenantPlanResult> GetTenantPlanAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Get tenant settings
        var settings = await conn.QueryFirstOrDefaultAsync<TenantSettingsDto>(
            """
            SELECT plan, max_workspaces, max_users, features
                          FROM tenant_settings
                          WHERE tenant_id = @TenantId
            """,
            new { TenantId = tenantId });

        if (settings == null)
        {
            // Return default plan info
            settings = new TenantSettingsDto
            {
                plan = "free",
                max_workspaces = 1,
                max_users = 5,
                features = "{}"
            };
        }

        // Get plan details
        var plan = await conn.QueryFirstOrDefaultAsync<TenantPlanDto>(
            """
            SELECT plan_name, display_name, credits_per_cycle, cycle_days, max_memories, max_entities
                          FROM tenant_plans
                          WHERE plan_name = @PlanName AND is_active = TRUE
            """,
            new { PlanName = settings.plan });

        // Get current counts
        var memoriesCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM memories WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        var entitiesCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entities WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        var workspacesCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM tenant_workspaces WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        var usersCount = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM tenant_users WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        // Parse features from settings
        var features = ParseFeatures(settings.features);

        return new TenantPlanResult
        {
            TenantId = tenantId,
            PlanName = plan?.plan_name ?? settings.plan,
            DisplayName = plan?.display_name ?? GetDisplayName(settings.plan),
            CreditsPerCycle = plan?.credits_per_cycle ?? 1000,
            CycleDays = plan?.cycle_days ?? 30,
            MaxMemories = plan?.max_memories,
            MaxEntities = plan?.max_entities,
            MaxWorkspaces = settings.max_workspaces,
            MaxUsers = settings.max_users,
            Features = features,
            LimitsStatus = new PlanLimitsStatus
            {
                CurrentMemories = memoriesCount,
                CurrentEntities = entitiesCount,
                CurrentWorkspaces = workspacesCount,
                CurrentUsers = usersCount
            }
        };
    }

    public async Task<ExportRequestResult> RequestExportAsync(
        Guid tenantId,
        string userId,
        ExportRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var exportId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        // Check if there's already a pending export
        var pendingExport = await conn.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT id FROM export_requests
                          WHERE tenant_id = @TenantId AND status IN ('pending', 'processing')
                          LIMIT 1
            """,
            new { TenantId = tenantId });

        if (pendingExport.HasValue)
        {
            throw new InvalidOperationException("An export is already in progress. Please wait for it to complete.");
        }

        // Create export request
        await conn.ExecuteAsync(
            """
            INSERT INTO export_requests (id, tenant_id, requested_by, status, options, requested_at)
                          VALUES (@Id, @TenantId, @RequestedBy, 'pending', @Options::jsonb, @RequestedAt)
            """,
            new
            {
                Id = exportId,
                TenantId = tenantId,
                RequestedBy = userId,
                Options = System.Text.Json.JsonSerializer.Serialize(options),
                RequestedAt = now
            });

        _logger.LogInformation(
            "Export request {ExportId} created for tenant {TenantId} by {UserId}",
            exportId, tenantId, userId);

        return new ExportRequestResult
        {
            ExportId = exportId,
            Status = "pending",
            RequestedAt = now,
            EstimatedCompletion = now.AddMinutes(30)
        };
    }

    public async Task<ExportStatusResult?> GetExportStatusAsync(
        Guid tenantId,
        Guid exportId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var export = await conn.QueryFirstOrDefaultAsync<ExportRequestDto>(
            """
            SELECT id, status, requested_at, completed_at, download_url, download_expires_at,
                                 file_size_bytes, error_message
                          FROM export_requests
                          WHERE id = @ExportId AND tenant_id = @TenantId
            """,
            new { ExportId = exportId, TenantId = tenantId });

        if (export == null)
            return null;

        return new ExportStatusResult
        {
            ExportId = export.id,
            Status = export.status,
            RequestedAt = export.requested_at,
            CompletedAt = export.completed_at,
            DownloadUrl = export.download_url,
            DownloadExpiresAt = export.download_expires_at,
            FileSizeBytes = export.file_size_bytes,
            ErrorMessage = export.error_message
        };
    }

    public async Task<DeletionRequestResult> RequestDeletionAsync(
        Guid tenantId,
        string userId,
        string confirmationPhrase,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Verify user is owner
        var role = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT role FROM tenant_users WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId, UserId = userId });

        if (role != "owner")
        {
            throw new UnauthorizedAccessException("Only tenant owners can request deletion");
        }

        // Get tenant slug for confirmation
        var tenantSlug = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT slug FROM tenants WHERE id = @TenantId",
            new { TenantId = tenantId });

        // Verify confirmation phrase
        var expectedPhrase = $"delete {tenantSlug}";
        if (!string.Equals(confirmationPhrase, expectedPhrase, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Confirmation phrase must be '{expectedPhrase}'");
        }

        // Check if there's already a pending deletion
        var pendingDeletion = await conn.QueryFirstOrDefaultAsync<DeletionRequestDto>(
            """
            SELECT id, status, scheduled_for FROM deletion_requests
                          WHERE tenant_id = @TenantId AND status = 'pending'
                          LIMIT 1
            """,
            new { TenantId = tenantId });

        if (pendingDeletion != null)
        {
            return new DeletionRequestResult
            {
                DeletionId = pendingDeletion.id,
                Status = pendingDeletion.status,
                RequestedAt = DateTimeOffset.UtcNow,
                ScheduledFor = pendingDeletion.scheduled_for,
                Message = "A deletion request is already pending. You can cancel it within the grace period."
            };
        }

        var deletionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var scheduledFor = now.AddDays(30); // 30-day grace period

        // Create deletion request
        await conn.ExecuteAsync(
            """
            INSERT INTO deletion_requests (id, tenant_id, requested_by, status, requested_at, scheduled_for)
                          VALUES (@Id, @TenantId, @RequestedBy, 'pending', @RequestedAt, @ScheduledFor)
            """,
            new
            {
                Id = deletionId,
                TenantId = tenantId,
                RequestedBy = userId,
                RequestedAt = now,
                ScheduledFor = scheduledFor
            });

        // Update tenant status
        await conn.ExecuteAsync(
            "UPDATE tenants SET status = 'pending_deletion' WHERE id = @TenantId",
            new { TenantId = tenantId });

        _logger.LogWarning(
            "Deletion request {DeletionId} created for tenant {TenantId} by {UserId}, scheduled for {ScheduledFor}",
            deletionId, tenantId, userId, scheduledFor);

        return new DeletionRequestResult
        {
            DeletionId = deletionId,
            Status = "pending",
            RequestedAt = now,
            ScheduledFor = scheduledFor,
            Message = $"Deletion scheduled for {scheduledFor:yyyy-MM-dd}. You have 30 days to cancel."
        };
    }

    public async Task<DeletionStatusResult?> GetDeletionStatusAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var deletion = await conn.QueryFirstOrDefaultAsync<DeletionRequestDto>(
            """
            SELECT id, status, requested_at, scheduled_for, cancelled_at, completed_at, cancellation_reason
                          FROM deletion_requests
                          WHERE tenant_id = @TenantId
                          ORDER BY requested_at DESC
                          LIMIT 1
            """,
            new { TenantId = tenantId });

        if (deletion == null)
            return null;

        return new DeletionStatusResult
        {
            DeletionId = deletion.id,
            Status = deletion.status,
            RequestedAt = deletion.requested_at,
            ScheduledFor = deletion.scheduled_for,
            CancelledAt = deletion.cancelled_at,
            CompletedAt = deletion.completed_at,
            CancellationReason = deletion.cancellation_reason
        };
    }

    public async Task<bool> CancelDeletionAsync(
        Guid tenantId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Verify user is owner or admin
        var role = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT role FROM tenant_users WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId, UserId = userId });

        if (role != "owner" && role != "admin")
        {
            throw new UnauthorizedAccessException("Only tenant owners or admins can cancel deletion");
        }

        // Cancel the pending deletion
        var affected = await conn.ExecuteAsync(
            """
            UPDATE deletion_requests
                          SET status = 'cancelled', cancelled_at = NOW(), cancellation_reason = 'User cancelled'
                          WHERE tenant_id = @TenantId AND status = 'pending'
            """,
            new { TenantId = tenantId });

        if (affected > 0)
        {
            // Restore tenant status
            await conn.ExecuteAsync(
                "UPDATE tenants SET status = 'active' WHERE id = @TenantId",
                new { TenantId = tenantId });

            _logger.LogInformation(
                "Deletion cancelled for tenant {TenantId} by {UserId}",
                tenantId, userId);

            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> GetScopesForRole(string role) => role switch
    {
        "owner" => new[] { "serialmemory.core", "serialmemory.admin", "serialmemory.export", "serialmemory.delete" },
        "admin" => new[] { "serialmemory.core", "serialmemory.admin", "serialmemory.export" },
        "member" => new[] { "serialmemory.core", "serialmemory.export" },
        "readonly" => new[] { "serialmemory.read" },
        _ => new[] { "serialmemory.read" }
    };

    private static string GetDisplayName(string plan) => plan switch
    {
        "free" => "Free Tier",
        "pro" => "Professional",
        "enterprise" => "Enterprise",
        "self" => "Self-Hosted",
        _ => plan
    };

    private static IReadOnlyList<string> ParseFeatures(string? featuresJson)
    {
        if (string.IsNullOrEmpty(featuresJson) || featuresJson == "{}")
            return Array.Empty<string>();

        try
        {
            var features = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(featuresJson);
            return features?
                .Where(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // DTO classes for Dapper mapping
    private sealed class TenantDto
    {
        public Guid id { get; set; }
        public string name { get; set; } = "";
        public string slug { get; set; } = "";
    }

    private sealed class TenantUserDto
    {
        public string role { get; set; } = "";
        public DateTimeOffset created_at { get; set; }
    }

    private sealed class WorkspaceDto
    {
        public Guid id { get; set; }
        public string name { get; set; } = "";
        public string slug { get; set; } = "";
        public bool is_default { get; set; }
    }

    private sealed class BillingCycleDto
    {
        public Guid id { get; set; }
        public DateTimeOffset cycle_start { get; set; }
        public DateTimeOffset cycle_end { get; set; }
        public decimal credits_allocated { get; set; }
        public decimal credits_used { get; set; }
    }

    private sealed class UsageBreakdownDto
    {
        public string event_type { get; set; } = "";
        public int count { get; set; }
        public decimal credits { get; set; }
    }

    private sealed class DailyUsageDto
    {
        public DateTime date { get; set; }
        public int event_count { get; set; }
        public decimal credits_used { get; set; }
    }

    private sealed class TenantSettingsDto
    {
        public string plan { get; set; } = "";
        public int max_workspaces { get; set; }
        public int max_users { get; set; }
        public string? features { get; set; }
    }

    private sealed class TenantPlanDto
    {
        public string plan_name { get; set; } = "";
        public string display_name { get; set; } = "";
        public decimal credits_per_cycle { get; set; }
        public int cycle_days { get; set; }
        public int? max_memories { get; set; }
        public int? max_entities { get; set; }
    }

    private sealed class ExportRequestDto
    {
        public Guid id { get; set; }
        public string status { get; set; } = "";
        public DateTimeOffset requested_at { get; set; }
        public DateTimeOffset? completed_at { get; set; }
        public string? download_url { get; set; }
        public DateTimeOffset? download_expires_at { get; set; }
        public long? file_size_bytes { get; set; }
        public string? error_message { get; set; }
    }

    private sealed class DeletionRequestDto
    {
        public Guid id { get; set; }
        public string status { get; set; } = "";
        public DateTimeOffset requested_at { get; set; }
        public DateTimeOffset scheduled_for { get; set; }
        public DateTimeOffset? cancelled_at { get; set; }
        public DateTimeOffset? completed_at { get; set; }
        public string? cancellation_reason { get; set; }
    }
}
