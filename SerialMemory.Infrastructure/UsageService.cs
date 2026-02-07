using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Usage metering service with non-blocking event logging.
/// Uses a background queue to prevent usage tracking from affecting core functionality.
/// All queries are strictly scoped to tenant_id to ensure tenant isolation.
/// </summary>
public sealed class UsageService : IUsageService, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<UsageService> _logger;
    private readonly string _tenantId;
    private readonly string _workspaceId;
    private readonly BlockingCollection<UsageEvent> _eventQueue;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts;
    private Guid? _currentBillingCycleId;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    /// <summary>
    /// The tenant ID for this service instance.
    /// </summary>
    public string TenantId => _tenantId;

    /// <summary>
    /// The workspace ID for this service instance.
    /// </summary>
    public string WorkspaceId => _workspaceId;

    public UsageService(
        string connectionString,
        ILogger<UsageService> logger,
        string tenantId = "self",
        string workspaceId = "default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
        _logger = logger;
        _tenantId = tenantId;
        _workspaceId = workspaceId;
        _eventQueue = new BlockingCollection<UsageEvent>(boundedCapacity: 10000);
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(ProcessQueueAsync);
    }

    public UsageService(
        NpgsqlDataSource dataSource,
        ILogger<UsageService> logger,
        string tenantId = "self",
        string workspaceId = "default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        _dataSource = dataSource;
        _logger = logger;
        _tenantId = tenantId;
        _workspaceId = workspaceId;
        _eventQueue = new BlockingCollection<UsageEvent>(boundedCapacity: 10000);
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(ProcessQueueAsync);
    }

    public void TrackUsage(
        UsageEventType eventType,
        Guid? memoryId = null,
        string? userId = null,
        Guid? sessionId = null,
        int? latencyMs = null,
        bool success = true,
        string? errorMessage = null,
        Dictionary<string, object>? metadata = null)
    {
        var usageEvent = new UsageEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            WorkspaceId = _workspaceId,
            EventType = eventType,
            CreditsConsumed = UsageCreditCosts.GetCost(eventType),
            EventTimestamp = DateTimeOffset.UtcNow,
            MemoryId = memoryId,
            UserId = userId,
            SessionId = sessionId,
            LatencyMs = latencyMs,
            Success = success,
            ErrorMessage = errorMessage,
            Metadata = metadata
        };

        if (!_eventQueue.TryAdd(usageEvent))
        {
            _logger.LogWarning("Usage event queue is full, dropping event: {EventType}", eventType);
        }
    }

    public Task<BillingCycle?> GetCurrentBillingCycleAsync(
        CancellationToken cancellationToken = default)
        => GetCurrentBillingCycleAsync(_tenantId, _workspaceId, cancellationToken);

    public async Task<BillingCycle?> GetCurrentBillingCycleAsync(
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        // Ensure tenant isolation - only allow access to own tenant or explicit tenant parameter
        if (tenantId != _tenantId)
        {
            _logger.LogWarning(
                "Attempt to access billing cycle for tenant {RequestedTenant} from service scoped to {ActualTenant}",
                tenantId, _tenantId);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Set tenant context for RLS
        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false); SELECT set_config('app.current_tenant_id', @TenantId, false);",
            new { TenantId = tenantId });

        var cycle = await conn.QueryFirstOrDefaultAsync<BillingCycle>(
            """
            SELECT id, tenant_id AS TenantId, workspace_id AS WorkspaceId, plan_id AS PlanId,
                                 cycle_start AS CycleStart, cycle_end AS CycleEnd,
                                 credits_allocated AS CreditsAllocated, credits_used AS CreditsUsed,
                                 is_current AS IsCurrent, closed_at AS ClosedAt, created_at AS CreatedAt
                          FROM billing_cycles
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId AND is_current = TRUE
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId });

        return cycle;
    }

    public Task<IReadOnlyList<UsageDailyRollup>> GetUsageStatsAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
        => GetUsageStatsAsync(from, to, _tenantId, _workspaceId, cancellationToken);

    public async Task<IReadOnlyList<UsageDailyRollup>> GetUsageStatsAsync(
        DateOnly from,
        DateOnly to,
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Set tenant context for RLS
        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false); SELECT set_config('app.current_tenant_id', @TenantId, false);",
            new { TenantId = tenantId });

        var rollups = await conn.QueryAsync<UsageDailyRollupDto>(
            """
            SELECT id, tenant_id, workspace_id, rollup_date, event_type,
                                 event_count, total_credits, success_count, failure_count,
                                 avg_latency_ms, p95_latency_ms, p99_latency_ms,
                                 created_at, updated_at
                          FROM usage_daily_rollups
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                            AND rollup_date >= @From AND rollup_date <= @To
                          ORDER BY rollup_date DESC, event_type
            """,
            new
            {
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.ToDateTime(TimeOnly.MaxValue)
            });

        return rollups.Select(MapToRollup).ToList();
    }

    public Task<IReadOnlyList<UsageEvent>> GetRecentEventsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
        => GetRecentEventsAsync(limit, _tenantId, _workspaceId, cancellationToken);

    public async Task<IReadOnlyList<UsageEvent>> GetRecentEventsAsync(
        int limit,
        string tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Set tenant context for RLS
        await conn.ExecuteAsync(
            "SELECT set_config('app.tenant_id', @TenantId, false); SELECT set_config('app.current_tenant_id', @TenantId, false);",
            new { TenantId = tenantId });

        var events = await conn.QueryAsync<UsageEventDto>(
            """
            SELECT id, tenant_id, workspace_id, billing_cycle_id, event_type,
                                 credits_consumed, event_timestamp, memory_id, user_id, session_id,
                                 latency_ms, success, error_message, metadata
                          FROM usage_events
                          WHERE tenant_id = @TenantId AND workspace_id = @WorkspaceId
                          ORDER BY event_timestamp DESC
                          LIMIT @Limit
            """,
            new { TenantId = tenantId, WorkspaceId = workspaceId, Limit = limit });

        return events.Select(MapToEvent).ToList();
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<UsageEvent>(100);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                batch.Clear();

                // Wait for first event
                if (_eventQueue.TryTake(out var firstEvent, 1000, _cts.Token))
                {
                    batch.Add(firstEvent);

                    // Collect more events if available (batch for efficiency)
                    while (batch.Count < 100 && _eventQueue.TryTake(out var nextEvent, 10))
                    {
                        batch.Add(nextEvent);
                    }

                    await PersistBatchAsync(batch);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing usage event batch");
                await Task.Delay(1000, _cts.Token);
            }
        }

        // Drain remaining events on shutdown
        while (_eventQueue.TryTake(out var evt))
        {
            batch.Add(evt);
            if (batch.Count >= 100)
            {
                await PersistBatchAsync(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await PersistBatchAsync(batch);
        }
    }

    private async Task PersistBatchAsync(List<UsageEvent> events)
    {
        if (events.Count == 0) return;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();

            // Set tenant context for RLS
            await conn.ExecuteAsync(
                """
                SELECT set_config('app.tenant_id', @TenantId, false);
                SELECT set_config('app.current_tenant_id', @TenantId, false);
                """,
                new { TenantId = _tenantId });

            // Ensure we have a billing cycle
            var cycleId = await EnsureBillingCycleAsync(conn);

            foreach (var evt in events)
            {
                evt.BillingCycleId = cycleId;
            }

            // Use INSERT with ON CONFLICT to handle duplicates gracefully
            // This is slightly less efficient than COPY but prevents duplicate key errors
            const string insertSql = """
                INSERT INTO usage_events (id, tenant_id, workspace_id, billing_cycle_id, tool_name,
                                          credits_used, created_at, memory_id, user_id, session_id,
                                          latency_ms, success, error_message, metadata)
                VALUES (@Id, @TenantId, @WorkspaceId, @BillingCycleId, @ToolName,
                        @CreditsUsed, @CreatedAt, @MemoryId, @UserId, @SessionId,
                        @LatencyMs, @Success, @ErrorMessage, @Metadata::jsonb)
                ON CONFLICT (id) DO NOTHING
                """;

            foreach (var evt in events)
            {
                await conn.ExecuteAsync(insertSql, new
                {
                    evt.Id,
                    evt.TenantId,
                    evt.WorkspaceId,
                    evt.BillingCycleId,
                    ToolName = UsageCreditCosts.ToSnakeCase(evt.EventType),
                    CreditsUsed = evt.CreditsConsumed,
                    CreatedAt = evt.EventTimestamp,
                    evt.MemoryId,
                    evt.UserId,
                    evt.SessionId,
                    evt.LatencyMs,
                    evt.Success,
                    evt.ErrorMessage,
                    Metadata = evt.Metadata != null ? JsonSerializer.Serialize(evt.Metadata) : null
                });
            }

            _logger.LogDebug("Persisted {Count} usage events", events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Count} usage events", events.Count);
        }
    }

    private async Task<Guid?> EnsureBillingCycleAsync(NpgsqlConnection conn)
    {
        if (_currentBillingCycleId.HasValue)
        {
            return _currentBillingCycleId;
        }

        await _cycleLock.WaitAsync();
        try
        {
            if (_currentBillingCycleId.HasValue)
            {
                return _currentBillingCycleId;
            }

            _currentBillingCycleId = await conn.QueryFirstOrDefaultAsync<Guid?>(
                "SELECT get_or_create_billing_cycle(@TenantId, @WorkspaceId)",
                new { TenantId = _tenantId, WorkspaceId = _workspaceId });

            return _currentBillingCycleId;
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    private static UsageDailyRollup MapToRollup(UsageDailyRollupDto dto) => new()
    {
        Id = dto.id,
        TenantId = dto.tenant_id,
        WorkspaceId = dto.workspace_id,
        RollupDate = DateOnly.FromDateTime(dto.rollup_date),
        EventType = ParseEventType(dto.event_type),
        EventCount = dto.event_count,
        TotalCredits = dto.total_credits,
        SuccessCount = dto.success_count,
        FailureCount = dto.failure_count,
        AvgLatencyMs = dto.avg_latency_ms,
        P95LatencyMs = dto.p95_latency_ms,
        P99LatencyMs = dto.p99_latency_ms,
        CreatedAt = dto.created_at,
        UpdatedAt = dto.updated_at
    };

    private static UsageEvent MapToEvent(UsageEventDto dto) => new()
    {
        Id = dto.id,
        TenantId = dto.tenant_id,
        WorkspaceId = dto.workspace_id,
        BillingCycleId = dto.billing_cycle_id,
        EventType = ParseEventType(dto.event_type),
        CreditsConsumed = dto.credits_consumed,
        EventTimestamp = dto.event_timestamp,
        MemoryId = dto.memory_id,
        UserId = dto.user_id,
        SessionId = dto.session_id,
        LatencyMs = dto.latency_ms,
        Success = dto.success,
        ErrorMessage = dto.error_message
    };

    private static UsageEventType ParseEventType(string eventType) =>
        UsageCreditCosts.FromSnakeCase(eventType) ?? UsageEventType.MemoryIngest;

    public void Dispose()
    {
        _cts.Cancel();
        _eventQueue.CompleteAdding();

        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Ignore cancellation exceptions
        }

        _cts.Dispose();
        _eventQueue.Dispose();
        _cycleLock.Dispose();
        _dataSource.Dispose();
    }

    // DTO classes for Dapper mapping
    private sealed class UsageEventDto
    {
        public Guid id { get; set; }
        public string tenant_id { get; set; } = "";
        public string workspace_id { get; set; } = "";
        public Guid? billing_cycle_id { get; set; }
        public string event_type { get; set; } = "";
        public decimal credits_consumed { get; set; }
        public DateTimeOffset event_timestamp { get; set; }
        public Guid? memory_id { get; set; }
        public string? user_id { get; set; }
        public Guid? session_id { get; set; }
        public int? latency_ms { get; set; }
        public bool success { get; set; }
        public string? error_message { get; set; }
        public string? metadata { get; set; }
    }

    private sealed class UsageDailyRollupDto
    {
        public Guid id { get; set; }
        public string tenant_id { get; set; } = "";
        public string workspace_id { get; set; } = "";
        public DateTime rollup_date { get; set; }
        public string event_type { get; set; } = "";
        public int event_count { get; set; }
        public decimal total_credits { get; set; }
        public int success_count { get; set; }
        public int failure_count { get; set; }
        public decimal? avg_latency_ms { get; set; }
        public int? p95_latency_ms { get; set; }
        public int? p99_latency_ms { get; set; }
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }
    }
}

/// <summary>
/// Extension for timing operations and tracking usage.
/// </summary>
public static class UsageTrackingExtensions
{
    /// <summary>
    /// Wraps an async operation with usage tracking.
    /// Non-blocking - will not fail if usage tracking fails.
    /// </summary>
    public static async Task<T> TrackAsync<T>(
        this IUsageService usageService,
        UsageEventType eventType,
        Func<Task<T>> operation,
        Guid? memoryId = null,
        string? userId = null,
        Guid? sessionId = null)
    {
        var sw = Stopwatch.StartNew();
        var success = true;
        string? errorMessage = null;

        try
        {
            return await operation();
        }
        catch (Exception ex)
        {
            success = false;
            errorMessage = ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            usageService.TrackUsage(
                eventType,
                memoryId: memoryId,
                userId: userId,
                sessionId: sessionId,
                latencyMs: (int)sw.ElapsedMilliseconds,
                success: success,
                errorMessage: errorMessage);
        }
    }

    /// <summary>
    /// Wraps an async operation with usage tracking (no return value).
    /// </summary>
    public static async Task TrackAsync(
        this IUsageService usageService,
        UsageEventType eventType,
        Func<Task> operation,
        Guid? memoryId = null,
        string? userId = null,
        Guid? sessionId = null)
    {
        await usageService.TrackAsync(eventType, async () =>
        {
            await operation();
            return true;
        }, memoryId, userId, sessionId);
    }
}

/// <summary>
/// Factory for creating tenant-scoped usage services.
/// </summary>
public sealed class UsageServiceFactory(string connectionString, ILoggerFactory loggerFactory) : IUsageServiceFactory
{
    public IUsageService CreateForTenant(string tenantId, string workspaceId)
    {
        return new UsageService(
            connectionString,
            loggerFactory.CreateLogger<UsageService>(),
            tenantId,
            workspaceId);
    }

    public IUsageService CreateFromContext(ITenantContext context)
    {
        return CreateForTenant(context.TenantId, context.WorkspaceId);
    }
}
