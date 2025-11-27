using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public class MemoriesModel : PageModel
{
    private readonly IConfiguration _configuration;

    public string ApiBaseUrl { get; private set; } = "";
    public string AuthToken { get; private set; } = "";

    public MemoriesModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void OnGet()
    {
        // Use same-origin proxy - all API calls go through /api/* on this server
        // which proxies to internal Docker API, keeping traffic inside the network
        ApiBaseUrl = "";

        // Auth token is read from cookie by the proxy, not needed in JS
        AuthToken = "";
    }
}
