using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class MindModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public MindModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
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
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
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
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
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
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsync($"/api/mind/hallucinations/flag/{memoryId}", null);
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
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
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
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");

            var healthTask = client.GetFromJsonAsync<MindHealthScore>("/api/mind/health");
            var trendTask = client.GetFromJsonAsync<TrendResponse>("/api/mind/trends?days=30");
            var driftTask = client.GetFromJsonAsync<DriftResponse>("/api/mind/confidence?days=30");
            var hallucinationsTask = client.GetFromJsonAsync<HallucinationsListResponse>("/api/mind/hallucinations/list?limit=20");
            var contradictionsTask = client.GetFromJsonAsync<ContradictionsListResponse>("/api/mind/contradictions/unresolved?limit=20");

            await Task.WhenAll(healthTask, trendTask, driftTask, hallucinationsTask, contradictionsTask);

            HealthScore = await healthTask;
            ActiveAlerts = []; // Alerts not currently supported in main API
            DailyTrend = (await trendTask)?.Points ?? [];
            DriftCalibration = (await driftTask)?.Points ?? [];

            // Map hallucination events to display format
            var hallucinationEvents = (await hallucinationsTask)?.Items ?? [];
            Hallucinations = hallucinationEvents.Select(h => new HallucinationFlag
            {
                MemoryId = h.MemoryId ?? Guid.Empty,
                Content = h.Description,
                HallucinationScore = h.Severity,
                Reason = h.Evidence,
                FlaggedAt = h.DetectedAt,
                IsConfirmed = false,
                IsDismissed = h.Resolved
            }).ToList();

            // Map contradiction events to display format
            var contradictionEvents = (await contradictionsTask)?.Contradictions ?? [];
            Contradictions = contradictionEvents.Select(c => new ContradictionFlag
            {
                Id = c.Id,
                MemoryAId = c.MemoryA,
                MemoryBId = c.MemoryB,
                MemoryAContent = c.Description,
                MemoryBContent = "",
                SimilarityScore = c.SimilarityScore,
                Explanation = c.Description,
                DetectedAt = c.DetectedAt,
                IsResolved = c.Status == "Resolved"
            }).ToList();

            // Build stats from health response
            Stats = new MindStats
            {
                TotalMemories = HealthScore?.TotalMemories ?? 0,
                ActiveMemories = HealthScore?.ActiveMemories ?? 0,
                ArchivedMemories = HealthScore?.ArchivedMemories ?? 0,
                AvgConfidence = HealthScore?.AvgConfidence ?? 0,
                HallucinationCount = Hallucinations.Count,
                ContradictionCount = Contradictions.Count
            };
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load mind health data: {ex.Message}";
        }
    }

    public sealed class AlertsResponse { public IReadOnlyList<AlertItem>? Items { get; init; } }
    public sealed class TrendResponse { public IReadOnlyList<DailyTrendPoint>? Points { get; init; } }
    public sealed class DriftResponse { public IReadOnlyList<DriftCalibrationPoint>? Points { get; init; } }

    // API response types that match the actual API structure
    public sealed class HallucinationsListResponse { public IReadOnlyList<HallucinationEventDto>? Items { get; init; } }
    public sealed class ContradictionsListResponse
    {
        public int Count { get; init; }
        public IReadOnlyList<ContradictionEventDto>? Contradictions { get; init; }
    }

    public sealed class HallucinationEventDto
    {
        public Guid Id { get; init; }
        public Guid? MemoryId { get; init; }
        public string Type { get; init; } = "";
        public float Severity { get; init; }
        public string Description { get; init; } = "";
        public string? Evidence { get; init; }
        public string? GroundTruth { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
        public string Source { get; init; } = "";
        public bool Resolved { get; init; }
        public string? Resolution { get; init; }
    }

    public sealed class ContradictionEventDto
    {
        public Guid Id { get; init; }
        public Guid MemoryA { get; init; }
        public Guid MemoryB { get; init; }
        public string Type { get; init; } = "";
        public float Severity { get; init; }
        public string Description { get; init; } = "";
        public float SimilarityScore { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
        public string Status { get; init; } = "";
    }

    public sealed class MindHealthScore
    {
        public double OverallScore { get; init; }
        public double CoherenceScore { get; init; }
        public double ConsistencyScore { get; init; }
        public double ReliabilityScore { get; init; }
        public double FreshnessScore { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset CalculatedAt { get; init; }
        public long TotalMemories { get; init; }
        public long ActiveMemories { get; init; }
        public long ArchivedMemories { get; init; }
        public double AvgConfidence { get; init; }
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
