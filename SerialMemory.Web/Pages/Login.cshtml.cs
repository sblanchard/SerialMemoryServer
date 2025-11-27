using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages;

public sealed class LoginModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public LoginModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [BindProperty]
    public string ApiKey { get; set; } = "";

    [BindProperty]
    public bool RememberMe { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ReturnUrl { get; set; }

    public void OnGet([FromQuery] string? returnUrl)
    {
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync([FromQuery] string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ErrorMessage = "API Key is required.";
            return Page();
        }

        try
        {
            // Use Dashboard API for authentication (it has the /me endpoint)
            var client = _httpClientFactory.CreateClient("DashboardApi");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            var response = await client.GetAsync("/me");

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                ErrorMessage = "Invalid API key. Please check and try again.";
                return Page();
            }

            var result = await response.Content.ReadFromJsonAsync<MeResult>();

            if (result == null)
            {
                ErrorMessage = "Unexpected response from server.";
                return Page();
            }

            // Sign in with claims
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, result.UserId),
                new(ClaimTypes.Email, result.Email ?? result.UserId),
                new(ClaimTypes.Name, result.TenantName ?? result.UserId),
                new("tenant_id", result.TenantId),
                new("api_key", ApiKey),
                new("role", result.Role)
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = RememberMe,
                ExpiresUtc = RememberMe
                    ? DateTimeOffset.UtcNow.AddDays(7)
                    : DateTimeOffset.UtcNow.AddHours(8)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            // Store API key in a secure cookie for API calls from pages
            Response.Cookies.Append("auth_token", ApiKey, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires = RememberMe ? DateTimeOffset.UtcNow.AddDays(7) : DateTimeOffset.UtcNow.AddHours(8)
            });

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToPage("/Dashboard/Index");
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to the server: {ex.Message}";
            return Page();
        }
    }

    private sealed class MeResult
    {
        public string TenantId { get; init; } = "";
        public string? TenantName { get; init; }
        public string UserId { get; init; } = "";
        public string? Email { get; init; }
        public string Role { get; init; } = "";
    }
}
