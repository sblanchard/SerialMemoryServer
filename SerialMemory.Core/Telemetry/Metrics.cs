using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SerialMemory.Core.Telemetry;

/// <summary>
/// Production-grade telemetry metrics for SerialMemory.
/// Exposes Prometheus-compatible metrics for observability, cost tracking, and capacity planning.
/// </summary>
public static class Metrics
{
    public const string MeterName = "SerialMemory";
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    // =============================================================================
    // TECHNICAL METRICS - Infrastructure Health
    // =============================================================================

    /// <summary>Total HTTP requests by method, path, and status code</summary>
    public static readonly Counter<long> HttpRequestsTotal = Meter.CreateCounter<long>(
        "http_requests_total",
        unit: "{request}",
        description: "Total HTTP requests by method, path pattern, and status code");

    /// <summary>HTTP request duration histogram</summary>
    public static readonly Histogram<double> HttpRequestDurationMs = Meter.CreateHistogram<double>(
        "http_request_duration_ms",
        unit: "ms",
        description: "HTTP request duration in milliseconds");

    /// <summary>Total database queries by operation type</summary>
    public static readonly Counter<long> DbQueryCount = Meter.CreateCounter<long>(
        "db_query_count",
        unit: "{query}",
        description: "Total database queries by operation (select, insert, update, delete)");

    /// <summary>Database query duration histogram</summary>
    public static readonly Histogram<double> DbQueryDurationMs = Meter.CreateHistogram<double>(
        "db_query_duration_ms",
        unit: "ms",
        description: "Database query duration in milliseconds");

    /// <summary>Active database connections</summary>
    public static readonly UpDownCounter<int> ActiveDbConnections = Meter.CreateUpDownCounter<int>(
        "active_db_connections",
        unit: "{connection}",
        description: "Current number of active database connections");

    /// <summary>Active HTTP connections</summary>
    public static readonly UpDownCounter<int> ActiveHttpConnections = Meter.CreateUpDownCounter<int>(
        "active_http_connections",
        unit: "{connection}",
        description: "Current number of active HTTP connections");

    /// <summary>Embedding generation duration</summary>
    public static readonly Histogram<double> EmbeddingDurationMs = Meter.CreateHistogram<double>(
        "embedding_duration_ms",
        unit: "ms",
        description: "Embedding generation duration in milliseconds");

    /// <summary>Entity extraction duration</summary>
    public static readonly Histogram<double> EntityExtractionDurationMs = Meter.CreateHistogram<double>(
        "entity_extraction_duration_ms",
        unit: "ms",
        description: "Entity extraction duration in milliseconds");

    // =============================================================================
    // BUSINESS METRICS - Core Operations
    // =============================================================================

    /// <summary>Total memories created</summary>
    public static readonly Counter<long> MemoriesCreatedTotal = Meter.CreateCounter<long>(
        "memories_created_total",
        unit: "{memory}",
        description: "Total memories ingested into the knowledge graph");

    /// <summary>Total memories read/searched</summary>
    public static readonly Counter<long> MemoriesReadTotal = Meter.CreateCounter<long>(
        "memories_read_total",
        unit: "{memory}",
        description: "Total memories retrieved via search or direct read");

    /// <summary>Total entities extracted</summary>
    public static readonly Counter<long> EntitiesExtractedTotal = Meter.CreateCounter<long>(
        "entities_extracted_total",
        unit: "{entity}",
        description: "Total entities extracted from memories");

    /// <summary>Total relationships created</summary>
    public static readonly Counter<long> RelationshipsCreatedTotal = Meter.CreateCounter<long>(
        "relationships_created_total",
        unit: "{relationship}",
        description: "Total entity relationships created in knowledge graph");

    /// <summary>Search operations by mode (semantic, text, hybrid)</summary>
    public static readonly Counter<long> SearchOperationsTotal = Meter.CreateCounter<long>(
        "search_operations_total",
        unit: "{search}",
        description: "Total search operations by mode");

    /// <summary>Search latency histogram</summary>
    public static readonly Histogram<double> SearchDurationMs = Meter.CreateHistogram<double>(
        "search_duration_ms",
        unit: "ms",
        description: "Memory search duration in milliseconds");

    // =============================================================================
    // BUSINESS METRICS - SaaS / Multi-tenant
    // =============================================================================

    /// <summary>Credits consumed per tenant</summary>
    public static readonly Counter<long> CreditsConsumedTotal = Meter.CreateCounter<long>(
        "credits_consumed_total",
        unit: "{credit}",
        description: "Total credits consumed by operation type and tenant");

    /// <summary>Rate limit violations</summary>
    public static readonly Counter<long> RateLimitViolationsTotal = Meter.CreateCounter<long>(
        "rate_limit_violations_total",
        unit: "{violation}",
        description: "Total rate limit violations by tenant and endpoint");

