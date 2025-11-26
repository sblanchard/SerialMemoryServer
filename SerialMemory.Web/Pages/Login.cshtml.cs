using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    public string Email { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty]
    public string? ApiKey { get; set; }

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
        // Prefer API key login if provided
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return await LoginWithApiKeyAsync(ApiKey, returnUrl);
        }

        // Otherwise use email/password
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Email and password are required.";
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");

            var loginRequest = new { Email, Password };
            var response = await client.PostAsJsonAsync("/login", loginRequest);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                ErrorMessage = error?.Message ?? "Invalid email or password.";
                return Page();
            }

            var result = await response.Content.ReadFromJsonAsync<LoginResult>();

            if (result == null)
            {
                ErrorMessage = "Unexpected response from server.";
                return Page();
            }

            return await SignInUserAsync(result, returnUrl);
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server. Please try again later.";
            return Page();
        }
    }

    private async Task<IActionResult> LoginWithApiKeyAsync(string apiKey, string? returnUrl)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await client.GetAsync("/me");

            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = "Invalid API key.";
                return Page();
            }

            var result = await response.Content.ReadFromJsonAsync<MeResult>();

            if (result == null)
            {
                ErrorMessage = "Unexpected response from server.";
                return Page();
            }

            var loginResult = new LoginResult
            {
                TenantId = result.TenantId,
                UserId = result.UserId,
                Email = result.Email ?? "",
                Role = result.Role,
                Token = apiKey // Use API key as token
            };

            return await SignInUserAsync(loginResult, returnUrl);
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server. Please try again later.";
            return Page();
        }
    }

    private async Task<IActionResult> SignInUserAsync(LoginResult result, string? returnUrl)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.UserId),
            new(ClaimTypes.Email, result.Email),
            new(ClaimTypes.Name, result.Email),
            new("tenant_id", result.TenantId),
            new("token", result.Token),
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

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToPage("/Dashboard/Index");
    }

    private sealed class LoginResult
    {
        public string TenantId { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Email { get; init; } = "";
        public string Role { get; init; } = "";
        public string Token { get; init; } = "";
    }

    private sealed class MeResult
    {
        public string TenantId { get; init; } = "";
        public string UserId { get; init; } = "";
        public string? Email { get; init; }
        public string Role { get; init; } = "";
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
