using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class PerformanceModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public PerformanceModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public MetricsSnapshot? Metrics { get; set; }
    public IReadOnlyList<LatencyItem> LatencyTable { get; set; } = [];
    public IReadOnlyList<SlowOperation> SlowOps { get; set; } = [];
    public CacheStats? Cache { get; set; }
    public DbPoolStats? DbPool { get; set; }
    public IReadOnlyList<ActiveRequest> ActiveRequests { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadPerformanceDataAsync();
    }

    public async Task<IActionResult> OnPostSetThresholdAsync(string operation, int thresholdMs)
    {
        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsJsonAsync("/api/performance/threshold", new { operation, thresholdMs });
            SuccessMessage = response.IsSuccessStatusCode ? $"Threshold set for {operation}" : "Failed to set threshold";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }
        await LoadPerformanceDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResetMetricsAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsync("/api/performance/reset", null);
            SuccessMessage = response.IsSuccessStatusCode ? "Metrics reset successfully" : "Failed to reset metrics";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }
        await LoadPerformanceDataAsync();
        return Page();
    }

    private async Task LoadPerformanceDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();

            var metricsTask = client.GetFromJsonAsync<MetricsSnapshot>("/api/performance/metrics");
            var slowTask = client.GetFromJsonAsync<SlowOpsResponse>("/api/performance/slow?limit=20");
            var cacheTask = client.GetFromJsonAsync<CacheStats>("/api/performance/cache");
            var dbTask = client.GetFromJsonAsync<DbPoolStats>("/api/performance/db");
            var activeTask = client.GetFromJsonAsync<ActiveRequestsResponse>("/api/performance/active");

            await Task.WhenAll(metricsTask, slowTask, cacheTask, dbTask, activeTask);

            Metrics = await metricsTask;
            SlowOps = (await slowTask)?.Operations ?? [];
            Cache = await cacheTask;
            DbPool = await dbTask;
            ActiveRequests = (await activeTask)?.Requests ?? [];

            // Build latency table from metrics
            if (Metrics?.OperationLatencies != null)
            {
                LatencyTable = Metrics.OperationLatencies.Select(kv => new LatencyItem
                {
                    Operation = kv.Key,
                    AvgMs = kv.Value.AvgMs,
                    P95Ms = kv.Value.P95Ms,
                    P99Ms = kv.Value.P99Ms,
                    Count = kv.Value.Count
                }).OrderByDescending(l => l.AvgMs).ToList();
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load performance data: {ex.Message}";
        }
    }

    public sealed class MetricsSnapshot
    {
        public long TotalRequests { get; init; }
        public long TotalErrors { get; init; }
        public double AvgLatencyMs { get; init; }
        public double P95LatencyMs { get; init; }
        public double P99LatencyMs { get; init; }
        public double RequestsPerSecond { get; init; }
        public Dictionary<string, LatencyStats>? OperationLatencies { get; init; }
        public DateTimeOffset Timestamp { get; init; }
    }

    public sealed class LatencyStats
    {
        public double AvgMs { get; init; }
        public double P95Ms { get; init; }
        public double P99Ms { get; init; }
        public long Count { get; init; }
    }

    public sealed class LatencyItem
    {
        public string Operation { get; init; } = "";
        public double AvgMs { get; init; }
        public double P95Ms { get; init; }
        public double P99Ms { get; init; }
        public long Count { get; init; }
    }

    public sealed class SlowOpsResponse
    {
        public IReadOnlyList<SlowOperation>? Operations { get; init; }
    }

    public sealed class SlowOperation
    {
        public string Operation { get; init; } = "";
        public double DurationMs { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public string? Details { get; init; }
    }

    public sealed class CacheStats
    {
        public long Hits { get; init; }
        public long Misses { get; init; }
        public double HitRate { get; init; }
        public long ItemCount { get; init; }
        public long MemoryBytes { get; init; }
        public long Evictions { get; init; }
    }

    public sealed class DbPoolStats
    {
        public int ActiveConnections { get; init; }
        public int IdleConnections { get; init; }
        public int MaxConnections { get; init; }
        public long TotalAcquired { get; init; }
        public long TotalReleased { get; init; }
        public double AvgAcquireTimeMs { get; init; }
    }

    public sealed class ActiveRequestsResponse
    {
        public IReadOnlyList<ActiveRequest>? Requests { get; init; }
    }

    public sealed class ActiveRequest
    {
        public string RequestId { get; init; } = "";
        public string Operation { get; init; } = "";
        public double ElapsedMs { get; init; }
        public DateTimeOffset StartedAt { get; init; }
    }
}
