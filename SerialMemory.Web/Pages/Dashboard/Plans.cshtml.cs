using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;
using System.Text.Json;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class PlansModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public PlansModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public IReadOnlyList<PlanInfo> AvailablePlans { get; set; } = [];
    public string? CurrentPlan { get; set; } = "free";
    public string? CurrentBillingInterval { get; set; } = "monthly";
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    [BindProperty]
    public string? SelectedBillingInterval { get; set; } = "monthly";

    public async Task OnGetAsync([FromQuery] bool? success, [FromQuery] bool? cancelled)
    {
        if (success == true)
        {
            SuccessMessage = "Plan changed successfully!";
        }
        else if (cancelled == true)
        {
            ErrorMessage = "Plan change was cancelled.";
        }

        SelectedBillingInterval = Request.Query["interval"].FirstOrDefault() ?? "monthly";
        await LoadPlansAsync();
    }

    public async Task<IActionResult> OnPostSelectPlanAsync(string plan, string interval)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var request = new
            {
                TargetPlan = plan,
                BillingInterval = interval,
                SuccessUrl = $"{Request.Scheme}://{Request.Host}/dashboard/plans?success=true",
                CancelUrl = $"{Request.Scheme}://{Request.Host}/dashboard/plans?cancelled=true"
            };

            var response = await client.PostAsJsonAsync("/api/billing/plan/change", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<PlanChangeResponse>();
                if (!string.IsNullOrEmpty(result?.CheckoutUrl))
                {
                    return Redirect(result.CheckoutUrl);
                }
                SuccessMessage = result?.Message ?? "Plan change initiated";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                try
                {
                    var error = JsonSerializer.Deserialize<ErrorResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    ErrorMessage = error?.Message ?? $"Failed to change plan (HTTP {(int)response.StatusCode})";
                }
                catch
                {
                    ErrorMessage = $"Failed to change plan (HTTP {(int)response.StatusCode})";
                }
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to the server: {ex.Message}";
        }

        await LoadPlansAsync();
        return Page();
    }

    private async Task LoadPlansAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.GetAsync("/api/billing/plans");

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<PlansResponse>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result?.Plans != null)
                {
                    AvailablePlans = result.Plans;
                    CurrentPlan = result.CurrentPlan ?? "free";
                }
            }
            else
            {
                // Fallback to default plans
                AvailablePlans = GetDefaultPlans();
            }
        }
        catch (HttpRequestException)
        {
            // Fallback to default plans
            AvailablePlans = GetDefaultPlans();
        }
    }

    private static List<PlanInfo> GetDefaultPlans() =>
    [
        new PlanInfo
        {
            Id = Guid.Empty,
            Name = "free",
            DisplayName = "Free",
            CreditsPerMonth = 1000,
            MaxMemories = 1000,
            RateLimitPerMinute = 30,
            Features = new PlanFeatures { ApiAccess = true },
            Pricing = new PlanPricing { Monthly = 0, Quarterly = 0, Annual = 0 },
            IsCurrent = true
        },
        new PlanInfo
        {
            Id = Guid.Empty,
            Name = "pro",
            DisplayName = "Pro",
            CreditsPerMonth = 10000,
            MaxMemories = 50000,
            RateLimitPerMinute = 120,
            Features = new PlanFeatures { ApiAccess = true, AdvancedAnalytics = true },
            Pricing = new PlanPricing { Monthly = 19, Quarterly = 51.30m, Annual = 182.40m }  // €19/month
        },
        new PlanInfo
        {
            Id = Guid.Empty,
            Name = "pro_plus",
            DisplayName = "Pro Plus",
            CreditsPerMonth = 20000,
            MaxMemories = 200000,
            RateLimitPerMinute = 200,
            Features = new PlanFeatures
            {
                ApiAccess = true,
                AdvancedAnalytics = true,
                PrioritySupport = true,
                TeamCollaboration = true,
                SharedWorkspace = true,
                CreditPooling = true,
                SeatsIncluded = 5
            },
            Pricing = new PlanPricing { Monthly = 79, Quarterly = 213.30m, Annual = 758.40m }  // €79/month
        },
        new PlanInfo
        {
            Id = Guid.Empty,
            Name = "enterprise",
            DisplayName = "Enterprise",
            CreditsPerMonth = 100000,
            MaxMemories = null,
            RateLimitPerMinute = 600,
            Features = new PlanFeatures { ApiAccess = true, AdvancedAnalytics = true, PrioritySupport = true, CustomIntegrations = true },
            Pricing = new PlanPricing { Monthly = 299, Quarterly = 807.30m, Annual = 2870.40m }  // €299/month
        }
    ];

    public sealed class PlansResponse
    {
        public List<PlanInfo>? Plans { get; set; }
        public string? CurrentPlan { get; set; }
    }

    public sealed class PlanInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public decimal CreditsPerMonth { get; set; }
        public int? MaxMemories { get; set; }
        public int? MaxEntities { get; set; }
        public int RateLimitPerMinute { get; set; }
        public PlanFeatures Features { get; set; } = new();
        public PlanPricing Pricing { get; set; } = new();
        public bool IsCurrent { get; set; }
    }

    public sealed class PlanFeatures
    {
        public bool PrioritySupport { get; set; }
        public bool CustomIntegrations { get; set; }
        public bool AdvancedAnalytics { get; set; }
        public bool ApiAccess { get; set; }
        public bool TeamCollaboration { get; set; }
        public bool SharedWorkspace { get; set; }
        public bool CreditPooling { get; set; }
        public int SeatsIncluded { get; set; }
    }

    public sealed class PlanPricing
    {
        public decimal Monthly { get; set; }
        public decimal Quarterly { get; set; }
        public decimal Annual { get; set; }
    }

    public sealed class PlanChangeResponse
    {
        public string? Message { get; set; }
        public string? CheckoutUrl { get; set; }
        public string? SessionId { get; set; }
    }

    public sealed class ErrorResponse
    {
        public string? Error { get; set; }
        public string? Message { get; set; }
    }
}