    /// <summary>Failed authentication attempts</summary>
    public static readonly Counter<long> FailedAuthTotal = Meter.CreateCounter<long>(
        "failed_auth_total",
        unit: "{attempt}",
        description: "Total failed authentication attempts by reason");

    /// <summary>Successful authentication</summary>
    public static readonly Counter<long> SuccessfulAuthTotal = Meter.CreateCounter<long>(
        "successful_auth_total",
        unit: "{attempt}",
        description: "Total successful authentications by method (api_key, jwt)");

    /// <summary>New tenant signups</summary>
    public static readonly Counter<long> TenantSignupTotal = Meter.CreateCounter<long>(
        "tenant_signup_total",
        unit: "{signup}",
        description: "Total new tenant signups");

    /// <summary>Stripe payment failures</summary>
    public static readonly Counter<long> StripeFailuresTotal = Meter.CreateCounter<long>(
        "stripe_failures_total",
        unit: "{failure}",
        description: "Total Stripe payment/webhook failures by type");

    /// <summary>Stripe successful payments</summary>
    public static readonly Counter<long> StripePaymentsTotal = Meter.CreateCounter<long>(
        "stripe_payments_total",
        unit: "{payment}",
        description: "Total successful Stripe payments");

    /// <summary>Active tenants gauge</summary>
    public static readonly ObservableGauge<int> ActiveTenants = Meter.CreateObservableGauge(
        "active_tenants",
        () => _activeTenantCount,
        unit: "{tenant}",
        description: "Current number of active tenants");

    /// <summary>API keys in use</summary>
    public static readonly Counter<long> ApiKeyUsageTotal = Meter.CreateCounter<long>(
        "api_key_usage_total",
        unit: "{usage}",
        description: "Total API key usages by key prefix");

    // =============================================================================
    // INFRASTRUCTURE METRICS - Message Queue
    // =============================================================================

    /// <summary>Messages published to RabbitMQ</summary>
    public static readonly Counter<long> RabbitPublished = Meter.CreateCounter<long>(
        "rabbit_published_total",
        unit: "{message}",
        description: "Total messages published to RabbitMQ by exchange");

    /// <summary>Messages consumed from RabbitMQ</summary>
    public static readonly Counter<long> RabbitConsumed = Meter.CreateCounter<long>(
        "rabbit_consumed_total",
        unit: "{message}",
        description: "Total messages consumed from RabbitMQ by queue");

    /// <summary>Redis operation latency</summary>
    public static readonly Histogram<double> RedisLatencyMs = Meter.CreateHistogram<double>(
        "redis_latency_ms",
        unit: "ms",
        description: "Redis operation latency in milliseconds");

    /// <summary>Redis cache hits</summary>
    public static readonly Counter<long> RedisCacheHits = Meter.CreateCounter<long>(
        "redis_cache_hits_total",
        unit: "{hit}",
        description: "Total Redis cache hits");

    /// <summary>Redis cache misses</summary>
    public static readonly Counter<long> RedisCacheMisses = Meter.CreateCounter<long>(
        "redis_cache_misses_total",
        unit: "{miss}",
        description: "Total Redis cache misses");

    // =============================================================================
    // ERROR METRICS
    // =============================================================================

    /// <summary>Unhandled exceptions</summary>
    public static readonly Counter<long> UnhandledExceptionsTotal = Meter.CreateCounter<long>(
        "unhandled_exceptions_total",
        unit: "{exception}",
        description: "Total unhandled exceptions by type");

    /// <summary>Database connection errors</summary>
    public static readonly Counter<long> DbConnectionErrorsTotal = Meter.CreateCounter<long>(
        "db_connection_errors_total",
        unit: "{error}",
        description: "Total database connection errors");

    /// <summary>Embedding service errors</summary>
    public static readonly Counter<long> EmbeddingErrorsTotal = Meter.CreateCounter<long>(
        "embedding_errors_total",
        unit: "{error}",
        description: "Total embedding service errors");

    // =============================================================================
    // OBSERVABLE GAUGES (for current state)
    // =============================================================================

    private static int _activeTenantCount = 0;
    private static long _totalMemoryCount = 0;
    private static long _totalEntityCount = 0;

    /// <summary>Total memories in system (gauge)</summary>
    public static readonly ObservableGauge<long> TotalMemories = Meter.CreateObservableGauge(
        "total_memories",
        () => _totalMemoryCount,
        unit: "{memory}",
        description: "Total memories stored in the knowledge graph");

    /// <summary>Total entities in system (gauge)</summary>
    public static readonly ObservableGauge<long> TotalEntities = Meter.CreateObservableGauge(
        "total_entities",
        () => _totalEntityCount,
        unit: "{entity}",
        description: "Total entities in the knowledge graph");

    // =============================================================================
    // HELPER METHODS
    // =============================================================================

    /// <summary>Update the active tenant count for the gauge</summary>
    public static void SetActiveTenantCount(int count) => _activeTenantCount = count;

