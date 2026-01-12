using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages;

public sealed class SignupModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public SignupModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [BindProperty]
    public string OrganizationName { get; set; } = "";

    [BindProperty]
    public string Email { get; set; } = "";

    public string? ErrorMessage { get; set; }

    public string? Plan { get; set; }

    public void OnGet([FromQuery] string? plan)
    {
        Plan = plan;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(OrganizationName) ||
            string.IsNullOrWhiteSpace(Email))
        {
            ErrorMessage = "Organization name and email are required.";
            return Page();
        }

        try
        {
            // Use Dashboard API - that's where /signup endpoint lives
            var client = _httpClientFactory.CreateClient("DashboardApi");

            // API expects TenantName, not OrganizationName
            var signupRequest = new
            {
                TenantName = OrganizationName,
                Email,
                Plan = Plan ?? "free"
            };

            var response = await client.PostAsJsonAsync("/signup", signupRequest);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                // Try to parse as JSON, otherwise use as-is
                try
                {
                    var error = JsonSerializer.Deserialize<ErrorResponse>(errorContent);
                    ErrorMessage = error?.Message ?? error?.Error ?? errorContent;
                }
                catch
                {
                    ErrorMessage = errorContent.Length > 200 ? "Failed to create account. Please try again." : errorContent;
                }
                return Page();
            }

            var result = await response.Content.ReadFromJsonAsync<SignupResult>();

            if (result == null)
            {
                ErrorMessage = "Unexpected response from server.";
                return Page();
            }

            // Sign in the user
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, result.UserId),
                new(ClaimTypes.Email, Email),
                new(ClaimTypes.Name, OrganizationName),
                new("tenant_id", result.TenantId.ToString()),
                new("api_key", result.ApiKey?.Key ?? ""),
                new("role", result.Role),
                new("token", result.Token)
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            return RedirectToPage("/Dashboard/Index");
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to the server: {ex.Message}";
            return Page();
        }
    }

    private sealed class SignupResult
    {
        public Guid TenantId { get; init; }
        public string TenantName { get; init; } = "";
        public string Slug { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Role { get; init; } = "";
        public string Plan { get; init; } = "";
        public ApiKeyInfo? ApiKey { get; init; }
        public string Token { get; init; } = "";
    }

    private sealed class ApiKeyInfo
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
