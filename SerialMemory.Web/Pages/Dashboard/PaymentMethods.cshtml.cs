using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;
using System.Text.Json;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class PaymentMethodsModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public PaymentMethodsModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public IReadOnlyList<PaymentMethodInfo> PaymentMethods { get; set; } = [];
    public BillingAddressInfo? BillingAddress { get; set; }
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync([FromQuery] bool? success, [FromQuery] bool? canceled)
    {
        if (success == true)
        {
            SuccessMessage = "Payment method added successfully!";
        }
        else if (canceled == true)
        {
            ErrorMessage = "Adding payment method was cancelled.";
        }

        await LoadPaymentMethodsAsync();
    }

    public async Task<IActionResult> OnPostAddCardAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var request = new
            {
                SuccessUrl = $"{Request.Scheme}://{Request.Host}/dashboard/payment-methods?success=true",
                CancelUrl = $"{Request.Scheme}://{Request.Host}/dashboard/payment-methods?canceled=true"
            };

            var response = await client.PostAsJsonAsync("/api/billing/payment-methods/setup", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SetupResponse>();
                if (!string.IsNullOrEmpty(result?.SetupUrl))
                {
                    return Redirect(result.SetupUrl);
                }
                ErrorMessage = "No setup URL returned";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                try
                {
                    var error = JsonSerializer.Deserialize<ErrorResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    ErrorMessage = error?.Message ?? $"Failed to setup payment method (HTTP {(int)response.StatusCode})";
                }
                catch
                {
                    ErrorMessage = $"Failed to setup payment method (HTTP {(int)response.StatusCode})";
                }
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to the server: {ex.Message}";
        }

        await LoadPaymentMethodsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSetDefaultAsync(string paymentMethodId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsync($"/api/billing/payment-methods/{paymentMethodId}/default", null);

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = "Default payment method updated.";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                try
                {
                    var error = JsonSerializer.Deserialize<ErrorResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    ErrorMessage = error?.Message ?? "Failed to update default payment method";
                }
                catch
                {
                    ErrorMessage = "Failed to update default payment method";
                }
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to the server: {ex.Message}";
        }

        await LoadPaymentMethodsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string paymentMethodId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.DeleteAsync($"/api/billing/payment-methods/{paymentMethodId}");

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = "Payment method removed.";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                try
                {
                    var error = JsonSerializer.Deserialize<ErrorResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    ErrorMessage = error?.Message ?? "Failed to remove payment method";
                }
                catch
                {
                    ErrorMessage = "Failed to remove payment method";
                }
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to the server: {ex.Message}";
        }

        await LoadPaymentMethodsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateBillingAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var request = new
            {
                ReturnUrl = $"{Request.Scheme}://{Request.Host}/dashboard/payment-methods"
            };

            var response = await client.PostAsJsonAsync("/api/billing/portal", request);

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

        await LoadPaymentMethodsAsync();
        return Page();
    }

    private async Task LoadPaymentMethodsAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");

            // Get payment methods
            var pmResponse = await client.GetAsync("/api/billing/payment-methods");
            if (pmResponse.IsSuccessStatusCode)
            {
                var result = await pmResponse.Content.ReadFromJsonAsync<PaymentMethodsResponse>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result?.PaymentMethods != null)
                {
                    PaymentMethods = result.PaymentMethods;
                }
            }

            // Get billing address from customer
            var billingResponse = await client.GetAsync("/api/billing/customer");
            if (billingResponse.IsSuccessStatusCode)
            {
                var result = await billingResponse.Content.ReadFromJsonAsync<CustomerResponse>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                BillingAddress = result?.BillingAddress;
            }
        }
        catch (HttpRequestException)
        {
            // API unavailable - show empty state
            PaymentMethods = [];
        }
    }

    #region DTOs

    public sealed class PaymentMethodInfo
    {
        public string Id { get; set; } = "";
        public string? Brand { get; set; }
        public string? Last4 { get; set; }
        public int ExpMonth { get; set; }
        public int ExpYear { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed class BillingAddressInfo
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Line1 { get; set; }
        public string? Line2 { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? PostalCode { get; set; }
        public string? Country { get; set; }
    }

    private sealed class PaymentMethodsResponse
    {
        public List<PaymentMethodInfo>? PaymentMethods { get; set; }
    }

    private sealed class CustomerResponse
    {
        public string? CustomerId { get; set; }
        public string? Email { get; set; }
        public BillingAddressInfo? BillingAddress { get; set; }
    }

    private sealed class SetupResponse
    {
        public string? SetupUrl { get; set; }
        public string? SessionId { get; set; }
    }

    private sealed class PortalResponse
    {
        public string? PortalUrl { get; set; }
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; set; }
        public string? Message { get; set; }
    }

    #endregion
}
