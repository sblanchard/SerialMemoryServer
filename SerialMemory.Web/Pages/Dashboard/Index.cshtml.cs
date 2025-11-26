using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class IndexModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppConfig _config;

    public IndexModel(IHttpClientFactory httpClientFactory, AppConfig config)
    {
        _httpClientFactory = httpClientFactory;
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
        var token = User.FindFirst("token")?.Value ?? User.FindFirst("api_key")?.Value;

        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        ApiKey = token;

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Get usage info
            var usageResponse = await client.GetAsync("/tenant/usage");
            if (usageResponse.IsSuccessStatusCode)
            {
                var usage = await usageResponse.Content.ReadFromJsonAsync<UsageResult>();
                if (usage != null)
                {
                    CreditsUsed = usage.CreditsUsed;
                    CreditsAllocated = usage.CreditsAllocated;
                    CycleEnd = usage.CycleEnd;
                    MemoryCount = usage.MemoryCount;
                }
            }

            // Get plan info
            var planResponse = await client.GetAsync("/tenant/plan");
            if (planResponse.IsSuccessStatusCode)
            {
                var plan = await planResponse.Content.ReadFromJsonAsync<PlanResult>();
                if (plan != null)
                {
                    PlanName = plan.DisplayName ?? plan.PlanName ?? "Free";
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
        public decimal CreditsAllocated { get; init; }
        public DateTimeOffset? CycleEnd { get; init; }
        public int MemoryCount { get; init; }
    }

    private sealed class PlanResult
    {
        public string? PlanName { get; init; }
        public string? DisplayName { get; init; }
    }
}
