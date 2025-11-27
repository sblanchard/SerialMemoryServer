using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ConflictsModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ConflictsModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/conflicts/resolve", new
            {
                conflictId,
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/conflicts/keep-winner", new { conflictId, winnerId });
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/conflicts/merge-both", new { conflictId, mergedContent });
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/conflicts/discard-both", new { conflictId });
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/conflicts/dismiss-hallucination", new { memoryId });
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/conflicts/flag-hallucination", new { memoryId });
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
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsync("/api/conflicts/run-detection", null);
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
            var client = _httpClientFactory.CreateClient("Api");

            var conflictsTask = client.GetFromJsonAsync<ConflictsResponse>("/api/conflicts/list?limit=50");
            var contradictionsTask = client.GetFromJsonAsync<ContradictionsResponse>("/api/conflicts/contradictions?limit=50");
            var hallucinationsTask = client.GetFromJsonAsync<HallucinationsResponse>("/api/conflicts/hallucinations?limit=50");
            var statsTask = client.GetFromJsonAsync<ConflictStats>("/api/conflicts/stats");

            await Task.WhenAll(conflictsTask, contradictionsTask, hallucinationsTask, statsTask);

            Conflicts = (await conflictsTask)?.Items ?? [];
            Contradictions = (await contradictionsTask)?.Items ?? [];
            Hallucinations = (await hallucinationsTask)?.Items ?? [];
            Stats = await statsTask;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load conflicts data: {ex.Message}";
        }
    }

    public sealed class ConflictsResponse { public IReadOnlyList<ConflictItem>? Items { get; init; } }
    public sealed class ContradictionsResponse { public IReadOnlyList<ContradictionItem>? Items { get; init; } }
    public sealed class HallucinationsResponse { public IReadOnlyList<HallucinationItem>? Items { get; init; } }

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
