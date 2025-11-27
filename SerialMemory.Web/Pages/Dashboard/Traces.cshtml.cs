using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class TracesModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public TracesModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public IReadOnlyList<MemoryTraceItem> Traces { get; set; } = [];
    public IReadOnlyList<EventLogItem> Events { get; set; } = [];
    public MemoryTraceDetail? SelectedTrace { get; set; }
    public string? SqlResult { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    [BindProperty]
    public string? MemoryId { get; set; }

    [BindProperty]
    public string? EventFilter { get; set; }

    [BindProperty]
    public string? SqlQuery { get; set; }

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

    public async Task<IActionResult> OnPostExecuteSqlAsync()
    {
        if (string.IsNullOrWhiteSpace(SqlQuery))
        {
            ErrorMessage = "SQL query cannot be empty";
            await LoadTracesAsync();
            return Page();
        }

        // Security check - only allow SELECT queries
        var trimmed = SqlQuery.Trim().ToUpperInvariant();
        if (!trimmed.StartsWith("SELECT") && !trimmed.StartsWith("EXPLAIN"))
        {
            ErrorMessage = "Only SELECT and EXPLAIN queries are allowed";
            await LoadTracesAsync();
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/traces/execute-sql", new { query = SqlQuery });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SqlResultResponse>();
                SqlResult = result?.Result ?? "No results";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"SQL error: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadTracesAsync();
        return Page();
    }

    private async Task LoadTracesAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");

            var tracesUrl = "/api/traces/recent?limit=50";
            if (!string.IsNullOrEmpty(EventFilter))
            {
                tracesUrl += $"&eventType={EventFilter}";
            }

            var tracesTask = client.GetFromJsonAsync<TracesResponse>(tracesUrl);
            var eventsTask = client.GetFromJsonAsync<EventsResponse>("/api/traces/events?limit=100");

            await Task.WhenAll(tracesTask, eventsTask);

            Traces = (await tracesTask)?.Items ?? [];
            Events = (await eventsTask)?.Items ?? [];
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
            var client = _httpClientFactory.CreateClient("Api");
            SelectedTrace = await client.GetFromJsonAsync<MemoryTraceDetail>($"/api/traces/memory/{id}");
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load trace: {ex.Message}";
        }
    }

    public sealed class TracesResponse { public IReadOnlyList<MemoryTraceItem>? Items { get; init; } }
    public sealed class EventsResponse { public IReadOnlyList<EventLogItem>? Items { get; init; } }
    public sealed class SqlResultResponse { public string? Result { get; init; } }

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
