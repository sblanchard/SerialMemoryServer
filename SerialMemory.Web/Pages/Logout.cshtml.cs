using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages;

public sealed class LogoutModel : PageModel
{
    private readonly ILogger<LogoutModel> _logger;

    public LogoutModel(ILogger<LogoutModel> logger)
    {
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var tenantId = User.FindFirst("tenant_id")?.Value;

        _logger.LogInformation(
            "User {UserId} logging out from tenant {TenantId}",
            userId ?? "unknown", tenantId ?? "unknown");

        // SECURITY: Clear all auth-related session data
        HttpContext.Session.Remove("internal_token");
        HttpContext.Session.Remove("internal_token_tenant");
        HttpContext.Session.Remove("internal_token_user");
        HttpContext.Session.Clear();

        // SECURITY: Delete the legacy auth_token cookie if it exists
        Response.Cookies.Delete("auth_token");

        // Sign out of cookie authentication
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        return RedirectToPage("/Index");
    }
}
