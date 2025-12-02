using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

/// <summary>
/// Base page model for dashboard pages that provides common API configuration properties.
/// </summary>
[Authorize]
public abstract class DashboardPageModel : PageModel
{
    protected readonly AppConfig AppConfig;

    protected DashboardPageModel(AppConfig appConfig)
    {
        AppConfig = appConfig;
    }

    /// <summary>
    /// Public API base URL (used for SignalR/WebSocket connections that can't go through proxy)
    /// </summary>
    public string ApiBaseUrl => AppConfig.ApiBaseUrl;

    /// <summary>
    /// Dashboard API base URL (if different from main API)
    /// </summary>
    public string DashboardApiBaseUrl => AppConfig.DashboardApiBaseUrl;

    /// <summary>
    /// SignalR access token from InternalTokenMiddleware
    /// </summary>
    public string? SignalRToken => HttpContext.Items["InternalToken"] as string;

    /// <summary>
    /// Service API key for authenticated API calls (only exposed to authorized users)
    /// </summary>
    public string ServiceApiKey => AppConfig.ServiceApiKey ?? string.Empty;
}
