using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class IndexModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _config;

    public IndexModel(ApiClientService apiClient, AppConfig config)
    {
        _apiClient = apiClient;
        _config = config;
    }

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
        else
        {
            ApiKey = "(view in API Keys page)";
        }

        try
        {
            var client = _apiClient.CreateClient();

            // Get usage info
            var usageResponse = await client.GetAsync("/api/usage/current");
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

            // Get stats for memory count
            var statsResponse = await client.GetAsync("/api/stats");
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
        catch (HttpRequestException)
        {
            // API unavailable - use defaults
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
}
