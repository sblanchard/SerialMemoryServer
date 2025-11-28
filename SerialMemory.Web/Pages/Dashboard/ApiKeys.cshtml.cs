using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ApiKeysModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public ApiKeysModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public IReadOnlyList<ApiKeyInfo> ApiKeys { get; set; } = [];
    public string? NewApiKey { get; set; }
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadApiKeysAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, string? description, int? expiresIn)
    {
        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var request = new
            {
                Name = name,
                Description = description,
                ExpiresInDays = expiresIn
            };

            var response = await client.PostAsJsonAsync("/api-keys", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CreateApiKeyResult>();
                NewApiKey = result?.Key;
                SuccessMessage = "API key created successfully";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        var error = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(content);
                        ErrorMessage = error?.Message ?? error?.Error ?? $"Failed to create API key ({response.StatusCode})";
                    }
                    catch
                    {
                        ErrorMessage = $"Failed to create API key: {content}";
                    }
                }
                else
                {
                    ErrorMessage = $"Failed to create API key ({response.StatusCode})";
                }
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to the server: {ex.Message}";
        }

        await LoadApiKeysAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid keyId)
    {
        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var response = await client.DeleteAsync($"/api-keys/{keyId}");

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = "API key revoked successfully";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        var error = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(content);
                        ErrorMessage = error?.Message ?? error?.Error ?? $"Failed to revoke API key ({response.StatusCode})";
                    }
                    catch
                    {
                        ErrorMessage = $"Failed to revoke API key: {content}";
                    }
                }
                else
                {
                    ErrorMessage = $"Failed to revoke API key ({response.StatusCode})";
                }
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Could not connect to the server: {ex.Message}";
        }

        await LoadApiKeysAsync();
        return Page();
    }

    private async Task LoadApiKeysAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var response = await client.GetAsync("/api-keys?includeRevoked=true");

            if (response.IsSuccessStatusCode)
            {
                var keys = await response.Content.ReadFromJsonAsync<List<ApiKeyInfo>>();
                ApiKeys = keys ?? [];
            }
        }
        catch (HttpRequestException)
        {
            // API unavailable
        }
    }

    public sealed class ApiKeyInfo
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string KeyPrefix { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public bool IsRevoked { get; init; }
    }

    private sealed class CreateApiKeyResult
    {
        public Guid Id { get; init; }
        public string Key { get; init; } = "";
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
