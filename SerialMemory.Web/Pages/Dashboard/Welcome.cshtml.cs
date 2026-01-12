using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Web.Pages.Dashboard;

/// <summary>
/// Welcome page shown after successful email verification and tenant provisioning.
/// Displays the one-time API key with Docker command and MCP config.
/// </summary>
[Authorize]
public sealed class WelcomeModel : PageModel
{
    private readonly IDeveloperOnboardingService? _onboardingService;

    public WelcomeModel(IDeveloperOnboardingService? onboardingService = null)
    {
        _onboardingService = onboardingService;
    }

    /// <summary>
    /// The API key (only shown once, passed via TempData from verification redirect).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Docker run command for the MCP client.
    /// </summary>
    public string? DockerCommand { get; set; }

    /// <summary>
    /// MCP config JSON for Claude Desktop.
    /// </summary>
    public string? McpConfig { get; set; }

    /// <summary>
    /// Whether the key has already been viewed (cannot show again).
    /// </summary>
    public bool KeyAlreadyViewed { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
        // Check if API key was passed from verification redirect
        if (TempData.TryGetValue("ApiKey", out var apiKey) && apiKey is string key && !string.IsNullOrEmpty(key))
        {
            ApiKey = key;

            if (TempData.TryGetValue("DockerCommand", out var docker) && docker is string dockerCmd)
            {
                DockerCommand = dockerCmd;
            }
            else if (_onboardingService != null)
            {
                DockerCommand = _onboardingService.GenerateDockerCommand(key);
            }

            if (TempData.TryGetValue("McpConfig", out var mcp) && mcp is string mcpCfg)
            {
                McpConfig = mcpCfg;
            }
            else if (_onboardingService != null)
            {
                McpConfig = _onboardingService.GenerateMcpConfig(key);
            }
        }
        else
        {
            // Key already viewed - cannot show again
            KeyAlreadyViewed = true;
        }
    }

    public async Task<IActionResult> OnPostRegenerateAsync()
    {
        if (_onboardingService == null)
        {
            ErrorMessage = "Onboarding service not available";
            return Page();
        }

        var tenantIdClaim = User.FindFirst("tenant_id")?.Value;
        var userId = User.FindFirst("sub")?.Value ?? User.Identity?.Name;

        if (string.IsNullOrEmpty(tenantIdClaim) || !Guid.TryParse(tenantIdClaim, out var tenantId))
        {
            ErrorMessage = "Invalid tenant";
            return Page();
        }

        if (string.IsNullOrEmpty(userId))
        {
            ErrorMessage = "Invalid user";
            return Page();
        }

        try
        {
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var result = await _onboardingService.RegenerateApiKeyAsync(tenantId, userId, ipAddress);

            ApiKey = result.Key;
            DockerCommand = result.DockerCommand;
            McpConfig = result.McpConfig;
            KeyAlreadyViewed = false;

            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to regenerate API key: {ex.Message}";
            return Page();
        }
    }
}
