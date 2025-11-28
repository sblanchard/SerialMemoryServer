using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages;

public sealed class LoginModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly InternalTokenService _internalTokenService;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        IHttpClientFactory httpClientFactory,
        InternalTokenService internalTokenService,
        ILogger<LoginModel> logger)
    {
        _httpClientFactory = httpClientFactory;
        _internalTokenService = internalTokenService;
        _logger = logger;
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
        // SECURITY: Always clear old auth tokens before processing new login
        // This prevents cross-tenant data leakage from stale cookies
        Response.Cookies.Delete("auth_token");
        HttpContext.Session.Remove("internal_token");

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

            // Use a 15-second timeout for the auth request
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await client.GetAsync("/me", cts.Token);

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

            _logger.LogInformation(
                "User {UserId} authenticated for tenant {TenantId}",
                result.UserId, result.TenantId);

            // Sign in with claims - DO NOT store api_key in claims
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, result.UserId),
                new(ClaimTypes.Email, result.Email ?? result.UserId),
                new(ClaimTypes.Name, result.TenantName ?? result.UserId),
                new("tenant_id", result.TenantId),
                new("role", result.Role)
                // NOTE: api_key intentionally NOT stored in cookie claims
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

            // Generate short-lived internal token for API calls
            var internalToken = _internalTokenService.GenerateToken(new InternalTokenClaims
            {
                TenantId = result.TenantId,
                UserId = result.UserId,
                WorkspaceId = "default",
                Role = result.Role,
                Email = result.Email
            });

            // Store internal token in session (server-side, not in cookie)
            HttpContext.Session.SetString("internal_token", internalToken);
            HttpContext.Session.SetString("internal_token_tenant", result.TenantId);
            HttpContext.Session.SetString("internal_token_user", result.UserId);

            _logger.LogDebug(
                "Internal token generated for user {UserId}, tenant {TenantId}",
                result.UserId, result.TenantId);

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToPage("/Dashboard/Index");
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "Authentication service timed out. Please try again.";
            return Page();
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to authentication service: {ex.Message}";
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
