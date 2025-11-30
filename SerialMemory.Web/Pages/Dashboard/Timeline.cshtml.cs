using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class TimelineModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public TimelineModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
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
            var client = _apiClient.CreateClient("Api");
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
            var client = _apiClient.CreateClient("Api");
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
            var client = _apiClient.CreateClient("Api");

            // Use the correct API endpoints
            var timelineResponse = await client.GetFromJsonAsync<GlobalTimelineResponse>("/api/memory/timeline?limit=100");

            if (timelineResponse != null)
            {
                TimelineEntries = timelineResponse.Events?.Select(e => new TimelineEntry
                {
                    SequenceNumber = e.SequenceNumber,
                    MemoryId = e.MemoryId,
                    EventType = e.EventType ?? "Unknown",
                    Summary = e.Summary,
                    Timestamp = e.Timestamp,
                    ActorId = e.ActorId,
                    ConfidenceBefore = e.ConfidenceBefore,
                    ConfidenceAfter = e.ConfidenceAfter
                }).ToList() ?? [];

                Stats = new TimelineStats
                {
                    TotalEvents = timelineResponse.TotalEvents,
                    EventsToday = TimelineEntries.Count(e => e.Timestamp.Date == DateTimeOffset.UtcNow.Date),
                    UniqueMemories = TimelineEntries.Select(e => e.MemoryId).Distinct().Count(),
                    EarliestEvent = TimelineEntries.Count > 0 ? TimelineEntries.Min(e => e.Timestamp) : null,
                    LatestEvent = TimelineEntries.Count > 0 ? TimelineEntries.Max(e => e.Timestamp) : null
                };
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load timeline: {ex.Message}";
        }
    }

    public sealed class GlobalTimelineResponse
    {
        public IReadOnlyList<TimelineEvent>? Events { get; init; }
        public long TotalEvents { get; init; }
    }

    public sealed class TimelineEvent
    {
        public long SequenceNumber { get; init; }
        public Guid MemoryId { get; init; }
        public string? EventType { get; init; }
        public string? Summary { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public string? ActorId { get; init; }
        public double? ConfidenceBefore { get; init; }
        public double? ConfidenceAfter { get; init; }
    }

    private async Task LoadMemoryTimelineAsync(Guid memoryId, DateTimeOffset? at, long? seq)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");

            // Use correct API endpoints
            var timelineTask = client.GetFromJsonAsync<MemoryTimelineResponse>($"/api/memory/{memoryId}/timeline");
            var driftTask = client.GetFromJsonAsync<ConfidenceDriftResponse>($"/api/memory/{memoryId}/confidence-drift");

            await Task.WhenAll(timelineTask, driftTask);

            var timelineResponse = await timelineTask;
            if (timelineResponse != null)
            {
                TimelineEntries = timelineResponse.Events?.Select(e => new TimelineEntry
                {
                    SequenceNumber = e.SequenceNumber,
                    MemoryId = memoryId,
                    EventType = e.EventType ?? "Unknown",
                    Summary = e.Summary,
                    Timestamp = e.Timestamp,
                    ActorId = e.ActorId,
                    ConfidenceBefore = e.ConfidenceBefore,
                    ConfidenceAfter = e.ConfidenceAfter
                }).ToList() ?? [];

                // Set current snapshot from the timeline response
                if (timelineResponse.CurrentState != null)
                {
                    CurrentSnapshot = new MemorySnapshot
                    {
                        MemoryId = memoryId,
                        Content = timelineResponse.CurrentState.Content ?? "",
                        Layer = timelineResponse.CurrentState.Layer ?? "L0_RAW",
                        Confidence = timelineResponse.CurrentState.Confidence,
                        IsActive = timelineResponse.CurrentState.IsActive,
                        ContentHash = timelineResponse.CurrentState.ContentHash,
                        SequenceNumber = timelineResponse.CurrentState.SequenceNumber,
                        SnapshotAt = DateTimeOffset.UtcNow
                    };
                }
            }

            var driftResponse = await driftTask;
            if (driftResponse?.Points != null)
            {
                ConfidenceDrift = driftResponse.Points.Select(p => new ConfidenceDriftPoint
                {
                    Timestamp = p.Timestamp,
                    Confidence = p.Confidence,
                    EventType = p.EventType
                }).ToList();
            }

            // Historical snapshot - use replay endpoint if available
            if (seq.HasValue)
            {
                try
                {
                    var replayResponse = await client.GetFromJsonAsync<ReplayResponse>($"/api/memory/{memoryId}/replay?atSequence={seq}");
                    if (replayResponse != null)
                    {
                        HistoricalSnapshot = new MemorySnapshot
                        {
                            MemoryId = memoryId,
                            Content = replayResponse.Content ?? "",
                            Layer = replayResponse.Layer ?? "L0_RAW",
                            Confidence = replayResponse.Confidence,
                            IsActive = replayResponse.IsActive,
                            ContentHash = replayResponse.ContentHash,
                            SequenceNumber = seq.Value,
                            SnapshotAt = replayResponse.ReconstructedAt
                        };
                    }
                }
                catch { /* Historical snapshot is optional */ }
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load memory timeline: {ex.Message}";
        }
    }

    public sealed class MemoryTimelineResponse
    {
        public Guid MemoryId { get; init; }
        public IReadOnlyList<TimelineEvent>? Events { get; init; }
        public MemoryCurrentState? CurrentState { get; init; }
    }

    public sealed class MemoryCurrentState
    {
        public string? Content { get; init; }
        public string? Layer { get; init; }
        public double Confidence { get; init; }
        public bool IsActive { get; init; }
        public string? ContentHash { get; init; }
        public long SequenceNumber { get; init; }
    }

    public sealed class ReplayResponse
    {
        public string? Content { get; init; }
        public string? Layer { get; init; }
        public double Confidence { get; init; }
        public bool IsActive { get; init; }
        public string? ContentHash { get; init; }
        public DateTimeOffset ReconstructedAt { get; init; }
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
