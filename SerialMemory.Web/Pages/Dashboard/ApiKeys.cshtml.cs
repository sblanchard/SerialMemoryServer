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

    public async Task<IActionResult> OnPostRotateAsync(Guid keyId)
    {
        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var response = await client.PostAsync($"/api-keys/{keyId}/rotate", null);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<RotateApiKeyResult>();
                NewApiKey = result?.Key;
                SuccessMessage = "API key rotated successfully. The old key has been revoked.";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        var error = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(content);
                        ErrorMessage = error?.Message ?? error?.Error ?? $"Failed to rotate API key ({response.StatusCode})";
                    }
                    catch
                    {
                        ErrorMessage = $"Failed to rotate API key: {content}";
                    }
                }
                else
                {
                    ErrorMessage = $"Failed to rotate API key ({response.StatusCode})";
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

    // OAuth credentials shown after enabling
    public string? OAuthClientId { get; set; }
    public string? OAuthClientSecret { get; set; }

    public async Task<IActionResult> OnPostEnableOAuthAsync(Guid keyId)
    {
        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var request = new { redirectUris = new[] { "https://chat.openai.com/aip/plugin", "https://mcp.serialmemory.dev/mcp" } };
            var response = await client.PostAsJsonAsync($"/api-keys/{keyId}/oauth/enable", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<EnableOAuthResult>();
                OAuthClientId = result?.ClientId;
                OAuthClientSecret = result?.ClientSecret;
                SuccessMessage = "OAuth enabled successfully. Save the client secret - it will not be shown again!";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        var error = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(content);
                        ErrorMessage = error?.Message ?? error?.Error ?? $"Failed to enable OAuth ({response.StatusCode})";
                    }
                    catch
                    {
                        ErrorMessage = $"Failed to enable OAuth: {content}";
                    }
                }
                else
                {
                    ErrorMessage = $"Failed to enable OAuth ({response.StatusCode})";
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

    private sealed class EnableOAuthResult
    {
        public string? ClientId { get; init; }
        public string? ClientSecret { get; init; }
    }

    private async Task LoadApiKeysAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var response = await client.GetAsync("/api-keys?includeRevoked=true");

            if (response.IsSuccessStatusCode)
            {
                // API returns { keys: [...], total: X }
                var result = await response.Content.ReadFromJsonAsync<ApiKeysResponse>();
                ApiKeys = result?.Keys?.Select(k => new ApiKeyInfo
                {
                    Id = k.Id,
                    Name = k.Name,
                    Description = null, // Not returned by API
                    KeyPrefix = k.KeyPrefix,
                    CreatedAt = k.CreatedAt,
                    LastUsedAt = k.LastUsedAt,
                    ExpiresAt = k.ExpiresAt,
                    IsRevoked = k.Status == "revoked"
                }).ToList() ?? [];
            }
        }
        catch (HttpRequestException)
        {
            // API unavailable
        }
    }

    // Response wrapper matching API format (camelCase from JSON)
    private sealed class ApiKeysResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("keys")]
        public List<ApiKeyDto>? Keys { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("total")]
        public int Total { get; init; }
    }

    // DTO matching API response format (camelCase from JSON)
    private sealed class ApiKeyDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public Guid Id { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("keyPrefix")]
        public string KeyPrefix { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("scopes")]
        public string[]? Scopes { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("createdBy")]
        public string? CreatedBy { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("lastUsedAt")]
        public DateTimeOffset? LastUsedAt { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("expiresAt")]
        public DateTimeOffset? ExpiresAt { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("revokedAt")]
        public DateTimeOffset? RevokedAt { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; init; } = "active";
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

    private sealed class RotateApiKeyResult
    {
        public Guid Id { get; init; }
        public string Key { get; init; } = "";
        public string? Message { get; init; }
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
