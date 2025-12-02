using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Core.Deployment;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages;

public sealed class LoginModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly InternalTokenService _internalTokenService;
    private readonly ILogger<LoginModel> _logger;
    private readonly IDeploymentContext _deploymentContext;

    public LoginModel(
        IHttpClientFactory httpClientFactory,
        InternalTokenService internalTokenService,
        ILogger<LoginModel> logger,
        IDeploymentContext deploymentContext)
    {
        _httpClientFactory = httpClientFactory;
        _internalTokenService = internalTokenService;
        _logger = logger;
        _deploymentContext = deploymentContext;
    }

    [BindProperty]
    public string ApiKey { get; set; } = "";

    [BindProperty]
    public bool RememberMe { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ReturnUrl { get; set; }

    public async Task OnGetAsync([FromQuery] string? returnUrl)
    {
        ReturnUrl = returnUrl;

        // SECURITY: Always clear any existing session on login page GET
        // This prevents session inheritance attacks where a new user
        // could accidentally inherit an old session's tenant claims
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Cookies.Delete("auth_token");
        HttpContext.Session.Clear();
    }

    public async Task<IActionResult> OnPostAsync([FromQuery] string? returnUrl)
    {
        // SECURITY: Always clear old auth tokens before processing new login
        // This prevents cross-tenant data leakage from stale cookies
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Cookies.Delete("auth_token");
        HttpContext.Session.Clear();

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ErrorMessage = "API Key is required.";
            return Page();
        }

        try
        {
            // Use Dashboard API for authentication (it has the /me endpoint)
            var client = _httpClientFactory.CreateClient("DashboardApi");

            _logger.LogInformation(
                "LOGIN_ATTEMPT: BaseAddress={BaseAddress}, ApiKeyPrefix={Prefix}, ApiKeyLength={Length}",
                client.BaseAddress,
                ApiKey.Length > 10 ? ApiKey[..10] + "..." : "(short)",
                ApiKey.Length);

            // Send raw API key in X-Api-Key header (not Bearer token)
            // This matches how MCP clients and curl authenticate
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

            // Use a 15-second timeout for the auth request
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await client.GetAsync("/me", cts.Token);

            _logger.LogInformation(
                "LOGIN_RESPONSE: StatusCode={StatusCode}, ReasonPhrase={Reason}",
                (int)response.StatusCode, response.ReasonPhrase);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Login failed with status {StatusCode}: {Error}",
                    response.StatusCode, errorContent);

                ErrorMessage = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Invalid API key. Please check and try again."
                    : $"Authentication failed: {response.StatusCode}";
                return Page();
            }

            var result = await response.Content.ReadFromJsonAsync<MeResult>();

            if (result == null)
            {
                ErrorMessage = "Unexpected response from server.";
                return Page();
            }

            _logger.LogInformation(
                "LOGIN_DEBUG: UserId={UserId}, TenantId={TenantId}, Email={Email}, Role={Role}",
                result.UserId, result.TenantId, result.Email ?? "(null)", result.Role);

            // Determine if user is root admin
            var isRootAdmin = _deploymentContext.IsRootAdmin(result.Email);
            _logger.LogInformation(
                "LOGIN_DEBUG: IsRootAdmin={IsRootAdmin}, ConfiguredRootEmail={ConfiguredRoot}",
                isRootAdmin, _deploymentContext.RootAdminEmail ?? "(not set)");

            // Sign in with claims - DO NOT store api_key in claims
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, result.UserId),
                new(ClaimTypes.Email, result.Email ?? result.UserId),
                new(ClaimTypes.Name, result.TenantName ?? result.UserId),
                new("tenant_id", result.TenantId),
                new("role", result.Role),
                new("is_root_admin", isRootAdmin.ToString().ToLowerInvariant())
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
                Email = result.Email,
                IsRootAdmin = isRootAdmin
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
        [System.Text.Json.Serialization.JsonPropertyName("tenantId")]
        public string TenantId { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("tenantName")]
        public string? TenantName { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("userId")]
        public string UserId { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("email")]
        public string? Email { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("role")]
        public string Role { get; init; } = "";
    }
}
