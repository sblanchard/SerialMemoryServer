using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class TimelineModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public TimelineModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public IReadOnlyList<TimelineEntry> TimelineEntries { get; set; } = [];
    public IReadOnlyList<ConfidenceDriftPoint> ConfidenceDrift { get; set; } = [];
    public MemorySnapshot? CurrentSnapshot { get; set; }
    public MemorySnapshot? HistoricalSnapshot { get; set; }
    public TimelineStats? Stats { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    [BindProperty]
    public string? MemoryId { get; set; }

    [BindProperty]
    public DateTimeOffset? SnapshotDate { get; set; }

    [BindProperty]
    public long? SnapshotSequence { get; set; }

    public async Task OnGetAsync(Guid? id, DateTimeOffset? at, long? seq)
    {
        if (id.HasValue)
        {
            MemoryId = id.Value.ToString();
            await LoadMemoryTimelineAsync(id.Value, at, seq);
        }
        else
        {
            await LoadGlobalTimelineAsync();
        }
    }

    public async Task<IActionResult> OnPostLoadTimelineAsync()
    {
        if (Guid.TryParse(MemoryId, out var id))
        {
            return RedirectToPage(new { id });
        }
        else
        {
            ErrorMessage = "Invalid memory ID format";
            await LoadGlobalTimelineAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostTimeTravelAsync()
    {
        if (!Guid.TryParse(MemoryId, out var id))
        {
            ErrorMessage = "Invalid memory ID format";
            await LoadGlobalTimelineAsync();
            return Page();
        }

        if (SnapshotSequence.HasValue)
        {
            return RedirectToPage(new { id, seq = SnapshotSequence.Value });
        }
        else if (SnapshotDate.HasValue)
        {
            return RedirectToPage(new { id, at = SnapshotDate.Value.ToString("o") });
        }
        else
        {
            ErrorMessage = "Please specify a date or sequence number";
            await LoadMemoryTimelineAsync(id, null, null);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRestoreSnapshotAsync(Guid memoryId, long sequence)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/timeline/restore", new { memoryId, sequence });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Memory restored to historical state"
                : "Failed to restore memory";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return RedirectToPage(new { id = memoryId });
    }

    public async Task<IActionResult> OnPostReplayEventsAsync(Guid memoryId, long fromSequence, long toSequence)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/timeline/replay", new { memoryId, fromSequence, toSequence });

            SuccessMessage = response.IsSuccessStatusCode
                ? $"Replayed events from {fromSequence} to {toSequence}"
                : "Failed to replay events";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return RedirectToPage(new { id = memoryId });
    }

    private async Task LoadGlobalTimelineAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");

            var timelineTask = client.GetFromJsonAsync<TimelineResponse>("/api/timeline/global?limit=100");
            var statsTask = client.GetFromJsonAsync<TimelineStats>("/api/timeline/stats");

            await Task.WhenAll(timelineTask, statsTask);

            TimelineEntries = (await timelineTask)?.Items ?? [];
            Stats = await statsTask;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load timeline: {ex.Message}";
        }
    }

    private async Task LoadMemoryTimelineAsync(Guid memoryId, DateTimeOffset? at, long? seq)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");

            var timelineTask = client.GetFromJsonAsync<TimelineResponse>($"/api/timeline/memory/{memoryId}?limit=100");
            var driftTask = client.GetFromJsonAsync<ConfidenceDriftResponse>($"/api/timeline/memory/{memoryId}/confidence-drift");
            var currentTask = client.GetFromJsonAsync<MemorySnapshot>($"/api/timeline/memory/{memoryId}/current");

            await Task.WhenAll(timelineTask, driftTask, currentTask);

            TimelineEntries = (await timelineTask)?.Items ?? [];
            ConfidenceDrift = (await driftTask)?.Points ?? [];
            CurrentSnapshot = await currentTask;

            // Load historical snapshot if requested
            if (seq.HasValue)
            {
                HistoricalSnapshot = await client.GetFromJsonAsync<MemorySnapshot>($"/api/timeline/memory/{memoryId}/at-sequence/{seq}");
            }
            else if (at.HasValue)
            {
                HistoricalSnapshot = await client.GetFromJsonAsync<MemorySnapshot>($"/api/timeline/memory/{memoryId}/at-time?timestamp={at.Value:o}");
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load memory timeline: {ex.Message}";
        }
    }

    public sealed class TimelineResponse { public IReadOnlyList<TimelineEntry>? Items { get; init; } }
    public sealed class ConfidenceDriftResponse { public IReadOnlyList<ConfidenceDriftPoint>? Points { get; init; } }

    public sealed class TimelineEntry
    {
        public long SequenceNumber { get; init; }
        public Guid MemoryId { get; init; }
        public string EventType { get; init; } = "";
        public string? Summary { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public string? ActorId { get; init; }
        public double? ConfidenceBefore { get; init; }
        public double? ConfidenceAfter { get; init; }
    }

    public sealed class ConfidenceDriftPoint
    {
        public DateTimeOffset Timestamp { get; init; }
        public double Confidence { get; init; }
        public string? EventType { get; init; }
    }

    public sealed class MemorySnapshot
    {
        public Guid MemoryId { get; init; }
        public string Content { get; init; } = "";
        public string Layer { get; init; } = "";
        public double Confidence { get; init; }
        public bool IsActive { get; init; }
        public string? ContentHash { get; init; }
        public long SequenceNumber { get; init; }
        public DateTimeOffset SnapshotAt { get; init; }
        public IReadOnlyList<string> CausalParents { get; init; } = [];
    }

    public sealed class TimelineStats
    {
        public long TotalEvents { get; init; }
        public long EventsToday { get; init; }
        public long UniqueMemories { get; init; }
        public DateTimeOffset? EarliestEvent { get; init; }
        public DateTimeOffset? LatestEvent { get; init; }
    }
}
