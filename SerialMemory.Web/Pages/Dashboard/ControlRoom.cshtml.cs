using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize(Policy = "Owner")]
public sealed class ControlRoomModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _appConfig;

    public ControlRoomModel(ApiClientService apiClient, AppConfig appConfig)
    {
        _apiClient = apiClient;
        _appConfig = appConfig;
    }

    // Global State
    public bool GlobalKillSwitchActive { get; set; }
    public string? GlobalKillReason { get; set; }
    public DateTimeOffset? GlobalKillExpiresAt { get; set; }
    public bool GlobalReadOnlyActive { get; set; }
    public string? GlobalReadOnlyReason { get; set; }

    // Tenant State (for current tenant)
    public bool TenantKillSwitchActive { get; set; }
    public string? TenantKillReason { get; set; }
    public bool TenantReadOnlyActive { get; set; }
    public bool TenantNoIngestActive { get; set; }

    // Quota Status
    public decimal CreditsUsed { get; set; }
    public decimal CreditsAllocated { get; set; }
    public decimal UsagePercent { get; set; }
    public bool IsAutoThrottled { get; set; }

    // Recent Actions
    public List<KillSwitchActionItem> RecentActions { get; set; } = [];

    // Form inputs
    [BindProperty]
    public string? Reason { get; set; }

    [BindProperty]
    public int? ExpiresInMinutes { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsSampleData { get; set; }

    public async Task OnGetAsync()
    {
        await LoadControlRoomStateAsync();
    }

    public async Task<IActionResult> OnPostEnableGlobalKillAsync()
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "Reason is required for enabling kill switch";
            await LoadControlRoomStateAsync();
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var response = await client.PostAsJsonAsync("/admin/control-room/global/kill", new
            {
                Reason,
                ExpiresInMinutes
            });

            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "Global kill switch enabled";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API unavailable: {ex.Message}";
        }

        await LoadControlRoomStateAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDisableGlobalKillAsync()
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "Reason is required for disabling kill switch";
            await LoadControlRoomStateAsync();
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var request = new HttpRequestMessage(HttpMethod.Delete, "/admin/control-room/global/kill")
            {
                Content = JsonContent.Create(new { Reason })
            };
            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "Global kill switch disabled";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API unavailable: {ex.Message}";
        }

        await LoadControlRoomStateAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostEnableGlobalReadOnlyAsync()
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "Reason is required";
            await LoadControlRoomStateAsync();
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var response = await client.PostAsJsonAsync("/admin/control-room/global/readonly", new
            {
                Reason,
                ExpiresInMinutes
            });

            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "Global read-only mode enabled";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API unavailable: {ex.Message}";
        }

        await LoadControlRoomStateAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDisableGlobalReadOnlyAsync()
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "Reason is required";
            await LoadControlRoomStateAsync();
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var request = new HttpRequestMessage(HttpMethod.Delete, "/admin/control-room/global/readonly")
            {
                Content = JsonContent.Create(new { Reason })
            };
            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "Global read-only mode disabled";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API unavailable: {ex.Message}";
        }

        await LoadControlRoomStateAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostEnableTenantNoIngestAsync()
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "Reason is required";
            await LoadControlRoomStateAsync();
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var tenantId = User.FindFirst("tenant_id")?.Value ?? "default";
            var response = await client.PostAsJsonAsync($"/admin/control-room/tenant/{tenantId}/no-ingest", new
            {
                Reason,
                ExpiresInMinutes
            });

            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "Memory ingestion disabled";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API unavailable: {ex.Message}";
        }

        await LoadControlRoomStateAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDisableTenantNoIngestAsync()
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "Reason is required";
            await LoadControlRoomStateAsync();
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient("DashboardApi");
            var tenantId = User.FindFirst("tenant_id")?.Value ?? "default";
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/control-room/tenant/{tenantId}/no-ingest")
            {
                Content = JsonContent.Create(new { Reason })
            };
            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                StatusMessage = "Memory ingestion re-enabled";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API unavailable: {ex.Message}";
        }

        await LoadControlRoomStateAsync();
        return Page();
    }

    private async Task LoadControlRoomStateAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("DashboardApi");

            // Get control room state
            var stateResponse = await client.GetAsync("/admin/control-room/state");
            if (stateResponse.IsSuccessStatusCode)
            {
                var state = await stateResponse.Content.ReadFromJsonAsync<ControlRoomState>();
                if (state != null)
                {
                    GlobalKillSwitchActive = state.GlobalKillSwitch?.IsActive ?? false;
                    GlobalKillReason = state.GlobalKillSwitch?.Reason;
                    GlobalKillExpiresAt = state.GlobalKillSwitch?.ExpiresAt;
                    GlobalReadOnlyActive = state.GlobalReadOnly?.IsActive ?? false;
                    GlobalReadOnlyReason = state.GlobalReadOnly?.Reason;
                }
            }

            // Get recent actions
            var actionsResponse = await client.GetAsync("/admin/control-room/actions?limit=10");
            if (actionsResponse.IsSuccessStatusCode)
            {
                var actions = await actionsResponse.Content.ReadFromJsonAsync<List<KillSwitchActionItem>>();
                RecentActions = actions ?? [];
            }

            // Get quota status
            var tenantId = User.FindFirst("tenant_id")?.Value ?? "default";
            var quotaResponse = await client.GetAsync($"/admin/control-room/tenant/{tenantId}/quota");
            if (quotaResponse.IsSuccessStatusCode)
            {
                var quota = await quotaResponse.Content.ReadFromJsonAsync<QuotaStatusResponse>();
                if (quota != null)
                {
                    CreditsUsed = quota.CreditsUsed;
                    CreditsAllocated = quota.CreditsAllocated;
                    UsagePercent = quota.UsagePercentage;
                    TenantNoIngestActive = quota.KillSwitchActive;
                    IsAutoThrottled = quota.UsagePercentage >= 100;
                }
            }

            IsSampleData = false;
        }
        catch (HttpRequestException)
        {
            // Show sample data when API is unavailable
            IsSampleData = true;
            GlobalKillSwitchActive = false;
            GlobalReadOnlyActive = false;
            TenantNoIngestActive = false;
            CreditsUsed = 45;
            CreditsAllocated = 100;
            UsagePercent = 45;
            RecentActions =
            [
                new() { ActionType = "GLOBAL_READONLY_DISABLED", Reason = "Maintenance complete", ActorId = "admin@example.com", Timestamp = DateTimeOffset.UtcNow.AddHours(-2) },
                new() { ActionType = "GLOBAL_READONLY_ENABLED", Reason = "Database maintenance", ActorId = "admin@example.com", Timestamp = DateTimeOffset.UtcNow.AddHours(-4) }
            ];
        }
    }

    // Response DTOs
    private sealed class ControlRoomState
    {
        public KillSwitchInfo? GlobalKillSwitch { get; set; }
        public KillSwitchInfo? GlobalReadOnly { get; set; }
        public Dictionary<string, TenantKillSwitchState>? TenantStates { get; set; }
    }

    private sealed class KillSwitchInfo
    {
        public bool IsActive { get; set; }
        public string? Reason { get; set; }
        public string? ActorId { get; set; }
        public DateTimeOffset? EnabledAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    private sealed class TenantKillSwitchState
    {
        public bool IsDisabled { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsNoIngest { get; set; }
    }

    private sealed class QuotaStatusResponse
    {
        public decimal CreditsUsed { get; set; }
        public decimal CreditsAllocated { get; set; }
        public decimal UsagePercentage { get; set; }
        public bool KillSwitchActive { get; set; }
    }

    public sealed class KillSwitchActionItem
    {
        public string ActionType { get; set; } = "";
        public string? TenantId { get; set; }
        public string? Reason { get; set; }
        public string? ActorId { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }
}
