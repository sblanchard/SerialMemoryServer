using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ReasoningModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _appConfig;

    public ReasoningModel(ApiClientService apiClient, AppConfig appConfig)
    {
        _apiClient = apiClient;
        _appConfig = appConfig;
    }

    public string ApiBaseUrl => _appConfig.ApiBaseUrl;

    // SignalR access token (from InternalTokenMiddleware)
    public string? SignalRToken => HttpContext.Items["InternalToken"] as string;

    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? MemoryId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Mode { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string SortBy { get; set; } = "severity";

    [BindProperty(SupportsGet = true)]
    public string? FilterType { get; set; }

    public IReadOnlyList<ReasoningInsight> Insights { get; set; } = [];
    public IReadOnlyList<string> Projects { get; set; } = [];
    public IReadOnlyList<RecentMemory> RecentMemories { get; set; } = [];
    public IReadOnlyList<ReasoningRun> ReasoningRuns { get; set; } = [];
    public bool IsLoading { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsSampleData { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedRunId { get; set; }

    public async Task OnGetAsync()
    {
        await LoadProjectsAsync();
        await LoadRecentMemoriesAsync();
        await LoadReasoningRunsAsync();

        if (!string.IsNullOrEmpty(Project) || MemoryId.HasValue)
        {
            await RunReasoningAsync();
        }
        else
        {
            LoadSampleInsights();
        }
    }

    private async Task LoadReasoningRunsAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.GetAsync("/api/reasoning/runs?limit=20");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ReasoningRunsResponse>();
                ReasoningRuns = result?.Runs ?? [];
            }
        }
        catch (HttpRequestException)
        {
            // Runs list not critical, just leave empty
        }
    }

    public async Task<IActionResult> OnPostRunReasoningAsync()
    {
        await LoadProjectsAsync();
        await LoadRecentMemoriesAsync();
        await RunReasoningAsync();
        return Page();
    }

    private async Task LoadProjectsAsync()
    {
        // For now, use sample projects - in production would query distinct projects from memories
        Projects = ["SerialMemory", "FlexPilot", "FlexHPSDR", "RebateX"];
    }

    private async Task LoadRecentMemoriesAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");

            var response = await client.GetAsync("/api/memories/recent?limit=10");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<List<RecentMemoryApiResponse>>();
                RecentMemories = result?.Select(m => new RecentMemory
                {
                    Id = m.Id,
                    Content = m.Content.Length > 100 ? m.Content[..100] + "..." : m.Content,
                    CreatedAt = m.CreatedAt
                }).ToList() ?? [];
            }
            else
            {
                RecentMemories = GenerateSampleMemories();
            }
        }
        catch (HttpRequestException)
        {
            RecentMemories = GenerateSampleMemories();
        }
    }

    private async Task RunReasoningAsync()
    {
        try
        {
            IsLoading = true;
            var client = _apiClient.CreateClient("Api");

            // Get recent traces to find the latest one with findings
            var tracesResponse = await client.GetAsync("/api/reasoning/traces?limit=10");

            if (tracesResponse.IsSuccessStatusCode)
            {
                var tracesResult = await tracesResponse.Content.ReadFromJsonAsync<TracesApiResponse>();

                if (tracesResult?.Items is { Count: > 0 })
                {
                    // Get the most recent trace with findings
                    var latestTrace = tracesResult.Items.FirstOrDefault(t => t.FindingsCount > 0);

                    if (latestTrace != null)
                    {
                        // Fetch full trace details with findings
                        var traceResponse = await client.GetAsync($"/api/reasoning/traces/{latestTrace.TraceId}");
                        if (traceResponse.IsSuccessStatusCode)
                        {
                            var traceDetail = await traceResponse.Content.ReadFromJsonAsync<TraceDetailResponse>();
                            if (traceDetail?.Findings is { Count: > 0 })
                            {
                                IsSampleData = false;
                                Insights = traceDetail.Findings.Select(f => new ReasoningInsight
                                {
                                    Id = f.Id,
                                    Type = f.Type,
                                    Category = GetCategoryFromType(f.Type),
                                    Title = f.Title,
                                    Description = f.Description,
                                    Severity = f.Severity,
                                    Confidence = f.Confidence,
                                    SourceModels = ["CodeAnalyzer"],
                                    Timestamp = traceDetail.StartedAt,
                                    FilePath = f.FilePath,
                                    LineNumber = f.LineNumber,
                                    CodeSnippet = f.CodeSnippet,
                                    Recommendation = f.Recommendation
                                }).ToList();

                                Insights = ApplyFiltersAndSort(Insights);
                                return;
                            }
                        }
                    }
                }
            }

            // No real data found, show sample
            LoadSampleInsights();
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to connect to API: {ex.Message}";
            LoadSampleInsights();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string GetCategoryFromType(string type) => type.ToUpper() switch
    {
        "SQL_INJECTION" => "risk",
        "MISSING_TRY_CATCH" => "risk",
        "EF_CORE_N_PLUS_ONE" => "optimization",
        _ => "risk"
    };

    private IReadOnlyList<ReasoningInsight> ApplyFiltersAndSort(IReadOnlyList<ReasoningInsight> insights)
    {
        var filtered = insights.AsEnumerable();

        // Filter by mode
        if (Mode != "all")
        {
            filtered = filtered.Where(i => i.Category.Equals(Mode, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by type
        if (!string.IsNullOrEmpty(FilterType))
        {
            filtered = filtered.Where(i => i.Type.Equals(FilterType, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        filtered = SortBy switch
        {
            "severity" => filtered.OrderByDescending(i => GetSeverityOrder(i.Severity)),
            "confidence" => filtered.OrderByDescending(i => i.Confidence),
            "recent" => filtered.OrderByDescending(i => i.Timestamp),
            _ => filtered.OrderByDescending(i => GetSeverityOrder(i.Severity))
        };

        return filtered.ToList();
    }

    private static int GetSeverityOrder(string severity) => severity.ToLower() switch
    {
        "critical" => 4,
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    private void LoadSampleInsights()
    {
        IsSampleData = true;
        Insights =
        [
            new ReasoningInsight
            {
                Id = Guid.NewGuid(),
                Type = "Risk",
                Category = "risk",
                Title = "Potential SQL Injection Vulnerability",
                Description = "User input in QueryBuilder.cs line 142 is not properly sanitized before being used in SQL query construction.",
                Severity = "Critical",
                Confidence = 0.92f,
                SourceModels = ["Structural", "Risk"],
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
                Trace = "Structural analysis detected string concatenation in SQL context → Risk model evaluated input validation → Cross-referenced with OWASP patterns"
            },
            new ReasoningInsight
            {
                Id = Guid.NewGuid(),
                Type = "Conflict",
                Category = "conflicts",
                Title = "Contradicting Configuration Values",
                Description = "MaxRetries is set to 3 in appsettings.json but the RetryPolicy class defaults to 5 retries.",
                Severity = "Medium",
                Confidence = 0.87f,
                SourceModels = ["Contradiction", "Structural"],
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-12),
                Trace = "Config scan found MaxRetries=3 → Code analysis found hardcoded default=5 → Contradiction model flagged inconsistency"
            },
            new ReasoningInsight
            {
                Id = Guid.NewGuid(),
                Type = "Optimization",
                Category = "optimization",
                Title = "N+1 Query Pattern Detected",
                Description = "GetAllUsersWithOrders() executes separate query for each user's orders. Consider using Include() or explicit join.",
                Severity = "High",
                Confidence = 0.95f,
                SourceModels = ["Optimization", "Structural"],
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-18),
                Trace = "Query pattern analysis detected loop with DB call → Performance model estimated O(n) queries → Optimization model suggested eager loading"
            },
            new ReasoningInsight
            {
                Id = Guid.NewGuid(),
                Type = "Risk",
                Category = "risk",
                Title = "Unhandled Exception in Background Worker",
                Description = "ProcessQueueItem() in BackgroundWorker.cs lacks try-catch, could crash the worker process.",
                Severity = "High",
                Confidence = 0.88f,
                SourceModels = ["Risk", "Structural"],
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-25),
                Trace = "Control flow analysis found unprotected async operation → Risk model evaluated crash potential → Severity elevated due to background context"
            },
            new ReasoningInsight
            {
                Id = Guid.NewGuid(),
                Type = "Conflict",
                Category = "conflicts",
                Title = "Duplicate Entity Definition",
                Description = "User entity defined in both Domain/User.cs and Models/UserDto.cs with different property sets.",
                Severity = "Low",
                Confidence = 0.78f,
                SourceModels = ["Structural", "Contradiction"],
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-32),
                Trace = "Entity scan found two User types → Structural model compared properties → Contradiction model flagged potential mapping issues"
            }
        ];
    }

    private static List<RecentMemory> GenerateSampleMemories() =>
    [
        new RecentMemory { Id = Guid.NewGuid(), Content = "Authentication module architecture decision", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) },
        new RecentMemory { Id = Guid.NewGuid(), Content = "Database schema for user preferences", CreatedAt = DateTimeOffset.UtcNow.AddHours(-3) },
        new RecentMemory { Id = Guid.NewGuid(), Content = "API rate limiting implementation", CreatedAt = DateTimeOffset.UtcNow.AddHours(-5) }
    ];

    public sealed class ReasoningInsight
    {
        public Guid Id { get; init; }
        public string Type { get; init; } = "";
        public string Category { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string Severity { get; init; } = "";
        public float Confidence { get; init; }
        public IReadOnlyList<string> SourceModels { get; init; } = [];
        public DateTimeOffset Timestamp { get; init; }
        public string? Trace { get; init; }
        public string? FilePath { get; init; }
        public int LineNumber { get; init; }
        public string? CodeSnippet { get; init; }
        public string? Recommendation { get; init; }
    }

    public sealed class RecentMemory
    {
        public Guid Id { get; init; }
        public string Content { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class ReasoningResponse
    {
        public IReadOnlyList<ReasoningInsight> Insights { get; init; } = [];
        public int TotalDurationMs { get; init; }
    }

    private sealed class RecentMemoryApiResponse
    {
        public Guid Id { get; init; }
        public string Content { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class TracesApiResponse
    {
        public IReadOnlyList<TraceItem> Items { get; init; } = [];
    }

    private sealed class TraceItem
    {
        public Guid TraceId { get; init; }
        public string Type { get; init; } = "";
        public DateTimeOffset TimestampUtc { get; init; }
        public int DurationMs { get; init; }
        public int FilesAnalyzed { get; init; }
        public int FindingsCount { get; init; }
        public int CriticalCount { get; init; }
        public int HighCount { get; init; }
        public int MediumCount { get; init; }
        public int LowCount { get; init; }
        public string Status { get; init; } = "";
    }

    private sealed class TraceDetailResponse
    {
        public Guid TraceId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public int? DurationMs { get; init; }
        public string Directory { get; init; } = "";
        public int FilesAnalyzed { get; init; }
        public IReadOnlyList<FindingItem> Findings { get; init; } = [];
    }

    private sealed class FindingItem
    {
        public Guid Id { get; init; }
        public string Type { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string FilePath { get; init; } = "";
        public int LineNumber { get; init; }
        public string? CodeSnippet { get; init; }
        public float Confidence { get; init; }
        public string? Recommendation { get; init; }
    }

    // Reasoning Runs DTOs
    public sealed class ReasoningRun
    {
        public Guid Id { get; init; }
        public string Scope { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public int? DurationMs { get; init; }
        public int StepCount { get; init; }
        public string Status { get; init; } = "";
    }

    private sealed class ReasoningRunsResponse
    {
        public IReadOnlyList<ReasoningRun> Runs { get; init; } = [];
    }
}
