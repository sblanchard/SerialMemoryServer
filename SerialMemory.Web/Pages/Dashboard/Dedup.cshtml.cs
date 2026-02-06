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
        public int PeriodDays { get; init; }
        public long DupesDetected { get; init; }
        public long DupesSkipped { get; init; }
        public long DupesAppended { get; init; }
        public long DupesWarned { get; init; }
        public long SupersedeCount { get; init; }
        public long MergeCount { get; init; }
    }

    public sealed class SupersedeResponse { public IReadOnlyList<SupersedeItem>? History { get; init; } }
    public sealed class MergeResponse { public IReadOnlyList<MergeItem>? History { get; init; } }

    public sealed class SupersedeItem
    {
        public Guid EventId { get; init; }
        public Guid OldMemoryId { get; init; }
        public Guid? NewMemoryId { get; init; }
        public string? Reason { get; init; }
        public string? ActorId { get; init; }
        public DateTimeOffset SupersededAt { get; init; }
        public string? OldContentPreview { get; init; }
        public string? NewContentPreview { get; init; }
    }

    public sealed class MergeItem
    {
        public Guid EventId { get; init; }
        public Guid MergedMemoryId { get; init; }
        public string? Strategy { get; init; }
        public string? ActorId { get; init; }
        public DateTimeOffset MergedAt { get; init; }
        public string? ContentPreview { get; init; }
    }
}
