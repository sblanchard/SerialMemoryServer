using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ExportsModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public ExportsModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public ExportStats? Stats { get; set; }
    public IReadOnlyList<ExportHistoryItem> History { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadDataAsync();
    }

    public async Task<IActionResult> OnPostExportAsync(string format, bool activeOnly, bool includeEntities,
        float minConfidence, string? groupBy, bool includeSessions, bool compress)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/exports/trigger", new
            {
                format,
                activeOnly,
                includeEntities,
                minConfidence,
                groupBy,
                includeSessions,
                compress
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ExportResult>();
                SuccessMessage = $"Export completed! Format: {format}, Size: {FormatBytes(result?.FileSizeBytes ?? 0)}";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Export failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadDataAsync();
        return Page();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var statsTask = client.GetFromJsonAsync<ExportStats>("/api/exports/stats");
            var historyTask = client.GetFromJsonAsync<ExportHistoryResponse>("/api/exports/history?limit=20");

            await Task.WhenAll(statsTask, historyTask);

            Stats = await statsTask;
            History = (await historyTask)?.Exports ?? [];
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load export data: {ex.Message}";
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    public sealed class ExportStats
    {
        public long ActiveMemories { get; init; }
        public long TotalEntities { get; init; }
        public long TotalRelationships { get; init; }
        public DateTimeOffset? LastExportAt { get; init; }
        public long TotalExports { get; init; }
    }

    public sealed class ExportHistoryResponse { public IReadOnlyList<ExportHistoryItem>? Exports { get; init; } }

    public sealed class ExportHistoryItem
    {
        public Guid Id { get; init; }
        public string Format { get; init; } = "";
        public string Status { get; init; } = "";
        public long? FileSizeBytes { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed class ExportResult
    {
        public Guid ExportId { get; init; }
        public string Status { get; init; } = "";
        public long FileSizeBytes { get; init; }
        public string? DownloadUrl { get; init; }
    }
}
