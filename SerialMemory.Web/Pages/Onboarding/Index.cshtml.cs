using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Onboarding;

[Authorize]
public sealed class IndexModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public IndexModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public TenantInfo? CurrentTenant { get; set; }
    public IReadOnlyList<ApiKeyInfo> ApiKeys { get; set; } = [];
    public bool HasCompletedOnboarding { get; set; }

    public async Task OnGetAsync()
    {
        await LoadTenantDataAsync();
    }

    private async Task LoadTenantDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
            CurrentTenant = await client.GetFromJsonAsync<TenantInfo>("/api/tenants/current");

            if (CurrentTenant != null)
            {
                var keysResponse = await client.GetFromJsonAsync<ApiKeysResponse>($"/api/tenants/{CurrentTenant.Id}/apikeys");
                ApiKeys = keysResponse?.Items ?? [];
                HasCompletedOnboarding = ApiKeys.Count > 0;
            }
        }
        catch (HttpRequestException)
        {
            // No tenant yet
            CurrentTenant = null;
        }
    }

    public sealed class TenantInfo
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? Email { get; init; }
        public string Plan { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
    }

    public sealed class ApiKeysResponse { public IReadOnlyList<ApiKeyInfo>? Items { get; init; } }

    public sealed class ApiKeyInfo
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? MaskedKey { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public bool IsActive { get; init; }
    }
}
