using System.Net.Http.Json;
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

    [BindProperty]
    public string Password { get; set; } = "";

    public string? ErrorMessage { get; set; }

    public string? Plan { get; set; }

    public void OnGet([FromQuery] string? plan)
    {
        Plan = plan;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(OrganizationName) ||
            string.IsNullOrWhiteSpace(Email) ||
            string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "All fields are required.";
            return Page();
        }

        if (Password.Length < 8)
        {
            ErrorMessage = "Password must be at least 8 characters.";
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");

            var signupRequest = new
            {
                OrganizationName,
                Email,
                Password,
                Plan = Plan ?? "free"
            };

            var response = await client.PostAsJsonAsync("/signup", signupRequest);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                ErrorMessage = error?.Message ?? "Failed to create account. Please try again.";
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
                new("tenant_id", result.TenantId),
                new("api_key", result.ApiKey),
                new("role", "owner")
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
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server. Please try again later.";
            return Page();
        }
    }

    private sealed class SignupResult
    {
        public string TenantId { get; init; } = "";
        public string UserId { get; init; } = "";
        public string ApiKey { get; init; } = "";
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
