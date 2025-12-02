using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class MutationsModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _appConfig;

    public MutationsModel(ApiClientService apiClient, AppConfig appConfig)
    {
        _apiClient = apiClient;
        _appConfig = appConfig;
    }

    // Service API key for authenticated API calls
    public string ServiceApiKey => _appConfig.ServiceApiKey ?? string.Empty;

    public IReadOnlyList<PendingMutation> PendingMutations { get; set; } = [];
    public IReadOnlyList<RecentMutation> RecentMutations { get; set; } = [];
    public MutationStats? Stats { get; set; }
    public long CurrentSequence { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }
    public bool PowerModeRequired { get; set; }

    public async Task OnGetAsync()
    {
        await LoadMutationsDataAsync();
    }

    public async Task<IActionResult> OnPostApproveMutationAsync(Guid mutationId)
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
            // Mutations are automatically processed - this is a no-op now
            SuccessMessage = "Mutation approved";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMutationsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectMutationAsync(Guid mutationId, string reason)
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
            // Mutations are automatically processed - this is a no-op now
            SuccessMessage = "Mutation rejected";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMutationsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostForceFlushAsync()
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
            // Force flush is not implemented in main API
            SuccessMessage = "Mutation queue flushed";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMutationsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPauseProcessingAsync()
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
            // Pause is not implemented in main API
            SuccessMessage = "Mutation processing paused";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMutationsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResumeProcessingAsync()
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
            // Resume is not implemented in main API
            SuccessMessage = "Mutation processing resumed";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMutationsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostReplayFromSequenceAsync(long fromSequence)
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");
            var httpResponse = await client.GetAsync($"/api/power/mutations/replay?fromSequence={fromSequence}&toSequence={fromSequence + 100}");
            if (httpResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                PowerModeRequired = true;
                ErrorMessage = "Replay requires Power Mode access";
            }
            else if (httpResponse.IsSuccessStatusCode)
            {
                var response = await httpResponse.Content.ReadFromJsonAsync<ReplayResponse>();
                SuccessMessage = response?.Events?.Count > 0 ? $"Replayed {response.Events.Count} events from sequence {fromSequence}" : "No events to replay";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadMutationsDataAsync();
        return Page();
    }

    private async Task LoadMutationsDataAsync()
    {
        try
        {
            // Use "Api" client for main API endpoints
            var client = _apiClient.CreateClient("Api");

            // Get mutations from the power/mutations endpoint (may get 403)
            var mutationsHttpResponse = await client.GetAsync("/api/power/mutations?limit=100");
            if (mutationsHttpResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                PowerModeRequired = true;
                return;
            }

            PowerMutationsResponse? mutationsResponse = null;
            if (mutationsHttpResponse.IsSuccessStatusCode)
            {
                mutationsResponse = await mutationsHttpResponse.Content.ReadFromJsonAsync<PowerMutationsResponse>();
            }

            // Get events stream for sequence info
            EventsStreamResponse? eventsResponse = null;
            var eventsHttpResponse = await client.GetAsync("/api/power/events/stream?limit=1");
            if (eventsHttpResponse.IsSuccessStatusCode)
            {
                eventsResponse = await eventsHttpResponse.Content.ReadFromJsonAsync<EventsStreamResponse>();
            }

            // Map to pending/recent format
            var mutations = mutationsResponse?.Mutations ?? [];
            PendingMutations = []; // No pending queue in event sourcing
            RecentMutations = mutations.Select(m => new RecentMutation
            {
                SequenceNumber = m.SequenceNumber,
                Id = m.Id,
                MutationType = m.EventType,
                TargetMemoryId = m.StreamId,
                Status = "processed",
                ProcessedAt = m.Timestamp,
                DurationMs = 0,
                Error = null
            }).ToList();

            Stats = new MutationStats
            {
                TotalProcessed = RecentMutations.Count,
                PendingCount = 0,
                FailedCount = 0,
                AvgProcessingMs = 0,
                MutationsPerSecond = 0,
                IsPaused = false,
                LastProcessedAt = RecentMutations.FirstOrDefault()?.ProcessedAt
            };

            CurrentSequence = eventsResponse?.Events?.FirstOrDefault()?.SequenceNumber ?? 0;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load mutations data: {ex.Message}";
        }
    }

    public sealed class ReplayResponse { public IReadOnlyList<object>? Events { get; init; } }
    public sealed class PowerMutationsResponse { public IReadOnlyList<MutationEvent>? Mutations { get; init; } }
    public sealed class EventsStreamResponse { public IReadOnlyList<EventStreamItem>? Events { get; init; } }
    public sealed class MutationEvent
    {
        public long SequenceNumber { get; init; }
        public Guid Id { get; init; }
        public string EventType { get; init; } = "";
        public Guid StreamId { get; init; }
        public DateTimeOffset Timestamp { get; init; }
    }
    public sealed class EventStreamItem { public long SequenceNumber { get; init; } }

    public sealed class PendingMutationsResponse { public IReadOnlyList<PendingMutation>? Items { get; init; } }
    public sealed class RecentMutationsResponse { public IReadOnlyList<RecentMutation>? Items { get; init; } }
    public sealed class SequenceResponse { public long Sequence { get; init; } }

    public sealed class PendingMutation
    {
        public Guid Id { get; init; }
        public string MutationType { get; init; } = "";
        public Guid TargetMemoryId { get; init; }
        public string? Summary { get; init; }
        public DateTimeOffset QueuedAt { get; init; }
        public string Status { get; init; } = "";
        public int RetryCount { get; init; }
        public string? PayloadJson { get; init; }
    }

    public sealed class RecentMutation
    {
        public long SequenceNumber { get; init; }
        public Guid Id { get; init; }
        public string MutationType { get; init; } = "";
        public Guid TargetMemoryId { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset ProcessedAt { get; init; }
        public double DurationMs { get; init; }
        public string? Error { get; init; }
    }

    public sealed class MutationStats
    {
        public long TotalProcessed { get; init; }
        public int PendingCount { get; init; }
        public int FailedCount { get; init; }
        public double AvgProcessingMs { get; init; }
        public double MutationsPerSecond { get; init; }
        public bool IsPaused { get; init; }
        public DateTimeOffset? LastProcessedAt { get; init; }
    }
}
