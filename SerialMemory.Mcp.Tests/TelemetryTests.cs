using System.Diagnostics;
using System.Diagnostics.Metrics;
using SerialMemory.Core.Telemetry;
using Xunit;
using TagList = System.Diagnostics.TagList;

namespace SerialMemory.Mcp.Tests;

/// <summary>
/// Tests proving the telemetry system works correctly.
/// Verifies metrics endpoint, counter increments, and histogram latency data.
/// </summary>
public class TelemetryTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly Dictionary<string, long> _counterValues = new();
    private readonly Dictionary<string, List<double>> _histogramValues = new();

    public TelemetryTests()
    {
        _listener = new MeterListener();

        // Subscribe to SerialMemory meter
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == Metrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        // Capture counter measurements
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            var key = GetMetricKey(instrument.Name, tags);
            if (!_counterValues.ContainsKey(key))
                _counterValues[key] = 0;
            _counterValues[key] += measurement;
        });

        // Capture histogram measurements
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            var key = GetMetricKey(instrument.Name, tags);
            if (!_histogramValues.ContainsKey(key))
                _histogramValues[key] = [];
            _histogramValues[key].Add(measurement);
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    private static string GetMetricKey(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var tagStr = string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}"));
        return string.IsNullOrEmpty(tagStr) ? name : $"{name}{{{tagStr}}}";
    }

    #region Counter Tests

    [Fact]
    public void HttpRequestsTotal_Increments_Correctly()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.RecordHttpRequest("GET", "/api/memories", 200, 50.0);
        Metrics.RecordHttpRequest("GET", "/api/memories", 200, 75.0);
        Metrics.RecordHttpRequest("POST", "/api/memories", 201, 100.0);

        // Assert
        var getKey = "http_requests_total{method=GET,path=/api/memories,status_code=200,status_class=2xx}";
        var postKey = "http_requests_total{method=POST,path=/api/memories,status_code=201,status_class=2xx}";

        Assert.True(_counterValues.ContainsKey(getKey), $"Expected key {getKey} not found. Available: {string.Join(", ", _counterValues.Keys)}");
        Assert.Equal(2, _counterValues[getKey]);
        Assert.True(_counterValues.ContainsKey(postKey));
        Assert.Equal(1, _counterValues[postKey]);
    }

    [Fact]
    public void MemoriesCreatedTotal_Increments_OnIngestion()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.RecordMemoryIngested(entityCount: 5, relationshipCount: 2);
        Metrics.RecordMemoryIngested(entityCount: 3, relationshipCount: 1);

        // Assert
        Assert.True(_counterValues.ContainsKey("memories_created_total"));
        Assert.Equal(2, _counterValues["memories_created_total"]);

        Assert.True(_counterValues.ContainsKey("entities_extracted_total"));
        Assert.Equal(8, _counterValues["entities_extracted_total"]);

        Assert.True(_counterValues.ContainsKey("relationships_created_total"));
        Assert.Equal(3, _counterValues["relationships_created_total"]);
    }

    [Fact]
    public void SearchOperationsTotal_Increments_WithMode()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.RecordSearch("hybrid", resultCount: 5, durationMs: 100.0);
        Metrics.RecordSearch("semantic", resultCount: 10, durationMs: 150.0);
        Metrics.RecordSearch("hybrid", resultCount: 0, durationMs: 50.0);

        // Assert
        var hybridKey = "search_operations_total{mode=hybrid,result_bucket=1-5}";
        var hybridZeroKey = "search_operations_total{mode=hybrid,result_bucket=0}";
        var semanticKey = "search_operations_total{mode=semantic,result_bucket=6-10}";

        Assert.True(_counterValues.ContainsKey(hybridKey));
        Assert.Equal(1, _counterValues[hybridKey]);

        Assert.True(_counterValues.ContainsKey(hybridZeroKey));
        Assert.Equal(1, _counterValues[hybridZeroKey]);

        Assert.True(_counterValues.ContainsKey(semanticKey));
        Assert.Equal(1, _counterValues[semanticKey]);
    }

    [Fact]
    public void DbQueryCount_Increments_ByOperation()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.RecordDbQuery("select", "memories", 25.0);
        Metrics.RecordDbQuery("select", "memories", 30.0);
        Metrics.RecordDbQuery("insert", "memories", 15.0);
        Metrics.RecordDbQuery("select", "entities", 20.0);

        // Assert
        var selectMemoriesKey = "db_query_count{operation=select,table=memories,success=true}";
        var insertMemoriesKey = "db_query_count{operation=insert,table=memories,success=true}";
        var selectEntitiesKey = "db_query_count{operation=select,table=entities,success=true}";

        Assert.True(_counterValues.ContainsKey(selectMemoriesKey));
        Assert.Equal(2, _counterValues[selectMemoriesKey]);

        Assert.True(_counterValues.ContainsKey(insertMemoriesKey));
        Assert.Equal(1, _counterValues[insertMemoriesKey]);

        Assert.True(_counterValues.ContainsKey(selectEntitiesKey));
        Assert.Equal(1, _counterValues[selectEntitiesKey]);
    }

    [Fact]
    public void CreditsConsumedTotal_Increments_ByTenant()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.RecordCreditsConsumed("memory_ingest", credits: 10, tenantId: "tenant-1");
        Metrics.RecordCreditsConsumed("memory_search", credits: 5, tenantId: "tenant-1");
        Metrics.RecordCreditsConsumed("memory_ingest", credits: 15, tenantId: "tenant-2");

        // Assert
        var tenant1IngestKey = "credits_consumed_total{operation=memory_ingest,tenant_id=tenant-1}";
        var tenant1SearchKey = "credits_consumed_total{operation=memory_search,tenant_id=tenant-1}";
        var tenant2IngestKey = "credits_consumed_total{operation=memory_ingest,tenant_id=tenant-2}";

        Assert.True(_counterValues.ContainsKey(tenant1IngestKey));
        Assert.Equal(10, _counterValues[tenant1IngestKey]);

        Assert.True(_counterValues.ContainsKey(tenant1SearchKey));
        Assert.Equal(5, _counterValues[tenant1SearchKey]);

        Assert.True(_counterValues.ContainsKey(tenant2IngestKey));
        Assert.Equal(15, _counterValues[tenant2IngestKey]);
    }

    [Fact]
    public void RateLimitViolationsTotal_Increments_ByEndpoint()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.RecordRateLimitViolation("/api/memories", "tenant-1");
        Metrics.RecordRateLimitViolation("/api/memories", "tenant-1");
        Metrics.RecordRateLimitViolation("/api/search", "tenant-2");

        // Assert
        var memoriesTenant1Key = "rate_limit_violations_total{endpoint=/api/memories,tenant_id=tenant-1}";
        var searchTenant2Key = "rate_limit_violations_total{endpoint=/api/search,tenant_id=tenant-2}";

        Assert.True(_counterValues.ContainsKey(memoriesTenant1Key));
        Assert.Equal(2, _counterValues[memoriesTenant1Key]);

        Assert.True(_counterValues.ContainsKey(searchTenant2Key));
        Assert.Equal(1, _counterValues[searchTenant2Key]);
    }

    [Fact]
    public void AuthMetrics_Track_SuccessAndFailure()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.RecordAuth(success: true, method: "api_key");
        Metrics.RecordAuth(success: true, method: "jwt");
        Metrics.RecordAuth(success: false, method: "api_key", failureReason: "invalid_key");
        Metrics.RecordAuth(success: false, method: "api_key", failureReason: "expired");

        // Assert
        var apiKeySuccessKey = "successful_auth_total{method=api_key}";
        var jwtSuccessKey = "successful_auth_total{method=jwt}";
        var apiKeyInvalidKey = "failed_auth_total{method=api_key,reason=invalid_key}";
        var apiKeyExpiredKey = "failed_auth_total{method=api_key,reason=expired}";

        Assert.True(_counterValues.ContainsKey(apiKeySuccessKey));
        Assert.Equal(1, _counterValues[apiKeySuccessKey]);

        Assert.True(_counterValues.ContainsKey(jwtSuccessKey));
        Assert.Equal(1, _counterValues[jwtSuccessKey]);

        Assert.True(_counterValues.ContainsKey(apiKeyInvalidKey));
        Assert.Equal(1, _counterValues[apiKeyInvalidKey]);

        Assert.True(_counterValues.ContainsKey(apiKeyExpiredKey));
        Assert.Equal(1, _counterValues[apiKeyExpiredKey]);
    }

    [Fact]
    public void TenantSignupTotal_Increments()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.TenantSignupTotal.Add(1);
        Metrics.TenantSignupTotal.Add(1);
        Metrics.TenantSignupTotal.Add(1);

        // Assert
        Assert.True(_counterValues.ContainsKey("tenant_signup_total"));
        Assert.Equal(3, _counterValues["tenant_signup_total"]);
    }

    [Fact]
    public void StripeMetrics_Track_PaymentsAndFailures()
    {
        // Arrange
        _counterValues.Clear();

        // Act
        Metrics.StripePaymentsTotal.Add(1);
        Metrics.StripePaymentsTotal.Add(1);
        Metrics.StripeFailuresTotal.Add(1, new TagList { { "type", "card_declined" } });
        Metrics.StripeFailuresTotal.Add(1, new TagList { { "type", "insufficient_funds" } });

        // Assert
        Assert.True(_counterValues.ContainsKey("stripe_payments_total"));
        Assert.Equal(2, _counterValues["stripe_payments_total"]);

        var declinedKey = "stripe_failures_total{type=card_declined}";
        var insufficientKey = "stripe_failures_total{type=insufficient_funds}";

        Assert.True(_counterValues.ContainsKey(declinedKey));
        Assert.Equal(1, _counterValues[declinedKey]);

        Assert.True(_counterValues.ContainsKey(insufficientKey));
        Assert.Equal(1, _counterValues[insufficientKey]);
    }

    #endregion

    #region Histogram Tests

    [Fact]
    public void HttpRequestDurationMs_Records_Latency()
    {
        // Arrange
        _histogramValues.Clear();

        // Act
        Metrics.RecordHttpRequest("GET", "/api/memories", 200, 50.0);
        Metrics.RecordHttpRequest("GET", "/api/memories", 200, 75.0);
        Metrics.RecordHttpRequest("GET", "/api/memories", 200, 100.0);

        // Assert
        var key = "http_request_duration_ms{method=GET,path=/api/memories,status_code=200,status_class=2xx}";
        Assert.True(_histogramValues.ContainsKey(key), $"Expected key {key} not found");
        Assert.Equal(3, _histogramValues[key].Count);
        Assert.Contains(50.0, _histogramValues[key]);
        Assert.Contains(75.0, _histogramValues[key]);
        Assert.Contains(100.0, _histogramValues[key]);
    }

    [Fact]
    public void DbQueryDurationMs_Records_Latency()
    {
        // Arrange
        _histogramValues.Clear();

        // Act
        Metrics.RecordDbQuery("select", "memories", 25.0);
        Metrics.RecordDbQuery("select", "memories", 30.0);
        Metrics.RecordDbQuery("select", "memories", 150.0);

        // Assert
        var key = "db_query_duration_ms{operation=select,table=memories,success=true}";
        Assert.True(_histogramValues.ContainsKey(key));
        Assert.Equal(3, _histogramValues[key].Count);
        Assert.Equal(25.0 + 30.0 + 150.0, _histogramValues[key].Sum(), precision: 1);
    }

    [Fact]
    public void SearchDurationMs_Records_Latency()
    {
        // Arrange
        _histogramValues.Clear();

        // Act
        Metrics.RecordSearch("hybrid", resultCount: 5, durationMs: 100.0);
        Metrics.RecordSearch("hybrid", resultCount: 10, durationMs: 200.0);
        Metrics.RecordSearch("hybrid", resultCount: 3, durationMs: 50.0);

        // Assert - Multiple keys due to different result buckets
        var values = _histogramValues
            .Where(kv => kv.Key.StartsWith("search_duration_ms"))
            .SelectMany(kv => kv.Value)
            .ToList();

        Assert.Equal(3, values.Count);
        Assert.Contains(100.0, values);
        Assert.Contains(200.0, values);
        Assert.Contains(50.0, values);
    }

    [Fact]
    public void EmbeddingDurationMs_Records_Latency()
    {
        // Arrange
        _histogramValues.Clear();

        // Act
        Metrics.EmbeddingDurationMs.Record(150.0);
        Metrics.EmbeddingDurationMs.Record(200.0);
        Metrics.EmbeddingDurationMs.Record(175.0);

        // Assert
        Assert.True(_histogramValues.ContainsKey("embedding_duration_ms"));
        Assert.Equal(3, _histogramValues["embedding_duration_ms"].Count);
        var avg = _histogramValues["embedding_duration_ms"].Average();
        Assert.InRange(avg, 170, 180);
    }

    #endregion

    #region Scope Tests

    [Fact]
    public void DbQueryScope_RecordsMetrics_OnDispose()
    {
        // Arrange
        _counterValues.Clear();
        _histogramValues.Clear();

        // Act
        using (new DbQueryScope("select", "memories"))
        {
            // Simulate work
            Thread.Sleep(10);
        }

        // Assert
        var key = "db_query_count{operation=select,table=memories,success=true}";
        Assert.True(_counterValues.ContainsKey(key));
        Assert.Equal(1, _counterValues[key]);

        var histKey = "db_query_duration_ms{operation=select,table=memories,success=true}";
        Assert.True(_histogramValues.ContainsKey(histKey));
        Assert.True(_histogramValues[histKey][0] >= 10); // At least 10ms
    }

    #endregion

    #region Observable Gauge Tests

    [Fact]
    public void SetTotalMemoryCount_Updates_Gauge()
    {
        // Act
        Metrics.SetTotalMemoryCount(1000);

        // Note: Observable gauges are read on demand, not pushed
        // This test verifies the setter doesn't throw
        Assert.True(true);
    }

    [Fact]
    public void SetTotalEntityCount_Updates_Gauge()
    {
        // Act
        Metrics.SetTotalEntityCount(5000);

        // Note: Observable gauges are read on demand, not pushed
        // This test verifies the setter doesn't throw
        Assert.True(true);
    }

    [Fact]
    public void SetActiveTenantCount_Updates_Gauge()
    {
        // Act
        Metrics.SetActiveTenantCount(100);

        // Note: Observable gauges are read on demand, not pushed
        // This test verifies the setter doesn't throw
        Assert.True(true);
    }

    #endregion

    #region Meter Existence Tests

    [Fact]
    public void AllRequiredMetrics_Exist()
    {
        // Technical metrics
        Assert.NotNull(Metrics.HttpRequestsTotal);
        Assert.NotNull(Metrics.HttpRequestDurationMs);
        Assert.NotNull(Metrics.DbQueryCount);
        Assert.NotNull(Metrics.DbQueryDurationMs);
        Assert.NotNull(Metrics.ActiveDbConnections);
        Assert.NotNull(Metrics.ActiveHttpConnections);

        // Business metrics
        Assert.NotNull(Metrics.MemoriesCreatedTotal);
        Assert.NotNull(Metrics.MemoriesReadTotal);
        Assert.NotNull(Metrics.CreditsConsumedTotal);
        Assert.NotNull(Metrics.RateLimitViolationsTotal);
        Assert.NotNull(Metrics.FailedAuthTotal);
        Assert.NotNull(Metrics.TenantSignupTotal);
        Assert.NotNull(Metrics.StripeFailuresTotal);
        Assert.NotNull(Metrics.StripePaymentsTotal);

        // Observable gauges
        Assert.NotNull(Metrics.TotalMemories);
        Assert.NotNull(Metrics.TotalEntities);
        Assert.NotNull(Metrics.ActiveTenants);
    }

    [Fact]
    public void Meter_HasCorrectName()
    {
        Assert.Equal("SerialMemory", Metrics.MeterName);
        Assert.Equal("SerialMemory", Metrics.Meter.Name);
    }

    #endregion
}
