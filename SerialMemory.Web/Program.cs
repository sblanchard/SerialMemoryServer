using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using SerialMemory.Core.Deployment;
using SerialMemory.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure Data Protection with persistent keys
// This prevents session cookies from becoming invalid after container restarts
var dataProtectionPath = builder.Configuration["DATA_PROTECTION_PATH"]
    ?? Environment.GetEnvironmentVariable("DATA_PROTECTION_PATH")
    ?? "/app/keys";

builder.Services.AddDataProtection()
    .SetApplicationName("SerialMemory.Web")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

// Configuration
var apiBaseUrl = builder.Configuration["API_BASE_URL"] ?? "http://localhost:5000";
var dashboardApiUrl = builder.Configuration["DASHBOARD_API_URL"] ?? "http://localhost:5001";
var stripePublishableKey = builder.Configuration["STRIPE_PUBLISHABLE_KEY"] ?? "";
var serviceApiKey = builder.Configuration["SERVICE_API_KEY"]
    ?? Environment.GetEnvironmentVariable("SERVICE_API_KEY")
    ?? "";

// Add deployment context
builder.Services.AddSingleton<IDeploymentContext, DeploymentContext>();

// Add services
builder.Services.AddRazorPages();

// Main API client (for graph, memories, etc.)
builder.Services.AddHttpClient("Api", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    // Service API key for server-to-server communication
    if (!string.IsNullOrEmpty(serviceApiKey))
    {
        client.DefaultRequestHeaders.Add("X-Api-Key", serviceApiKey);
    }
});

// Dashboard API client (for auth, user management)
builder.Services.AddHttpClient("DashboardApi", client =>
{
    client.BaseAddress = new Uri(dashboardApiUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Owner", policy =>
        policy.RequireAssertion(context =>
            context.User.IsInRole("owner") ||
            context.User.IsInRole("admin") ||
            context.User.HasClaim("role", "owner") ||
            context.User.HasClaim("role", "admin")));
});

// Add session for storing internal tokens (server-side, not in cookies)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Internal token service for dashboard-to-API communication
builder.Services.AddSingleton<InternalTokenService>();

// HttpContext accessor for services that need request context
builder.Services.AddHttpContextAccessor();

// API client service that adds tenant context to all API calls
builder.Services.AddScoped<ApiClientService>();

// Get deployment context for config
var deploymentContext = new DeploymentContext();

// Store configuration for views
builder.Services.AddSingleton(new AppConfig
{
    ApiBaseUrl = apiBaseUrl,
    StripePublishableKey = stripePublishableKey,
    IsSelfHosted = deploymentContext.IsSelfHosted,
    DeploymentMode = deploymentContext.Mode.ToString(),
    PowerModeEnabled = !deploymentContext.PowerModeGloballyDisabled,
    QuotasEnabled = deploymentContext.QuotasEnabled
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// API Proxy - forwards /api/* requests to internal API server
// Uses internal short-lived tokens, NOT external API keys
app.Map("/api/{**path}", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    InternalTokenService internalTokenService,
    ILogger<Program> logger,
    string path) =>
{
    var client = httpClientFactory.CreateClient("Api");
    // Note: X-Api-Key (service key) is already set on the client from AddHttpClient configuration

    // SECURITY: Get tenant context from cookie claims (primary authority)
    var cookieTenantId = context.User.FindFirst("tenant_id")?.Value;
    var cookieUserId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrEmpty(cookieTenantId) || string.IsNullOrEmpty(cookieUserId))
    {
        logger.LogWarning("API proxy request rejected: missing cookie claims");
        return Results.Unauthorized();
    }

    // SECURITY: Get internal token from session (NOT from cookie)
    var internalToken = context.Session.GetString("internal_token");
    var sessionTenantId = context.Session.GetString("internal_token_tenant");
    var sessionUserId = context.Session.GetString("internal_token_user");

    // SECURITY: Validate tenant matches between cookie and session
    if (sessionTenantId != cookieTenantId || sessionUserId != cookieUserId)
    {
        logger.LogError(
            "SECURITY: Cross-tenant attack detected! Cookie tenant={CookieTenant}/{CookieUser}, Session tenant={SessionTenant}/{SessionUser}",
            cookieTenantId, cookieUserId, sessionTenantId, sessionUserId);

        // Clear compromised session
        context.Session.Clear();
        return Results.Problem(
            "Session integrity check failed. Please log in again.",
            statusCode: 403);
    }

    // Validate internal token if present
    if (!string.IsNullOrEmpty(internalToken))
    {
        var tokenClaims = internalTokenService.ValidateToken(internalToken);

        if (tokenClaims == null)
        {
            // Token expired or invalid - regenerate
            logger.LogDebug("Internal token expired for user {UserId}, regenerating", cookieUserId);
            internalToken = internalTokenService.GenerateToken(new InternalTokenClaims
            {
                TenantId = cookieTenantId,
                UserId = cookieUserId,
                WorkspaceId = "default",
                Role = context.User.FindFirst("role")?.Value ?? "user",
                Email = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            });
            context.Session.SetString("internal_token", internalToken);
        }
        else if (!internalTokenService.ValidateTenantMatch(context.User, tokenClaims))
        {
            logger.LogError(
                "SECURITY: Token tenant mismatch! Clearing session for user {UserId}",
                cookieUserId);
            context.Session.Clear();
            return Results.Problem(
                "Security validation failed. Please log in again.",
                statusCode: 403);
        }

        // Forward internal token to API
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", internalToken);
    }
    else
    {
        // No internal token - generate new one
        logger.LogDebug("No internal token found for user {UserId}, generating", cookieUserId);
        internalToken = internalTokenService.GenerateToken(new InternalTokenClaims
        {
            TenantId = cookieTenantId,
            UserId = cookieUserId,
            WorkspaceId = "default",
            Role = context.User.FindFirst("role")?.Value ?? "user",
            Email = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        });
        context.Session.SetString("internal_token", internalToken);
        context.Session.SetString("internal_token_tenant", cookieTenantId);
        context.Session.SetString("internal_token_user", cookieUserId);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", internalToken);
    }

    // Add tenant context headers for API (trusted because of service API key)
    client.DefaultRequestHeaders.Add("X-Tenant-Id", cookieTenantId);
    client.DefaultRequestHeaders.Add("X-User-Id", cookieUserId);

    // Build target URL
    var queryString = context.Request.QueryString.Value ?? "";
    var targetUrl = $"/api/{path}{queryString}";

    try
    {
        HttpResponseMessage response;

        if (context.Request.Method == "GET")
        {
            response = await client.GetAsync(targetUrl);
        }
        else if (context.Request.Method == "POST")
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            response = await client.PostAsync(targetUrl, content);
        }
        else
        {
            return Results.StatusCode(405);
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

        return Results.Content(responseBody, contentType, System.Text.Encoding.UTF8, (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "API proxy error for path {Path}", path);
        return Results.Problem($"API proxy error: {ex.Message}", statusCode: 502);
    }
});

app.Run();

public sealed class AppConfig
{
    public string ApiBaseUrl { get; init; } = "";
    public string StripePublishableKey { get; init; } = "";
    public bool IsSelfHosted { get; init; }
    public string DeploymentMode { get; init; } = "";
    public bool PowerModeEnabled { get; init; }
    public bool QuotasEnabled { get; init; }
}
