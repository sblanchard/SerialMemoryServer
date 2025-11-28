using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ConfidenceModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public ConfidenceModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public IReadOnlyList<ConfidenceHistoryItem> History { get; set; } = [];
    public IReadOnlyList<DisagreementTrendItem> Trends { get; set; } = [];
    public IReadOnlyList<ReasoningRunItem> RecentRuns { get; set; } = [];
    public float CurrentConfidence { get; set; }
    public int TotalRuns { get; set; }
    public float AvgDisagreement { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsSampleData { get; set; }

    public async Task OnGetAsync()
    {
        await LoadConfidenceDataAsync();
    }

    public async Task<IActionResult> OnPostRunReasoningAsync(string? project, int? maxDurationMs)
    {
        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsJsonAsync("/api/confidence/reason", new
            {
                project,
                maxDurationMs = maxDurationMs ?? 30000
            });

            if (response.IsSuccessStatusCode)
            {
                await LoadConfidenceDataAsync();
            }
            else
            {
                ErrorMessage = "Failed to run reasoning analysis";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API connection failed: {ex.Message}";
        }

        return Page();
    }

    private async Task LoadConfidenceDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();

            // Load history
            var historyResponse = await client.GetAsync("/api/confidence/history?limit=20");
            if (historyResponse.IsSuccessStatusCode)
            {
                var result = await historyResponse.Content.ReadFromJsonAsync<HistoryApiResponse>();
                History = result?.Entries?.Select(h => new ConfidenceHistoryItem
                {
                    RunId = h.RunId,
                    OverallConfidence = h.OverallConfidence,
                    ModelsUsed = h.ModelsUsed,
                    SuccessfulModels = h.SuccessfulModels,
                    DisagreementCount = h.DisagreementCount,
                    AvgDisagreement = h.AvgDisagreement,
                    CreatedAt = h.CreatedAt
                }).ToList() ?? [];

                if (History.Count > 0)
                {
                    CurrentConfidence = History[0].OverallConfidence;
                    IsSampleData = false;
                }
            }

            // Load trends
            var trendsResponse = await client.GetAsync("/api/confidence/trends?days=7");
            if (trendsResponse.IsSuccessStatusCode)
            {
                var result = await trendsResponse.Content.ReadFromJsonAsync<TrendsApiResponse>();
                Trends = result?.Trends?.Select(t => new DisagreementTrendItem
                {
                    ModelPair = t.ModelPair,
                    AvgDisagreement = t.AvgDisagreement,
                    MaxDisagreement = t.MaxDisagreement,
                    OccurrenceCount = t.OccurrenceCount
                }).ToList() ?? [];
            }

            // Load recent runs
            var runsResponse = await client.GetAsync("/api/confidence/runs?limit=10");
            if (runsResponse.IsSuccessStatusCode)
            {
                var result = await runsResponse.Content.ReadFromJsonAsync<RunsApiResponse>();
                RecentRuns = result?.Runs?.Select(r => new ReasoningRunItem
                {
                    Id = r.Id,
                    InputHash = r.InputHash,
                    ModelsUsed = r.ModelsUsed,
                    SuccessfulModels = r.SuccessfulModels,
                    TotalDurationMs = r.TotalDurationMs,
                    OverallConfidence = r.OverallConfidence,
                    InsightCount = r.InsightCount,
                    DisagreementCount = r.DisagreementCount,
                    AvgDisagreementScore = r.AvgDisagreementScore,
                    ProjectFilter = r.ProjectFilter,
                    CreatedAt = r.CreatedAt
                }).ToList() ?? [];

                TotalRuns = RecentRuns.Count;
                AvgDisagreement = RecentRuns.Count > 0
                    ? RecentRuns.Where(r => r.AvgDisagreementScore.HasValue).Average(r => r.AvgDisagreementScore ?? 0)
                    : 0;
            }

            if (History.Count == 0)
            {
                LoadSampleData();
            }
        }
        catch (HttpRequestException)
        {
            LoadSampleData();
        }
    }

    private void LoadSampleData()
    {
        IsSampleData = true;
        CurrentConfidence = 0.87f;
        TotalRuns = 15;
        AvgDisagreement = 0.23f;

        History =
        [
            new() { RunId = Guid.NewGuid(), OverallConfidence = 0.87f, ModelsUsed = 5, SuccessfulModels = 5, DisagreementCount = 2, AvgDisagreement = 0.18f, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) },
            new() { RunId = Guid.NewGuid(), OverallConfidence = 0.82f, ModelsUsed = 5, SuccessfulModels = 4, DisagreementCount = 3, AvgDisagreement = 0.25f, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30) },
            new() { RunId = Guid.NewGuid(), OverallConfidence = 0.91f, ModelsUsed = 5, SuccessfulModels = 5, DisagreementCount = 1, AvgDisagreement = 0.12f, CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) },
            new() { RunId = Guid.NewGuid(), OverallConfidence = 0.78f, ModelsUsed = 5, SuccessfulModels = 4, DisagreementCount = 4, AvgDisagreement = 0.35f, CreatedAt = DateTimeOffset.UtcNow.AddHours(-2) },
            new() { RunId = Guid.NewGuid(), OverallConfidence = 0.85f, ModelsUsed = 5, SuccessfulModels = 5, DisagreementCount = 2, AvgDisagreement = 0.20f, CreatedAt = DateTimeOffset.UtcNow.AddHours(-4) }
        ];

        Trends =
        [
            new() { ModelPair = "RiskDetector vs ContradictionDetector", AvgDisagreement = 0.32f, MaxDisagreement = 0.45f, OccurrenceCount = 8 },
            new() { ModelPair = "StructuralValidator vs OptimizationFinder", AvgDisagreement = 0.28f, MaxDisagreement = 0.38f, OccurrenceCount = 12 },
            new() { ModelPair = "RuleBasedEngine vs RiskDetector", AvgDisagreement = 0.15f, MaxDisagreement = 0.22f, OccurrenceCount = 15 }
        ];

        RecentRuns =
        [
            new() { Id = Guid.NewGuid(), InputHash = "a1b2c3d4", ModelsUsed = 5, SuccessfulModels = 5, TotalDurationMs = 1250, OverallConfidence = 0.87f, InsightCount = 12, DisagreementCount = 2, AvgDisagreementScore = 0.18f, ProjectFilter = "SerialMemory", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) },
            new() { Id = Guid.NewGuid(), InputHash = "e5f6g7h8", ModelsUsed = 5, SuccessfulModels = 4, TotalDurationMs = 980, OverallConfidence = 0.82f, InsightCount = 8, DisagreementCount = 3, AvgDisagreementScore = 0.25f, ProjectFilter = null, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30) }
        ];
    }

    public sealed class ConfidenceHistoryItem
    {
        public Guid RunId { get; init; }
        public float OverallConfidence { get; init; }
        public int ModelsUsed { get; init; }
        public int SuccessfulModels { get; init; }
        public int DisagreementCount { get; init; }
        public float? AvgDisagreement { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    public sealed class DisagreementTrendItem
    {
        public string ModelPair { get; init; } = "";
        public float AvgDisagreement { get; init; }
        public float MaxDisagreement { get; init; }
        public int OccurrenceCount { get; init; }
    }

    public sealed class ReasoningRunItem
    {
        public Guid Id { get; init; }
        public string InputHash { get; init; } = "";
        public int ModelsUsed { get; init; }
        public int SuccessfulModels { get; init; }
        public int TotalDurationMs { get; init; }
        public float OverallConfidence { get; init; }
        public int InsightCount { get; init; }
        public int DisagreementCount { get; init; }
        public float? AvgDisagreementScore { get; init; }
        public string? ProjectFilter { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class HistoryApiResponse
    {
        public IReadOnlyList<HistoryEntry>? Entries { get; init; }
    }

    private sealed class HistoryEntry
    {
        public Guid RunId { get; init; }
        public float OverallConfidence { get; init; }
        public int ModelsUsed { get; init; }
        public int SuccessfulModels { get; init; }
        public int DisagreementCount { get; init; }
        public float? AvgDisagreement { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class TrendsApiResponse
    {
        public IReadOnlyList<TrendEntry>? Trends { get; init; }
    }

    private sealed class TrendEntry
    {
        public string ModelPair { get; init; } = "";
        public float AvgDisagreement { get; init; }
        public float MaxDisagreement { get; init; }
        public int OccurrenceCount { get; init; }
    }

    private sealed class RunsApiResponse
    {
        public IReadOnlyList<RunEntry>? Runs { get; init; }
    }

    private sealed class RunEntry
    {
        public Guid Id { get; init; }
        public string InputHash { get; init; } = "";
        public int ModelsUsed { get; init; }
        public int SuccessfulModels { get; init; }
        public int TotalDurationMs { get; init; }
        public float OverallConfidence { get; init; }
        public int InsightCount { get; init; }
        public int DisagreementCount { get; init; }
        public float? AvgDisagreementScore { get; init; }
        public string? ProjectFilter { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
