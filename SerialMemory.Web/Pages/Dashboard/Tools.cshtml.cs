using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ToolsModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public ToolsModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public int Days { get; set; } = 7;
    public ToolStats? Stats { get; set; }
    public IReadOnlyList<TopTool> TopTools { get; set; } = [];
    public IReadOnlyList<CategoryStats> Categories { get; set; } = [];
    public IReadOnlyList<RecentCall> RecentCalls { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(int? days)
    {
        Days = Math.Clamp(days ?? 7, 1, 365);
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var statsTask = client.GetFromJsonAsync<ToolStats>($"/api/tools/stats?days={Days}");
            var topTask = client.GetFromJsonAsync<TopToolsResponse>($"/api/tools/top?days={Days}&limit=10");
            var categoriesTask = client.GetFromJsonAsync<CategoriesResponse>($"/api/tools/categories?days={Days}");
            var recentTask = client.GetFromJsonAsync<RecentCallsResponse>("/api/tools/recent?limit=50");

            await Task.WhenAll(statsTask, topTask, categoriesTask, recentTask);

            Stats = await statsTask;
            TopTools = (await topTask)?.Tools ?? [];
            Categories = (await categoriesTask)?.Categories ?? [];
            RecentCalls = (await recentTask)?.Events ?? [];
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load tool analytics: {ex.Message}";
        }
    }

    public sealed class ToolStats
    {
        [JsonPropertyName("total_calls")] public long TotalCalls { get; init; }
        [JsonPropertyName("unique_tools")] public int UniqueTools { get; init; }
        [JsonPropertyName("avg_latency_ms")] public decimal? AvgLatencyMs { get; init; }
        [JsonPropertyName("error_count")] public long ErrorCount { get; init; }
        [JsonPropertyName("error_rate_pct")] public decimal ErrorRatePct { get; init; }
        [JsonPropertyName("calls_today")] public long CallsToday { get; init; }
        [JsonPropertyName("calls_7d")] public long Calls7d { get; init; }
        [JsonPropertyName("calls_30d")] public long Calls30d { get; init; }
    }

    public sealed class TopToolsResponse { public IReadOnlyList<TopTool>? Tools { get; init; } }
    public sealed class CategoriesResponse { public IReadOnlyList<CategoryStats>? Categories { get; init; } }
    public sealed class RecentCallsResponse { public IReadOnlyList<RecentCall>? Events { get; init; } }

    public sealed class TopTool
    {
        [JsonPropertyName("tool_name")] public string ToolName { get; init; } = "";
        public string Category { get; init; } = "";
        public long Calls { get; init; }
        [JsonPropertyName("avg_latency_ms")] public decimal? AvgLatencyMs { get; init; }
        public long Errors { get; init; }
        [JsonPropertyName("last_used_at")] public DateTimeOffset? LastUsedAt { get; init; }
    }

    public sealed class CategoryStats
    {
        public string Category { get; init; } = "";
        public long Calls { get; init; }
        [JsonPropertyName("tool_count")] public int ToolCount { get; init; }
    }

    public sealed class RecentCall
    {
        public Guid Id { get; init; }
        [JsonPropertyName("tool_name")] public string ToolName { get; init; } = "";
        [JsonPropertyName("credits_used")] public decimal CreditsUsed { get; init; }
        public bool Success { get; init; }
        [JsonPropertyName("latency_ms")] public int? LatencyMs { get; init; }
        [JsonPropertyName("error_message")] public string? ErrorMessage { get; init; }
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    }
}
