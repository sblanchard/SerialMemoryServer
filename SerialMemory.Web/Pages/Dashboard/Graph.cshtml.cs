using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public class GraphModel : PageModel
{
    private readonly IConfiguration _configuration;

    public string ApiBaseUrl { get; private set; } = "";
    public string AuthToken { get; private set; } = "";

    public GraphModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void OnGet()
    {
        // Get API base URL from config
        ApiBaseUrl = _configuration["API_BASE_URL"] ?? "http://localhost:5000";

        // Get auth token from claims
        var tokenClaim = User.FindFirst("access_token");
        AuthToken = tokenClaim?.Value ?? "";

        // If no token in claims, check session/cookie
        if (string.IsNullOrEmpty(AuthToken))
        {
            AuthToken = Request.Cookies["auth_token"] ?? "";
        }
    }
}
