using Microsoft.AspNetCore.Authorization;

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
