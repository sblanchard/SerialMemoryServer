using Microsoft.AspNetCore.Authentication.Cookies;
using SerialMemory.Core.Deployment;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddAuthorization();

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
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// API Proxy - forwards /api/* requests to internal API server
// This keeps all traffic internal to Docker network
app.Map("/api/{**path}", async (HttpContext context, IHttpClientFactory httpClientFactory, string path) =>
{
    var client = httpClientFactory.CreateClient("Api");
    // Note: X-Api-Key is already set on the client from AddHttpClient configuration

    // Forward authorization header from cookie (for user context)
    var authToken = context.Request.Cookies["auth_token"];
    if (!string.IsNullOrEmpty(authToken))
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
    }

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
