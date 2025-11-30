using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class TracesModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public TracesModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public IReadOnlyList<MemoryTraceItem> Traces { get; set; } = [];
    public IReadOnlyList<EventLogItem> Events { get; set; } = [];
    public MemoryTraceDetail? SelectedTrace { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }
    public bool PowerModeRequired { get; set; }

    [BindProperty]
    public string? MemoryId { get; set; }

    [BindProperty]
    public string? EventFilter { get; set; }

    public async Task OnGetAsync(Guid? id, string? eventFilter)
    {
        EventFilter = eventFilter;
        await LoadTracesAsync();

        if (id.HasValue)
        {
            await LoadTraceDetailAsync(id.Value);
        }
    }

    public async Task<IActionResult> OnPostLoadTraceAsync()
    {
        if (Guid.TryParse(MemoryId, out var id))
        {
            await LoadTraceDetailAsync(id);
        }
        else
        {
            ErrorMessage = "Invalid memory ID format";
        }

        await LoadTracesAsync();
        return Page();
    }

    private async Task LoadTracesAsync()
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");

            // Use the correct power events endpoint
            var eventsUrl = string.IsNullOrEmpty(EventFilter)
                ? "/api/power/events/stream?limit=100"
                : $"/api/power/events/type/{EventFilter}?limit=100";

            // Try power endpoints (may get 403 for non-power users)
            var memoriesResponse = await client.GetAsync("/api/power/recent?limit=50");
            if (memoriesResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                PowerModeRequired = true;
                return;
            }

            var eventsResponse = await client.GetAsync(eventsUrl);

            if (memoriesResponse.IsSuccessStatusCode)
            {
                var memoriesResult = await memoriesResponse.Content.ReadFromJsonAsync<MemoriesResponse>();
                var memories = memoriesResult?.Items ?? [];
                Traces = memories.Select(m => new MemoryTraceItem
                {
                    MemoryId = m.Id,
                    Content = m.Content ?? "",
                    EventCount = 0,
                    LastEventType = "unknown",
                    LastEventAt = m.UpdatedAt ?? m.CreatedAt,
                    IsActive = m.IsActive
                }).ToList();
            }

            if (eventsResponse.IsSuccessStatusCode)
            {
                var eventsResult = await eventsResponse.Content.ReadFromJsonAsync<EventsResponse>();
                Events = eventsResult?.Items ?? [];
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load traces: {ex.Message}";
        }
    }

    private async Task LoadTraceDetailAsync(Guid id)
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
            var response = await client.GetAsync($"/api/power/trace/{id}");
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                PowerModeRequired = true;
                return;
            }
            if (response.IsSuccessStatusCode)
            {
                SelectedTrace = await response.Content.ReadFromJsonAsync<MemoryTraceDetail>();
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load trace: {ex.Message}";
        }
    }

    public sealed class MemoriesResponse { public IReadOnlyList<MemoryItem>? Items { get; init; } }
    public sealed class MemoryItem
    {
        public Guid Id { get; init; }
        public string? Content { get; init; }
        public bool IsActive { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }

    public sealed class TracesResponse { public IReadOnlyList<MemoryTraceItem>? Items { get; init; } }
    public sealed class EventsResponse { public IReadOnlyList<EventLogItem>? Items { get; init; } }

    public sealed class MemoryTraceItem
    {
        public Guid MemoryId { get; init; }
        public string Content { get; init; } = "";
        public int EventCount { get; init; }
        public string LastEventType { get; init; } = "";
        public DateTimeOffset LastEventAt { get; init; }
        public bool IsActive { get; init; }
    }

    public sealed class EventLogItem
    {
        public long SequenceNumber { get; init; }
        public string EventType { get; init; } = "";
        public Guid StreamId { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public string? ActorId { get; init; }
        public string? Summary { get; init; }
    }

    public sealed class MemoryTraceDetail
    {
        public Guid MemoryId { get; init; }
        public string Content { get; init; } = "";
        public string Layer { get; init; } = "";
        public double Confidence { get; init; }
        public bool IsActive { get; init; }
        public string? ContentHash { get; init; }
        public IReadOnlyList<TraceEvent> Events { get; init; } = [];
        public IReadOnlyList<Guid> CausalParents { get; init; } = [];
        public IReadOnlyList<Guid> Descendants { get; init; } = [];
        public string RawJson { get; init; } = "{}";
    }

    public sealed class TraceEvent
    {
        public long SequenceNumber { get; init; }
        public string EventType { get; init; } = "";
        public DateTimeOffset Timestamp { get; init; }
        public string? ActorId { get; init; }
        public string? Reason { get; init; }
        public string PayloadJson { get; init; } = "{}";
    }
}
