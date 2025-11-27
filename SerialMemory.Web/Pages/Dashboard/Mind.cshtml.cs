using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class MindModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MindModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public MindHealthScore? HealthScore { get; set; }
    public IReadOnlyList<AlertItem> ActiveAlerts { get; set; } = [];
    public IReadOnlyList<DailyTrendPoint> DailyTrend { get; set; } = [];
    public IReadOnlyList<DriftCalibrationPoint> DriftCalibration { get; set; } = [];
    public IReadOnlyList<HallucinationFlag> Hallucinations { get; set; } = [];
    public IReadOnlyList<ContradictionFlag> Contradictions { get; set; } = [];
    public MindStats? Stats { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public string ActiveTab { get; set; } = "overview";

    public async Task OnGetAsync(string? tab)
    {
        ActiveTab = tab ?? "overview";
        await LoadMindDataAsync();
    }

    public async Task<IActionResult> OnPostRecalibrateAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsync("/api/mind/recalibrate", null);
            SuccessMessage = response.IsSuccessStatusCode
                ? "Recalibration started"
                : "Failed to start recalibration";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMindDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDismissAlertAsync(Guid alertId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/mind/alerts/dismiss", new { alertId });
            SuccessMessage = response.IsSuccessStatusCode
                ? "Alert dismissed"
                : "Failed to dismiss alert";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMindDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAcknowledgeHallucinationAsync(Guid memoryId, bool confirm)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/mind/hallucinations/acknowledge", new { memoryId, confirm });
            SuccessMessage = response.IsSuccessStatusCode
                ? (confirm ? "Hallucination confirmed and memory invalidated" : "Hallucination dismissed")
                : "Failed to process hallucination";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMindDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResolveContradictionAsync(Guid contradictionId, string resolution)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/mind/contradictions/resolve", new { contradictionId, resolution });
            SuccessMessage = response.IsSuccessStatusCode
                ? "Contradiction resolved"
                : "Failed to resolve contradiction";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMindDataAsync();
        return Page();
    }

    private async Task LoadMindDataAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");

            var healthTask = client.GetFromJsonAsync<MindHealthScore>("/api/mind/health");
            var alertsTask = client.GetFromJsonAsync<AlertsResponse>("/api/mind/alerts?limit=20");
            var trendTask = client.GetFromJsonAsync<TrendResponse>("/api/mind/trend?days=30");
            var driftTask = client.GetFromJsonAsync<DriftResponse>("/api/mind/drift");
            var hallucinationsTask = client.GetFromJsonAsync<HallucinationsResponse>("/api/mind/hallucinations?limit=20");
            var contradictionsTask = client.GetFromJsonAsync<ContradictionsResponse>("/api/mind/contradictions?limit=20");
            var statsTask = client.GetFromJsonAsync<MindStats>("/api/mind/stats");

            await Task.WhenAll(healthTask, alertsTask, trendTask, driftTask, hallucinationsTask, contradictionsTask, statsTask);

            HealthScore = await healthTask;
            ActiveAlerts = (await alertsTask)?.Items ?? [];
            DailyTrend = (await trendTask)?.Points ?? [];
            DriftCalibration = (await driftTask)?.Points ?? [];
            Hallucinations = (await hallucinationsTask)?.Items ?? [];
            Contradictions = (await contradictionsTask)?.Items ?? [];
            Stats = await statsTask;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load mind health data: {ex.Message}";
        }
    }

    public sealed class AlertsResponse { public IReadOnlyList<AlertItem>? Items { get; init; } }
    public sealed class TrendResponse { public IReadOnlyList<DailyTrendPoint>? Points { get; init; } }
    public sealed class DriftResponse { public IReadOnlyList<DriftCalibrationPoint>? Points { get; init; } }
    public sealed class HallucinationsResponse { public IReadOnlyList<HallucinationFlag>? Items { get; init; } }
    public sealed class ContradictionsResponse { public IReadOnlyList<ContradictionFlag>? Items { get; init; } }

    public sealed class MindHealthScore
    {
        public double OverallScore { get; init; }
        public double CoherenceScore { get; init; }
        public double ConsistencyScore { get; init; }
        public double ReliabilityScore { get; init; }
        public double FreshnessScore { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset CalculatedAt { get; init; }
    }

    public sealed class AlertItem
    {
        public Guid Id { get; init; }
        public string AlertType { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Message { get; init; } = "";
        public Guid? RelatedMemoryId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public bool IsAcknowledged { get; init; }
    }

    public sealed class DailyTrendPoint
    {
        public DateTimeOffset Date { get; init; }
        public double HealthScore { get; init; }
        public int MemoriesAdded { get; init; }
        public int MemoriesDecayed { get; init; }
        public int AlertsTriggered { get; init; }
    }

    public sealed class DriftCalibrationPoint
    {
        public DateTimeOffset Timestamp { get; init; }
        public double ExpectedConfidence { get; init; }
        public double ActualConfidence { get; init; }
        public double Drift { get; init; }
    }

    public sealed class HallucinationFlag
    {
        public Guid MemoryId { get; init; }
        public string Content { get; init; } = "";
        public double HallucinationScore { get; init; }
        public string? Reason { get; init; }
        public DateTimeOffset FlaggedAt { get; init; }
        public bool IsConfirmed { get; init; }
        public bool IsDismissed { get; init; }
    }

    public sealed class ContradictionFlag
    {
        public Guid Id { get; init; }
        public Guid MemoryAId { get; init; }
        public Guid MemoryBId { get; init; }
        public string MemoryAContent { get; init; } = "";
        public string MemoryBContent { get; init; } = "";
        public double SimilarityScore { get; init; }
        public string? Explanation { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
        public bool IsResolved { get; init; }
    }

    public sealed class MindStats
    {
        public long TotalMemories { get; init; }
        public long ActiveMemories { get; init; }
        public long ArchivedMemories { get; init; }
        public double AvgConfidence { get; init; }
        public int HallucinationCount { get; init; }
        public int ContradictionCount { get; init; }
        public int UnresolvedAlerts { get; init; }
        public DateTimeOffset? LastRecalibration { get; init; }
    }
}
