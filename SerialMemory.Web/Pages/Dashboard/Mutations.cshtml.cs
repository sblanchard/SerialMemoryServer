using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class MutationsModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MutationsModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public IReadOnlyList<PendingMutation> PendingMutations { get; set; } = [];
    public IReadOnlyList<RecentMutation> RecentMutations { get; set; } = [];
    public MutationStats? Stats { get; set; }
    public long CurrentSequence { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadMutationsDataAsync();
    }

    public async Task<IActionResult> OnPostApproveMutationAsync(Guid mutationId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/mutations/approve", new { mutationId });
            SuccessMessage = response.IsSuccessStatusCode ? "Mutation approved" : "Failed to approve mutation";
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/mutations/reject", new { mutationId, reason });
            SuccessMessage = response.IsSuccessStatusCode ? "Mutation rejected" : "Failed to reject mutation";
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsync("/api/mutations/force-flush", null);
            SuccessMessage = response.IsSuccessStatusCode ? "Mutation queue flushed" : "Failed to flush queue";
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsync("/api/mutations/pause", null);
            SuccessMessage = response.IsSuccessStatusCode ? "Mutation processing paused" : "Failed to pause processing";
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsync("/api/mutations/resume", null);
            SuccessMessage = response.IsSuccessStatusCode ? "Mutation processing resumed" : "Failed to resume processing";
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/mutations/replay", new { fromSequence });
            SuccessMessage = response.IsSuccessStatusCode ? $"Replay started from sequence {fromSequence}" : "Failed to start replay";
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
            var client = _httpClientFactory.CreateClient("Api");

            var pendingTask = client.GetFromJsonAsync<PendingMutationsResponse>("/api/mutations/pending?limit=50");
            var recentTask = client.GetFromJsonAsync<RecentMutationsResponse>("/api/mutations/recent?limit=100");
            var statsTask = client.GetFromJsonAsync<MutationStats>("/api/mutations/stats");
            var seqTask = client.GetFromJsonAsync<SequenceResponse>("/api/mutations/sequence");

            await Task.WhenAll(pendingTask, recentTask, statsTask, seqTask);

            PendingMutations = (await pendingTask)?.Items ?? [];
            RecentMutations = (await recentTask)?.Items ?? [];
            Stats = await statsTask;
            CurrentSequence = (await seqTask)?.Sequence ?? 0;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load mutations data: {ex.Message}";
        }
    }

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
