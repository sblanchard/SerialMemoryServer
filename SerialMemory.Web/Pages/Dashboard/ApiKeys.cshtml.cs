using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ApiKeysModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ApiKeysModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
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
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            ErrorMessage = "Authentication required";
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
                NewApiKey = result?.ApiKey;
                SuccessMessage = "API key created successfully";
            }
            else
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                ErrorMessage = error?.Message ?? "Failed to create API key";
            }
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server";
        }

        await LoadApiKeysAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid keyId)
    {
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            ErrorMessage = "Authentication required";
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.DeleteAsync($"/api-keys/{keyId}");

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = "API key revoked successfully";
            }
            else
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                ErrorMessage = error?.Message ?? "Failed to revoke API key";
            }
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not connect to the server";
        }

        await LoadApiKeysAsync();
        return Page();
    }

    private async Task LoadApiKeysAsync()
    {
        var token = GetAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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

    private string? GetAuthToken()
    {
        return User.FindFirst("token")?.Value ?? User.FindFirst("api_key")?.Value;
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
        public string ApiKey { get; init; } = "";
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
