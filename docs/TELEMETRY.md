# SerialMemory Telemetry Guide

Production-grade observability for safety, cost tracking, and capacity planning.

## Overview

SerialMemory uses OpenTelemetry with Prometheus-compatible metrics to provide comprehensive observability. All metrics are exposed via the `/metrics` endpoint in Prometheus text format.

## Quick Start

### Enabling Telemetry

Add to your ASP.NET Core service:

```csharp
using SerialMemory.Core.Telemetry;

// In Program.cs
builder.Services.AddSerialMemoryTelemetry("SerialMemory.Api", "1.0.0");

var app = builder.Build();

// Add metrics middleware early in pipeline
app.UseSerialMemoryMetrics();

// Map /metrics endpoint
app.MapSerialMemoryMetrics();
```

### Scraping Metrics

```bash
# Test metrics endpoint
curl http://localhost:5000/metrics
```

## Available Metrics

### Technical Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `http_requests_total` | Counter | Total HTTP requests by method, path, status |
| `http_request_duration_ms` | Histogram | HTTP request latency (P50, P95, P99) |
| `db_query_count` | Counter | Database queries by operation type |
| `db_query_duration_ms` | Histogram | Database query latency |
| `active_db_connections` | UpDownCounter | Current active DB connections |
| `active_http_connections` | UpDownCounter | Current active HTTP connections |
| `embedding_duration_ms` | Histogram | Embedding generation time |
| `entity_extraction_duration_ms` | Histogram | Entity extraction time |

### Business Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `memories_created_total` | Counter | Memories ingested |
| `memories_read_total` | Counter | Memories retrieved |
| `entities_extracted_total` | Counter | Entities extracted |
| `relationships_created_total` | Counter | Relationships created |
| `search_operations_total` | Counter | Searches by mode |
| `search_duration_ms` | Histogram | Search latency |

### SaaS Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `credits_consumed_total` | Counter | Credits by operation and tenant |
| `rate_limit_violations_total` | Counter | Rate limit hits |
| `failed_auth_total` | Counter | Auth failures by reason |
| `successful_auth_total` | Counter | Successful auths |
| `tenant_signup_total` | Counter | New signups |
| `stripe_payments_total` | Counter | Successful payments |
| `stripe_failures_total` | Counter | Payment failures |
| `active_tenants` | Gauge | Current tenant count |

### Infrastructure Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `rabbit_published_total` | Counter | RabbitMQ messages published |
| `rabbit_consumed_total` | Counter | RabbitMQ messages consumed |
| `redis_latency_ms` | Histogram | Redis operation latency |
| `redis_cache_hits_total` | Counter | Cache hits |
| `redis_cache_misses_total` | Counter | Cache misses |

### Error Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `unhandled_exceptions_total` | Counter | Exceptions by type |
| `db_connection_errors_total` | Counter | DB connection failures |
| `embedding_errors_total` | Counter | Embedding service errors |

## Recording Metrics

### HTTP Requests (automatic via middleware)

The `MetricsMiddleware` automatically records:
- Request count
- Duration histogram
- Status codes with path pattern normalization

### Database Queries

Use the `DbQueryScope` for timing:

```csharp
using (new DbQueryScope("select", "memories"))
{
    await connection.QueryAsync<Memory>(sql);
}
```

### Business Operations

```csharp
// Memory ingestion
Metrics.RecordMemoryIngested(entityCount: 5, relationshipCount: 2, tenantId: "tenant-1");

// Search operations
Metrics.RecordSearch("hybrid", resultCount: 10, durationMs: 150.0, tenantId: "tenant-1");

// Credit consumption
Metrics.RecordCreditsConsumed("memory_ingest", credits: 1, tenantId: "tenant-1");

// Rate limiting
Metrics.RecordRateLimitViolation("/api/memories", tenantId: "tenant-1");

// Authentication
Metrics.RecordAuth(success: true, method: "api_key");
Metrics.RecordAuth(success: false, method: "jwt", failureReason: "expired");
```

## Prometheus Configuration

Add to `prometheus.yml`:

```yaml
scrape_configs:
  - job_name: 'serialmemory-api'
    static_configs:
      - targets: ['localhost:5000']
    scrape_interval: 15s
    metrics_path: /metrics

  - job_name: 'serialmemory-dashboard'
    static_configs:
      - targets: ['localhost:5001']
    scrape_interval: 15s
    metrics_path: /metrics
```

## Grafana Dashboard

Import the dashboard from `ops/grafana/dashboards/serialmemory-overview.json`.

### Key Panels

1. **Overview Row**
   - Request Rate (RPS)
   - P95 Latency
   - Error Rate
   - Total Memories
   - Active Tenants
   - Active DB Connections

2. **HTTP Traffic**
   - Request rate by path
   - Latency distribution (P50, P95, P99)

3. **Business Metrics**
   - Memory operations
   - Search operations by mode
   - Credits consumed by operation

4. **Database**
   - Query rate by operation
   - Query latency by table

5. **Security & Billing**
   - Failed auth by reason
   - Rate limit violations
   - Payments and signups

6. **Runtime Metrics**
   - GC collections
   - ThreadPool usage
   - Memory usage

## Alerting Examples

### High Error Rate

```yaml
- alert: HighErrorRate
  expr: |
    100 * sum(rate(http_requests_total{status_class="5xx"}[5m]))
    / sum(rate(http_requests_total[5m])) > 5
  for: 5m
  labels:
    severity: critical
  annotations:
    summary: "High error rate detected"
```

### Slow Requests

```yaml
- alert: SlowRequests
  expr: |
    histogram_quantile(0.95, sum(rate(http_request_duration_ms_bucket[5m])) by (le)) > 500
  for: 5m
  labels:
    severity: warning
  annotations:
    summary: "P95 latency above 500ms"
```

### Credit Exhaustion

```yaml
- alert: TenantCreditsLow
  expr: |
    credits_remaining / monthly_credit_limit < 0.1
  for: 1h
  labels:
    severity: warning
  annotations:
    summary: "Tenant approaching credit limit"
```

## Runtime Metrics (Automatic)

OpenTelemetry automatically collects:

- **GC Metrics**: `process_runtime_dotnet_gc_collections_count_total`, `process_runtime_dotnet_gc_heap_size_bytes`
- **ThreadPool**: `process_runtime_dotnet_threadpool_threads_count`, `process_runtime_dotnet_threadpool_queue_length`
- **Process**: `process_memory_usage_bytes`, `process_cpu_seconds_total`

## Best Practices

1. **Path Normalization**: The middleware automatically replaces GUIDs and numeric IDs with `{id}` to prevent metric cardinality explosion.

2. **Histogram Buckets**: Custom bucket boundaries are configured for latency metrics to provide accurate percentile calculations.

3. **Tag Cardinality**: Keep tag values bounded. Avoid using unbounded values like user IDs directly in tags.

4. **Scrape Interval**: Use 15-30 second scrape intervals for most metrics. Increase for high-cardinality metrics.

5. **Retention**: Configure Prometheus retention based on your needs. 15 days is typical for operational metrics.

## Testing Metrics

Run the telemetry tests:

```bash
dotnet test --filter "FullyQualifiedName~TelemetryTests"
```

Tests verify:
- Counter increments correctly
- Histogram records latency data
- Tags are applied correctly
- All required metrics exist
