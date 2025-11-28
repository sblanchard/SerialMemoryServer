using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class SelfHostModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _appConfig;

    public SelfHostModel(ApiClientService apiClient, AppConfig appConfig)
    {
        _apiClient = apiClient;
        _appConfig = appConfig;
    }

    public bool IsSelfHosted => _appConfig.IsSelfHosted;

    // Status Panel
    public SelfHostStatus? Status { get; set; }

    // Configuration Panel
    public string? ConfigJson { get; set; }

    // Diagnostics Panel
    public SelfHostDiagnostics? Diagnostics { get; set; }

    // Messages
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync()
    {
        if (!IsSelfHosted)
        {
            ErrorMessage = "This page is only available in self-hosted mode.";
            return;
        }

        await LoadAllDataAsync();
    }

    public async Task<IActionResult> OnPostRunIntegrityCheckAsync()
    {
        if (!IsSelfHosted)
        {
            return RedirectToPage();
        }

        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsync("/api/selfhost/maintenance/integrity", null);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<MaintenanceResponse>();
                SuccessMessage = result?.Message ?? "Integrity check scheduled";
            }
            else
            {
                ErrorMessage = "Failed to start integrity check";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadAllDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCompactStorageAsync()
    {
        if (!IsSelfHosted)
        {
            return RedirectToPage();
        }

        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsync("/api/selfhost/maintenance/vacuum", null);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<MaintenanceResponse>();
                SuccessMessage = result?.Message ?? "Storage compaction scheduled";
            }
            else
            {
                ErrorMessage = "Failed to start storage compaction";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadAllDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRebuildIndexesAsync()
    {
        if (!IsSelfHosted)
        {
            return RedirectToPage();
        }

        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsync("/api/selfhost/maintenance/reindex", null);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<MaintenanceResponse>();
                SuccessMessage = result?.Message ?? "Index rebuild scheduled";
            }
            else
            {
                ErrorMessage = "Failed to start index rebuild";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadAllDataAsync();
        return Page();
    }

    private async Task LoadAllDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();

            var statusTask = client.GetAsync("/api/selfhost/status");
            var configTask = client.GetAsync("/api/selfhost/config");
            var diagnosticsTask = client.GetAsync("/api/selfhost/diagnostics");

            await Task.WhenAll(statusTask, configTask, diagnosticsTask);

            // Status
            if (statusTask.Result.IsSuccessStatusCode)
            {
                Status = await statusTask.Result.Content.ReadFromJsonAsync<SelfHostStatus>();
            }

            // Config - store as formatted JSON
            if (configTask.Result.IsSuccessStatusCode)
            {
                var configRaw = await configTask.Result.Content.ReadAsStringAsync();
                try
                {
                    var doc = JsonDocument.Parse(configRaw);
                    ConfigJson = MaskSecrets(JsonSerializer.Serialize(doc, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
                }
                catch
                {
                    ConfigJson = configRaw;
                }
            }

            // Diagnostics
            if (diagnosticsTask.Result.IsSuccessStatusCode)
            {
                Diagnostics = await diagnosticsTask.Result.Content.ReadFromJsonAsync<SelfHostDiagnostics>();
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load self-host data: {ex.Message}";
        }
    }

    private static string MaskSecrets(string json)
    {
        // Mask common secret patterns
        var patterns = new[]
        {
            ("password", "***"),
            ("secret", "***"),
            ("api_key", "***"),
            ("apikey", "***"),
            ("token", "***"),
            ("connectionstring", "[REDACTED]")
        };

        var result = json;
        foreach (var (pattern, mask) in patterns)
        {
            // Simple masking - look for "pattern": "value" and replace value
            var regex = new System.Text.RegularExpressions.Regex(
                $@"(""{pattern}"":\s*"")[^""]*("")",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            result = regex.Replace(result, $"$1{mask}$2");
        }
        return result;
    }

    // Response DTOs
    public sealed class SelfHostStatus
    {
        public string Mode { get; init; } = "";
        public string InstanceId { get; init; } = "";
        public bool QuotasEnabled { get; init; }
        public bool PowerModeEnabled { get; init; }
        public string Version { get; init; } = "";
        public string Uptime { get; init; } = "";
        public string Status { get; init; } = "";
    }

    public sealed class SelfHostDiagnostics
    {
        public DatabaseInfo? Database { get; init; }
        public SystemInfo? System { get; init; }
        public RuntimeInfo? Runtime { get; init; }
    }

    public sealed class DatabaseInfo
    {
        public long Memories { get; init; }
        public long Entities { get; init; }
        public string Status { get; init; } = "";
    }

    public sealed class SystemInfo
    {
        public string MachineName { get; init; } = "";
        public string OsVersion { get; init; } = "";
        public int ProcessorCount { get; init; }
        public long WorkingSet { get; init; }
        public long GcMemory { get; init; }
    }

    public sealed class RuntimeInfo
    {
        public string Version { get; init; } = "";
        public string Framework { get; init; } = "";
    }

    public sealed class MaintenanceResponse
    {
        public string Message { get; init; } = "";
        public string JobId { get; init; } = "";
        public string? Note { get; init; }
        public string? EstimatedDuration { get; init; }
    }
}
