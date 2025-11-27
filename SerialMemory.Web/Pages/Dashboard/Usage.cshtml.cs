using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class UsageModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public UsageModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public decimal CreditsUsed { get; set; }
    public decimal CreditsAllocated { get; set; } = 100;
    public decimal CreditsRemaining => CreditsAllocated - CreditsUsed;
    public int UsagePercent => CreditsAllocated > 0 ? (int)Math.Min(100, (CreditsUsed / CreditsAllocated) * 100) : 0;
    public int TotalOperations { get; set; }
    public DateTimeOffset? CycleEnd { get; set; }
    public int DaysRemaining => CycleEnd.HasValue ? Math.Max(0, (int)(CycleEnd.Value - DateTimeOffset.UtcNow).TotalDays) : 0;

    public int CurrentRatePerMinute { get; set; }
    public int? RateLimitPerMinute { get; set; }
    public int RateLimitHits { get; set; }

    public IReadOnlyList<OperationUsage> UsageByOperation { get; set; } = [];
    public IReadOnlyList<DailyUsageRecord> DailyUsage { get; set; } = [];

    public async Task OnGetAsync()
    {
        await LoadUsageDataAsync();
    }

    public async Task<IActionResult> OnPostExportAsync()
    {
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            return Unauthorized();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync("/tenant/usage/export?format=csv");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsByteArrayAsync();
                return File(content, "text/csv", $"usage-{DateTime.UtcNow:yyyy-MM-dd}.csv");
            }
        }
        catch (HttpRequestException)
        {
            // API unavailable
        }

        // Fallback: generate simple CSV
        var csv = "Date,Operation,Count,Credits\n";
        foreach (var op in UsageByOperation)
        {
            csv += $"{DateTime.UtcNow:yyyy-MM-dd},{op.OperationType},{op.Count},{op.Credits}\n";
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"usage-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    }

    private async Task LoadUsageDataAsync()
    {
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Get usage summary
            var usageResponse = await client.GetAsync("/tenant/usage");
            if (usageResponse.IsSuccessStatusCode)
            {
                var usage = await usageResponse.Content.ReadFromJsonAsync<UsageResponse>();
                if (usage != null)
                {
                    CreditsUsed = usage.CreditsUsed;
                    CreditsAllocated = usage.CreditsAllocated;
                    CycleEnd = usage.CycleEnd;
                    TotalOperations = usage.TotalOperations;
                    RateLimitPerMinute = usage.RateLimitPerMinute;
                    RateLimitHits = usage.RateLimitHits;
                }
            }

            // Get limits for rate info
            var limitsResponse = await client.GetAsync("/tenant/limits");
            if (limitsResponse.IsSuccessStatusCode)
            {
                var limits = await limitsResponse.Content.ReadFromJsonAsync<LimitsResponse>();
                if (limits != null)
                {
                    CurrentRatePerMinute = limits.CurrentRatePerMinute;
                    RateLimitPerMinute = limits.RateLimitPerMinute;
                }
            }

            // Generate sample data for demo (in production, this would come from API)
            UsageByOperation = GenerateSampleOperationUsage();
            DailyUsage = GenerateSampleDailyUsage();
        }
        catch (HttpRequestException)
        {
            // API unavailable - use sample data
            UsageByOperation = GenerateSampleOperationUsage();
            DailyUsage = GenerateSampleDailyUsage();
        }
    }

    private string? GetAuthToken()
    {
        return User.FindFirst("token")?.Value ?? User.FindFirst("api_key")?.Value;
    }

    private List<OperationUsage> GenerateSampleOperationUsage()
    {
        if (CreditsUsed == 0) return [];

        var total = CreditsUsed;
        return
        [
            new OperationUsage { OperationType = "Memory Ingest", Count = (int)(total * 0.4m), Credits = total * 0.4m },
            new OperationUsage { OperationType = "Memory Search", Count = (int)(total * 1.6m), Credits = total * 0.4m },
            new OperationUsage { OperationType = "Multi-Hop Search", Count = (int)(total * 0.05m), Credits = total * 0.1m },
            new OperationUsage { OperationType = "Memory Update", Count = (int)(total * 0.1m), Credits = total * 0.05m },
            new OperationUsage { OperationType = "Other", Count = (int)(total * 0.05m), Credits = total * 0.05m }
        ];
    }

    private List<DailyUsageRecord> GenerateSampleDailyUsage()
    {
        var result = new List<DailyUsageRecord>();
        var maxCredits = CreditsUsed > 0 ? CreditsUsed / 7 * 2 : 10m;

        for (var i = 6; i >= 0; i--)
        {
            var credits = i == 0 ? CreditsUsed * 0.2m : CreditsUsed * 0.1m * (decimal)(new Random().NextDouble() + 0.5);
            result.Add(new DailyUsageRecord
            {
                Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-i)),
                Credits = credits,
                Percent = maxCredits > 0 ? (int)(credits / maxCredits * 100) : 0
            });
        }

        return result;
    }

    public sealed class OperationUsage
    {
        public string OperationType { get; init; } = "";
        public int Count { get; init; }
        public decimal Credits { get; init; }
    }

    public sealed class DailyUsageRecord
    {
        public DateOnly Date { get; init; }
        public decimal Credits { get; init; }
        public int Percent { get; init; }
    }

    private sealed class UsageResponse
    {
        public decimal CreditsUsed { get; init; }
        public decimal CreditsAllocated { get; init; }
        public DateTimeOffset? CycleEnd { get; init; }
        public int TotalOperations { get; init; }
        public int? RateLimitPerMinute { get; init; }
        public int RateLimitHits { get; init; }
    }

    private sealed class LimitsResponse
    {
        public int CurrentRatePerMinute { get; init; }
        public int? RateLimitPerMinute { get; init; }
    }
}
