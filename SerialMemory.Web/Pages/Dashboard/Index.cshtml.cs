using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
[IgnoreAntiforgeryToken]
public sealed class IndexModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _config;

    public IndexModel(ApiClientService apiClient, AppConfig config)
    {
        _apiClient = apiClient;
        _config = config;
    }

    // Server-side proxy for Live Test - Ingest (calls main API using internal token)
    public async Task<IActionResult> OnPostIngestAsync([FromBody] IngestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Content))
        {
            return new JsonResult(new { success = false, error = "Content is required" });
        }

        try
        {
            // Use ApiClientService which handles internal token authentication
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/memories", new { content = request.Content });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<object>();
                return new JsonResult(new { success = true, data = result });
            }

            var errorText = await response.Content.ReadAsStringAsync();
            return new JsonResult(new { success = false, error = $"API returned {(int)response.StatusCode}: {errorText}" });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    // Server-side proxy for Live Test - Search (calls main API using internal token)
    public async Task<IActionResult> OnPostSearchAsync([FromBody] SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            return new JsonResult(new { success = false, error = "Query is required" });
        }

        try
        {
            // Use ApiClientService which handles internal token authentication
            var client = _apiClient.CreateClient("Api");
            var response = await client.GetAsync($"/api/memories/search?query={Uri.EscapeDataString(request.Query)}&limit=10");

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<object>();
                return new JsonResult(new { success = true, data = result });
            }

            var errorText = await response.Content.ReadAsStringAsync();
            return new JsonResult(new { success = false, error = $"API returned {(int)response.StatusCode}: {errorText}" });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    public sealed record IngestRequest(string Content);
    public sealed record SearchRequest(string Query);

    public string PlanName { get; set; } = "Free";
    public decimal CreditsUsed { get; set; }
    public decimal CreditsAllocated { get; set; } = 100;
    public int UsagePercent => CreditsAllocated > 0 ? (int)Math.Min(100, (CreditsUsed / CreditsAllocated) * 100) : 0;
    public int MemoryCount { get; set; }
    public DateTimeOffset? CycleEnd { get; set; }
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl => _config.ApiBaseUrl;
    public bool ShowQuickstart { get; set; } = true;

    public async Task OnGetAsync()
    {
        // Try to get API key from claims (set during signup)
        var apiKeyClaim = User.FindFirst("api_key")?.Value;
        if (!string.IsNullOrEmpty(apiKeyClaim) && apiKeyClaim.StartsWith("sm_"))
        {
            ApiKey = apiKeyClaim;
        }

        try
        {
            // Dashboard API for api-keys
            var dashboardClient = _apiClient.CreateClient();
            // Main API for usage/stats
            var apiClient = _apiClient.CreateClient("Api");

            // If no API key in claims, try to fetch the first active API key
            if (string.IsNullOrEmpty(ApiKey) || !ApiKey.StartsWith("sm_"))
            {
                try
                {
                    var keysResponse = await dashboardClient.GetAsync("/api-keys");
                    if (keysResponse.IsSuccessStatusCode)
                    {
                        // API might return array directly or object with Keys property
                        var json = await keysResponse.Content.ReadAsStringAsync();
                        if (json.TrimStart().StartsWith('['))
                        {
                            // Array format
                            var keysList = System.Text.Json.JsonSerializer.Deserialize<List<ApiKeyInfo>>(json,
                                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            var activeKey = keysList?.FirstOrDefault(k => k.IsActive);
                            ApiKey = activeKey != null ? $"{activeKey.KeyPrefix}..." : "(create an API key first)";
                        }
                        else
                        {
                            // Object format with Keys property
                            var keys = System.Text.Json.JsonSerializer.Deserialize<ApiKeysResult>(json,
                                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            var activeKey = keys?.Keys?.FirstOrDefault(k => k.IsActive);
                            ApiKey = activeKey != null ? $"{activeKey.KeyPrefix}..." : "(create an API key first)";
                        }
                    }
                    else
                    {
                        ApiKey = "(view in API Keys page)";
                    }
                }
                catch
                {
                    ApiKey = "(view in API Keys page)";
                }
            }

            // Get usage info from Main API
            var usageResponse = await apiClient.GetAsync("/api/usage/current");
            if (usageResponse.IsSuccessStatusCode)
            {
                var usage = await usageResponse.Content.ReadFromJsonAsync<UsageResult>();
                if (usage != null)
                {
                    CreditsUsed = usage.CreditsUsed;
                    CreditsAllocated = usage.CreditsIncluded;
                    CycleEnd = !string.IsNullOrEmpty(usage.CycleEnd)
                        ? DateTimeOffset.Parse(usage.CycleEnd)
                        : null;
                }
            }

            // Get stats for memory count from Main API
            var statsResponse = await apiClient.GetAsync("/api/stats");
            if (statsResponse.IsSuccessStatusCode)
            {
                var stats = await statsResponse.Content.ReadFromJsonAsync<StatsResult>();
                if (stats != null)
                {
                    MemoryCount = stats.Memories;
                }
            }

            // Show quickstart for new users (low memory count)
            ShowQuickstart = MemoryCount < 10;
        }
        catch (Exception)
        {
            // API unavailable or parsing error - use defaults
        }
    }

    private sealed class UsageResult
    {
        public decimal CreditsUsed { get; init; }
        public decimal CreditsIncluded { get; init; }
        public int PercentUsed { get; init; }
        public int TotalOperations { get; init; }
        public string? CycleStart { get; init; }
        public string? CycleEnd { get; init; }
        public int DaysRemaining { get; init; }
    }

    private sealed class StatsResult
    {
        public int Memories { get; init; }
        public int Entities { get; init; }
        public int Relationships { get; init; }
    }

    private sealed class ApiKeysResult
    {
        public IReadOnlyList<ApiKeyInfo>? Keys { get; init; }
    }

    private sealed class ApiKeyInfo
    {
        public string? KeyPrefix { get; init; }
        public bool IsActive { get; init; }
    }
}
