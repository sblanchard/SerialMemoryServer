using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class BillingModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _config;

    public BillingModel(ApiClientService apiClient, AppConfig config)
    {
        _apiClient = apiClient;
        _config = config;
    }

    public string? CurrentPlan { get; set; } = "Free";
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? PaymentMethodLast4 { get; set; }
    public string? PaymentMethodBrand { get; set; }
    public DateTimeOffset? CurrentPeriodStart { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public bool WillCancelAtPeriodEnd { get; set; }
    public decimal CreditsIncluded { get; set; } = 100;

    public IReadOnlyList<PaymentRecord> PaymentHistory { get; set; } = [];

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsSampleData { get; set; }
    public bool IsSelfHosted { get; set; }

    public async Task OnGetAsync([FromQuery] bool? success, [FromQuery] bool? canceled)
    {
        if (success == true)
        {
            SuccessMessage = "Your subscription has been updated successfully!";
        }
        else if (canceled == true)
        {
            ErrorMessage = "Checkout was cancelled.";
        }

        await LoadBillingDataAsync();
    }

    public async Task<IActionResult> OnPostCheckoutAsync(string plan)
    {
        try
        {
            var client = _apiClient.CreateClient();
            var request = new
            {
                PlanName = plan,
                SuccessUrl = $"{Request.Scheme}://{Request.Host}/dashboard/billing?success=true",
                CancelUrl = $"{Request.Scheme}://{Request.Host}/dashboard/billing?canceled=true"
            };

            var response = await client.PostAsJsonAsync("/billing/checkout", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CheckoutResponse>();
                if (!string.IsNullOrEmpty(result?.CheckoutUrl))
                {
                    return Redirect(result.CheckoutUrl);
                }
            }

            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            ErrorMessage = error?.Message ?? "Failed to create checkout session";
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server";
        }

        await LoadBillingDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPortalAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
            var request = new
            {
                ReturnUrl = $"{Request.Scheme}://{Request.Host}/dashboard/billing"
            };

            var response = await client.PostAsJsonAsync("/billing/portal", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<PortalResponse>();
                if (!string.IsNullOrEmpty(result?.PortalUrl))
                {
                    return Redirect(result.PortalUrl);
                }
            }

            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            ErrorMessage = error?.Message ?? "Failed to open billing portal";
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server";
        }

        await LoadBillingDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsync("/billing/cancel", null);

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = "Your subscription has been scheduled for cancellation at the end of the billing period.";
            }
            else
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                ErrorMessage = error?.Message ?? "Failed to cancel subscription";
            }
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server";
        }

        await LoadBillingDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResumeAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsync("/billing/resume", null);

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = "Your subscription has been resumed.";
            }
            else
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                ErrorMessage = error?.Message ?? "Failed to resume subscription";
            }
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server";
        }

        await LoadBillingDataAsync();
        return Page();
    }

    private async Task LoadBillingDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();

            // Get billing summary
            var billingResponse = await client.GetAsync("/billing");
            if (billingResponse.IsSuccessStatusCode)
            {
                var billing = await billingResponse.Content.ReadFromJsonAsync<BillingSummary>();
                if (billing != null)
                {
                    CurrentPlan = billing.CurrentPlan;
                    StripeCustomerId = billing.StripeCustomerId;
                    StripeSubscriptionId = billing.StripeSubscriptionId;
                    PaymentMethodLast4 = billing.PaymentMethodLast4;
                    PaymentMethodBrand = billing.PaymentMethodBrand;
                    CurrentPeriodStart = billing.CurrentPeriodStart;
                    CurrentPeriodEnd = billing.CurrentPeriodEnd;
                    WillCancelAtPeriodEnd = billing.WillCancelAtPeriodEnd;
                    PaymentHistory = billing.RecentPayments ?? [];
                }
            }

            // Get plan details for credits
            var planResponse = await client.GetAsync("/tenant/plan");
            if (planResponse.IsSuccessStatusCode)
            {
                var plan = await planResponse.Content.ReadFromJsonAsync<PlanDetails>();
                if (plan != null)
                {
                    CreditsIncluded = plan.CreditsPerCycle;
                }
            }

            // If API returned empty data, show sample data
            if (string.IsNullOrEmpty(CurrentPlan) || CurrentPlan == "Free")
            {
                if (!CurrentPeriodStart.HasValue && !CurrentPeriodEnd.HasValue)
                {
                    LoadSampleData();
                }
            }
        }
        catch (HttpRequestException)
        {
            // API unavailable - use sample data
            LoadSampleData();
        }
    }

    private void LoadSampleData()
    {
        IsSampleData = true;
        CurrentPlan = "Free";
        CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-15);
        CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(15);
        CreditsIncluded = 100;
        PaymentHistory = [];
    }

    public sealed class PaymentRecord
    {
        public DateTimeOffset CreatedAt { get; init; }
        public string? PlanName { get; init; }
        public int AmountCents { get; init; }
        public string Status { get; init; } = "";
        public string? InvoicePdfUrl { get; init; }
    }

    private sealed class BillingSummary
    {
        public string? CurrentPlan { get; init; }
        public string? StripeCustomerId { get; init; }
        public string? StripeSubscriptionId { get; init; }
        public string? PaymentMethodLast4 { get; init; }
        public string? PaymentMethodBrand { get; init; }
        public DateTimeOffset? CurrentPeriodStart { get; init; }
        public DateTimeOffset? CurrentPeriodEnd { get; init; }
        public bool WillCancelAtPeriodEnd { get; init; }
        public IReadOnlyList<PaymentRecord>? RecentPayments { get; init; }
    }

    private sealed class PlanDetails
    {
        public decimal CreditsPerCycle { get; init; }
    }

    private sealed class CheckoutResponse
    {
        public string? SessionId { get; init; }
        public string? CheckoutUrl { get; init; }
    }

    private sealed class PortalResponse
    {
        public string? PortalUrl { get; init; }
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
