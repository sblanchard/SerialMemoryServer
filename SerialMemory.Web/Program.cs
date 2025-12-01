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
var publicApiUrl = builder.Configuration["PUBLIC_API_URL"] ?? apiBaseUrl; // For browser JavaScript
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
            context.User.HasClaim("role", "admin") ||
            context.User.HasClaim("is_root_admin", "true")));
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
var publicDashboardApiUrl = builder.Configuration["PUBLIC_DASHBOARD_API_URL"] ?? dashboardApiUrl;
builder.Services.AddSingleton(new AppConfig
{
    ApiBaseUrl = publicApiUrl, // Public URL for browser JavaScript (SignalR, fetch)
    DashboardApiBaseUrl = publicDashboardApiUrl, // Dashboard API URL for reasoning endpoints
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
app.UseInternalTokenMiddleware(); // Auto-generates/refreshes internal tokens
app.MapRazorPages();

// API Proxy - forwards /api/* requests to internal API server
// Uses internal short-lived tokens managed by InternalTokenMiddleware
app.Map("/api/{**path}", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger,
    string path) =>
{
    var client = httpClientFactory.CreateClient("Api");

    // SECURITY: Get tenant context from cookie claims (primary authority)
    var cookieTenantId = context.User.FindFirst("tenant_id")?.Value;
    var cookieUserId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrEmpty(cookieTenantId) || string.IsNullOrEmpty(cookieUserId))
    {
        logger.LogWarning("API proxy request rejected: missing cookie claims");
        return Results.Unauthorized();
    }

    // Get internal token from middleware (stored in HttpContext.Items)
    var internalToken = context.Items["InternalToken"] as string;
    if (string.IsNullOrEmpty(internalToken))
    {
        logger.LogWarning("API proxy request rejected: no internal token available for user {UserId}", cookieUserId);
        return Results.Problem("Session expired. Please log in again.", statusCode: 401);
    }

    // Forward internal token to API as Bearer token
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", internalToken);

    // Add tenant context headers for API
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
        else if (context.Request.Method == "DELETE")
        {
            response = await client.DeleteAsync(targetUrl);
        }
        else if (context.Request.Method == "PUT")
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            response = await client.PutAsync(targetUrl, content);
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
    public string DashboardApiBaseUrl { get; init; } = "";
    public string StripePublishableKey { get; init; } = "";
    public bool IsSelfHosted { get; init; }
    public string DeploymentMode { get; init; } = "";
    public bool PowerModeEnabled { get; init; }
    public bool QuotasEnabled { get; init; }
}
