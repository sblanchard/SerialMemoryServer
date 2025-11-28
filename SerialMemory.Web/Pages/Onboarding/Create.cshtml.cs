using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Onboarding;

[Authorize]
public sealed class CreateModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public CreateModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    [BindProperty]
    public string WorkspaceName { get; set; } = "";

    [BindProperty]
    public string? Description { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceName))
        {
            ErrorMessage = "Workspace name is required";
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsJsonAsync("/api/tenants", new
            {
                name = WorkspaceName,
                description = Description,
                email = User.Identity?.Name
            });

            if (response.IsSuccessStatusCode)
            {
                return RedirectToPage("/Onboarding/ApiKey");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed to create workspace: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return Page();
    }
}