    /// <summary>Update the total memory count for the gauge</summary>
    public static void SetTotalMemoryCount(long count) => _totalMemoryCount = count;

    /// <summary>Update the total entity count for the gauge</summary>
    public static void SetTotalEntityCount(long count) => _totalEntityCount = count;

    /// <summary>Record an HTTP request with all dimensions</summary>
    public static void RecordHttpRequest(string method, string pathPattern, int statusCode, double durationMs)
    {
        var tags = new TagList
        {
            { "method", method },
            { "path", pathPattern },
            { "status_code", statusCode.ToString() },
            { "status_class", $"{statusCode / 100}xx" }
        };

        HttpRequestsTotal.Add(1, tags);
        HttpRequestDurationMs.Record(durationMs, tags);
    }

    /// <summary>Record a database query with timing</summary>
    public static void RecordDbQuery(string operation, string table, double durationMs, bool success = true)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "table", table },
            { "success", success.ToString().ToLower() }
        };

        DbQueryCount.Add(1, tags);
        DbQueryDurationMs.Record(durationMs, tags);
    }

    /// <summary>Record a memory search operation</summary>
    public static void RecordSearch(string mode, int resultCount, double durationMs, string? tenantId = null)
    {
        var tags = new TagList
        {
            { "mode", mode },
            { "result_bucket", GetResultBucket(resultCount) }
        };

        if (tenantId != null)
            tags.Add("tenant_id", tenantId);

        SearchOperationsTotal.Add(1, tags);
        SearchDurationMs.Record(durationMs, tags);
        MemoriesReadTotal.Add(resultCount, tags);
    }

    /// <summary>Record memory ingestion</summary>
    public static void RecordMemoryIngested(int entityCount, int relationshipCount, string? tenantId = null)
    {
        var tags = tenantId != null ? new TagList { { "tenant_id", tenantId } } : new TagList();

        MemoriesCreatedTotal.Add(1, tags);
        EntitiesExtractedTotal.Add(entityCount, tags);
        RelationshipsCreatedTotal.Add(relationshipCount, tags);
    }

    /// <summary>Record credit consumption (credits stored as centis for precision)</summary>
    public static void RecordCreditsConsumed(string operation, decimal credits, string tenantId)
    {
        // Store as centis (hundredths) for fractional credit support (0.25 credits = 25 centis)
        var creditCentis = (long)Math.Round(credits * 100);
        CreditsConsumedTotal.Add(creditCentis, new TagList
        {
            { "operation", operation },
            { "tenant_id", tenantId }
        });
    }

    /// <summary>Record rate limit violation</summary>
    public static void RecordRateLimitViolation(string endpoint, string tenantId)
    {
        RateLimitViolationsTotal.Add(1, new TagList
        {
            { "endpoint", endpoint },
            { "tenant_id", tenantId }
        });
    }

    /// <summary>Record authentication attempt</summary>
    public static void RecordAuth(bool success, string method, string? failureReason = null)
    {
        if (success)
        {
            SuccessfulAuthTotal.Add(1, new TagList { { "method", method } });
        }
        else
        {
            FailedAuthTotal.Add(1, new TagList
            {
                { "method", method },
                { "reason", failureReason ?? "unknown" }
            });
        }
    }

    private static string GetResultBucket(int count) => count switch
    {
        0 => "0",
        <= 5 => "1-5",
        <= 10 => "6-10",
        <= 25 => "11-25",
        _ => "25+"
    };
}

/// <summary>
/// Disposable scope for timing database operations
/// </summary>
public readonly struct DbQueryScope : IDisposable
{
    private readonly Stopwatch _stopwatch;
    private readonly string _operation;
    private readonly string _table;

    public DbQueryScope(string operation, string table)
    {
        _operation = operation;
        _table = table;
        _stopwatch = Stopwatch.StartNew();
        Metrics.ActiveDbConnections.Add(1);
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        Metrics.ActiveDbConnections.Add(-1);
        Metrics.RecordDbQuery(_operation, _table, _stopwatch.Elapsed.TotalMilliseconds);
    }
}

/// <summary>
/// Disposable scope for timing HTTP requests
/// </summary>
public struct HttpRequestScope : IDisposable
{
    private readonly Stopwatch _stopwatch;
    private readonly string _method;
    private readonly string _pathPattern;
    private int _statusCode;

    public HttpRequestScope(string method, string pathPattern)
    {
        _method = method;
        _pathPattern = pathPattern;
        _statusCode = 200;
        _stopwatch = Stopwatch.StartNew();
        Metrics.ActiveHttpConnections.Add(1);
    }

    public void SetStatusCode(int statusCode) => _statusCode = statusCode;

    public void Dispose()
    {
        _stopwatch.Stop();
        Metrics.ActiveHttpConnections.Add(-1);
        Metrics.RecordHttpRequest(_method, _pathPattern, _statusCode, _stopwatch.Elapsed.TotalMilliseconds);
    }
}
