using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Onboarding;

[Authorize]
public sealed class ApiKeyModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ApiKeyModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [BindProperty]
    public string KeyName { get; set; } = "Default Key";

    public string? GeneratedApiKey { get; set; }
    public Guid? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? McpConfig { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadTenantAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadTenantAsync();

        if (!TenantId.HasValue)
        {
            ErrorMessage = "No workspace found. Please create a workspace first.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(KeyName))
        {
            ErrorMessage = "Key name is required";
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/tenants/{TenantId}/apikeys", new
            {
                name = KeyName
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ApiKeyResult>();
                GeneratedApiKey = result?.ApiKey;

                // Generate MCP config
                McpConfig = GenerateMcpConfig(GeneratedApiKey ?? "YOUR_API_KEY");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed to generate API key: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return Page();
    }

    private async Task LoadTenantAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var tenant = await client.GetFromJsonAsync<TenantInfo>("/api/tenants/current");

            if (tenant != null)
            {
                TenantId = tenant.Id;
                TenantName = tenant.Name;
            }
        }
        catch (HttpRequestException)
        {
            // No tenant
        }
    }

    private static string GenerateMcpConfig(string apiKey)
    {
        return $$"""
        {
          "mcpServers": {
            "serialmemory-memory": {
              "command": "npx",
              "args": ["-y", "@anthropic/mcp-remote", "https://api.serialmemory.com/mcp"],
              "env": {
                "API_KEY": "{{apiKey}}"
              }
            }
          }
        }
        """;
    }

    public sealed class TenantInfo
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
    }

    public sealed class ApiKeyResult
    {
        public Guid Id { get; init; }
        public string ApiKey { get; init; } = "";
        public string Name { get; init; } = "";
    }
}
