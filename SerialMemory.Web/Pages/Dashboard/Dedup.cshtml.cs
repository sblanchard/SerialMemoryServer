using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class DedupModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public DedupModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public DedupStats? Stats { get; set; }
    public IReadOnlyList<SupersedeItem> SupersedeHistory { get; set; } = [];
    public IReadOnlyList<MergeItem> MergeHistory { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var statsTask = client.GetFromJsonAsync<DedupStats>("/api/dedup/stats?days=30");
            var supersedeTask = client.GetFromJsonAsync<SupersedeResponse>("/api/dedup/supersede-history?limit=20");
            var mergeTask = client.GetFromJsonAsync<MergeResponse>("/api/dedup/merge-history?limit=20");

            await Task.WhenAll(statsTask, supersedeTask, mergeTask);

            Stats = await statsTask;
            SupersedeHistory = (await supersedeTask)?.History ?? [];
            MergeHistory = (await mergeTask)?.History ?? [];
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load dedup data: {ex.Message}";
        }
    }

    public sealed class DedupStats
    {
        [JsonPropertyName("period_days")] public int PeriodDays { get; init; }
        [JsonPropertyName("dupes_detected")] public long DupesDetected { get; init; }
        [JsonPropertyName("dupes_skipped")] public long DupesSkipped { get; init; }
        [JsonPropertyName("dupes_appended")] public long DupesAppended { get; init; }
        [JsonPropertyName("dupes_warned")] public long DupesWarned { get; init; }
        [JsonPropertyName("supersede_count")] public long SupersedeCount { get; init; }
        [JsonPropertyName("merge_count")] public long MergeCount { get; init; }
    }

    public sealed class SupersedeResponse { public IReadOnlyList<SupersedeItem>? History { get; init; } }
    public sealed class MergeResponse { public IReadOnlyList<MergeItem>? History { get; init; } }

    public sealed class SupersedeItem
    {
        [JsonPropertyName("event_id")] public Guid EventId { get; init; }
        [JsonPropertyName("old_memory_id")] public Guid OldMemoryId { get; init; }
        [JsonPropertyName("new_memory_id")] public Guid? NewMemoryId { get; init; }
        public string? Reason { get; init; }
        [JsonPropertyName("actor_id")] public string? ActorId { get; init; }
        [JsonPropertyName("superseded_at")] public DateTimeOffset SupersededAt { get; init; }
        [JsonPropertyName("old_content_preview")] public string? OldContentPreview { get; init; }
        [JsonPropertyName("new_content_preview")] public string? NewContentPreview { get; init; }
    }

    public sealed class MergeItem
    {
        [JsonPropertyName("event_id")] public Guid EventId { get; init; }
        [JsonPropertyName("merged_memory_id")] public Guid MergedMemoryId { get; init; }
        public string? Strategy { get; init; }
        [JsonPropertyName("actor_id")] public string? ActorId { get; init; }
        [JsonPropertyName("merged_at")] public DateTimeOffset MergedAt { get; init; }
        [JsonPropertyName("content_preview")] public string? ContentPreview { get; init; }
    }
}
