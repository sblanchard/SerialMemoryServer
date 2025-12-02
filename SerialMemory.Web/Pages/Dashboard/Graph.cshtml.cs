using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public class GraphModel : DashboardPageModel
{
    public GraphModel(AppConfig appConfig) : base(appConfig)
    {
    }

    public void OnGet()
    {
        // All properties come from DashboardPageModel base class
    }
}
