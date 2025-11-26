using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class BillingModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppConfig _config;

    public BillingModel(IHttpClientFactory httpClientFactory, AppConfig config)
    {
        _httpClientFactory = httpClientFactory;
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
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            ErrorMessage = "Authentication required";
            await LoadBillingDataAsync();
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            ErrorMessage = "Authentication required";
            await LoadBillingDataAsync();
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            ErrorMessage = "Authentication required";
            await LoadBillingDataAsync();
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            ErrorMessage = "Authentication required";
            await LoadBillingDataAsync();
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        }
        catch (HttpRequestException)
        {
            // API unavailable
        }
    }

    private string? GetAuthToken()
    {
        return User.FindFirst("token")?.Value ?? User.FindFirst("api_key")?.Value;
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
