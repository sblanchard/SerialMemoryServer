using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ConflictsModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public ConflictsModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public IReadOnlyList<ConflictItem> Conflicts { get; set; } = [];
    public IReadOnlyList<ContradictionItem> Contradictions { get; set; } = [];
    public IReadOnlyList<HallucinationItem> Hallucinations { get; set; } = [];
    public ConflictStats? Stats { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadConflictsDataAsync();
    }

    public async Task<IActionResult> OnPostResolveAsync(Guid conflictId, string resolution, Guid? winnerId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/power/conflicts/{conflictId}/resolve", new
            {
                resolution,
                winnerId
            });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Conflict resolved successfully"
                : "Failed to resolve conflict";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadConflictsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostKeepWinnerAsync(Guid conflictId, Guid winnerId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/power/conflicts/{conflictId}/resolve", new { winnerId, resolution = "keep_winner" });
            SuccessMessage = response.IsSuccessStatusCode ? "Winner kept successfully" : "Failed to keep winner";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadConflictsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostMergeBothAsync(Guid conflictId, string mergedContent)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/power/conflicts/{conflictId}/resolve", new { resolution = "merge", mergedContent });
            SuccessMessage = response.IsSuccessStatusCode ? "Memories merged successfully" : "Failed to merge memories";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadConflictsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDiscardBothAsync(Guid conflictId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/power/conflicts/{conflictId}/resolve", new { resolution = "discard" });
            SuccessMessage = response.IsSuccessStatusCode ? "Both memories discarded" : "Failed to discard memories";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadConflictsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDismissHallucinationAsync(Guid memoryId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            // Mark as dismissed by flagging with low priority
            var response = await client.PostAsync($"/api/mind/hallucinations/flag/{memoryId}?action=dismiss", null);
            SuccessMessage = response.IsSuccessStatusCode ? "Hallucination dismissed" : "Failed to dismiss hallucination";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadConflictsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostFlagHallucinationAsync(Guid memoryId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsync($"/api/mind/hallucinations/flag/{memoryId}", null);
            SuccessMessage = response.IsSuccessStatusCode ? "Memory flagged as hallucination" : "Failed to flag memory";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadConflictsDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRunDetectionAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            // Use the security scan endpoint to detect conflicts/contradictions
            var response = await client.PostAsJsonAsync("/api/security/scan", new { scanType = "full" });
            SuccessMessage = response.IsSuccessStatusCode ? "Detection scan started" : "Failed to start detection";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadConflictsDataAsync();
        return Page();
    }

    private async Task LoadConflictsDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");

            // Use /api/mind/* endpoints available to all authenticated users
            var contradictionsTask = client.GetFromJsonAsync<ContradictionsResponse>("/api/mind/contradictions/unresolved?limit=50");
            var hallucinationsTask = client.GetFromJsonAsync<HallucinationsResponse>("/api/mind/hallucinations/list?limit=50");
            var statsTask = LoadStatsAsync(client);

            // Try power conflicts (gracefully handle 403)
            ConflictsResponse? conflictsResult = null;
            try
            {
                var resp = await client.GetAsync("/api/power/conflicts?limit=50");
                if (resp.IsSuccessStatusCode)
                    conflictsResult = await resp.Content.ReadFromJsonAsync<ConflictsResponse>();
            }
            catch { /* Power mode not available */ }

            await Task.WhenAll(contradictionsTask, hallucinationsTask, statsTask);

            Conflicts = conflictsResult?.Items ?? [];
            Contradictions = (await contradictionsTask)?.Items ?? [];
            Hallucinations = (await hallucinationsTask)?.Items ?? [];
            Stats = await statsTask;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load conflicts data: {ex.Message}";
        }
    }

    private static async Task<ConflictStats> LoadStatsAsync(HttpClient client)
    {
        // Aggregate stats from multiple endpoints
        try
        {
            var contradictions = await client.GetFromJsonAsync<ContradictionsStatsResponse>("/api/mind/contradictions");
            var hallucinations = await client.GetFromJsonAsync<HallucinationsStatsResponse>("/api/mind/hallucinations");

            return new ConflictStats
            {
                TotalContradictions = contradictions?.TotalCount ?? 0,
                PotentialHallucinations = hallucinations?.TotalCount ?? 0,
                UnresolvedConflicts = contradictions?.UnresolvedCount ?? 0
            };
        }
        catch
        {
            return new ConflictStats();
        }
    }

    public sealed class ConflictsResponse { public IReadOnlyList<ConflictItem>? Items { get; init; } }
    public sealed class ContradictionsResponse { public IReadOnlyList<ContradictionItem>? Items { get; init; } }
    public sealed class HallucinationsResponse { public IReadOnlyList<HallucinationItem>? Items { get; init; } }
    public sealed class ContradictionsStatsResponse { public int TotalCount { get; init; } public int UnresolvedCount { get; init; } }
    public sealed class HallucinationsStatsResponse { public int TotalCount { get; init; } }

    public sealed class ConflictItem
    {
        public Guid Id { get; init; }
        public Guid MemoryAId { get; init; }
        public Guid MemoryBId { get; init; }
        public string MemoryAContent { get; init; } = "";
        public string MemoryBContent { get; init; } = "";
        public string Severity { get; init; } = "";
        public double SimilarityScore { get; init; }
        public string? Reason { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
        public bool IsResolved { get; init; }
    }

    public sealed class ContradictionItem
    {
        public Guid Id { get; init; }
        public Guid MemoryAId { get; init; }
        public Guid MemoryBId { get; init; }
        public string MemoryAContent { get; init; } = "";
        public string MemoryBContent { get; init; } = "";
        public double ConfidenceA { get; init; }
        public double ConfidenceB { get; init; }
        public string? Explanation { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
    }

    public sealed class HallucinationItem
    {
        public Guid MemoryId { get; init; }
        public string Content { get; init; } = "";
        public double Confidence { get; init; }
        public double HallucinationScore { get; init; }
        public string? Reason { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
        public bool IsIsolated { get; init; }
        public int ValidationCount { get; init; }
    }

    public sealed class ConflictStats
    {
        public int TotalConflicts { get; init; }
        public int UnresolvedConflicts { get; init; }
        public int TotalContradictions { get; init; }
        public int PotentialHallucinations { get; init; }
        public int ResolvedToday { get; init; }
        public int DetectedToday { get; init; }
    }
}
