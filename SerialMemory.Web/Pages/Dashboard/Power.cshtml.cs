using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class PowerModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public PowerModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public MemoryDetail? SelectedMemory { get; set; }
    public IReadOnlyList<MemorySummary> RecentMemories { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    [BindProperty]
    public string? MemoryId { get; set; }

    [BindProperty]
    public string? NewContent { get; set; }

    [BindProperty]
    public string? BatchAction { get; set; }

    [BindProperty]
    public string? BatchMemoryIds { get; set; }

    [BindProperty]
    public string? MergedContent { get; set; }

    public async Task OnGetAsync(Guid? id)
    {
        await LoadRecentMemoriesAsync();

        if (id.HasValue)
        {
            await LoadMemoryAsync(id.Value);
        }
    }

    public async Task<IActionResult> OnPostLoadMemoryAsync()
    {
        await LoadRecentMemoriesAsync();

        if (Guid.TryParse(MemoryId, out var id))
        {
            await LoadMemoryAsync(id);
        }
        else
        {
            ErrorMessage = "Invalid memory ID format";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostUpdateContentAsync()
    {
        if (!Guid.TryParse(MemoryId, out var id))
        {
            ErrorMessage = "Invalid memory ID";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(NewContent))
        {
            ErrorMessage = "Content cannot be empty";
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsJsonAsync("/api/power/update-content", new
            {
                memoryId = id,
                newContent = NewContent,
                reason = "Manual update via Power Mode"
            });

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = "Memory content updated successfully";
                await LoadMemoryAsync(id);
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed to update: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadRecentMemoriesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteMemoryAsync()
    {
        if (!Guid.TryParse(MemoryId, out var id))
        {
            ErrorMessage = "Invalid memory ID";
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsJsonAsync("/api/power/delete-memory", new
            {
                memoryId = id,
                reason = "Manual deletion via Power Mode"
            });

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = "Memory deleted successfully";
                SelectedMemory = null;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed to delete: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadRecentMemoriesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostBatchActionAsync()
    {
        if (string.IsNullOrWhiteSpace(BatchMemoryIds))
        {
            ErrorMessage = "No memory IDs provided";
            return Page();
        }

        var ids = BatchMemoryIds.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => Guid.TryParse(s, out _))
            .Select(Guid.Parse)
            .ToList();

        if (ids.Count == 0)
        {
            ErrorMessage = "No valid memory IDs found";
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient();
            var response = BatchAction switch
            {
                "delete" => await client.PostAsJsonAsync("/api/power/batch-delete", new { memoryIds = ids, reason = "Batch delete via Power Mode" }),
                "decay" => await client.PostAsJsonAsync("/api/power/batch-decay", new { memoryIds = ids }),
                "reinforce" => await client.PostAsJsonAsync("/api/power/batch-reinforce", new { memoryIds = ids }),
                "archive" => await client.PostAsJsonAsync("/api/power/batch-archive", new { memoryIds = ids }),
                "merge" => await client.PostAsJsonAsync("/api/power/merge-memories", new { memoryIds = ids, mergedContent = MergedContent }),
                _ => throw new InvalidOperationException("Unknown batch action")
            };

            if (response.IsSuccessStatusCode)
            {
                SuccessMessage = $"Batch {BatchAction} completed on {ids.Count} memories";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Batch operation failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadRecentMemoriesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostForceReembedAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsync("/api/power/force-reembed", null);

            SuccessMessage = response.IsSuccessStatusCode
                ? "Re-embedding job queued successfully"
                : "Failed to queue re-embedding job";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadRecentMemoriesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRecrawlEntitiesAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.PostAsync("/api/power/recrawl-entities", null);

            SuccessMessage = response.IsSuccessStatusCode
                ? "Entity recrawl job queued successfully"
                : "Failed to queue entity recrawl job";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadRecentMemoriesAsync();
        return Page();
    }

    private async Task LoadRecentMemoriesAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
            var response = await client.GetFromJsonAsync<RecentMemoriesResponse>("/api/power/recent?limit=50");
            RecentMemories = response?.Memories ?? [];
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load recent memories: {ex.Message}";
        }
    }

    private async Task LoadMemoryAsync(Guid id)
    {
        try
        {
            var client = _apiClient.CreateClient();
            SelectedMemory = await client.GetFromJsonAsync<MemoryDetail>($"/api/power/memory/{id}");
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load memory: {ex.Message}";
        }
    }

    public sealed class RecentMemoriesResponse
    {
        public IReadOnlyList<MemorySummary>? Memories { get; init; }
    }

    public sealed class MemorySummary
    {
        public Guid Id { get; init; }
        public string Content { get; init; } = "";
        public string Layer { get; init; } = "";
        public double Confidence { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public bool IsActive { get; init; }
    }

    public sealed class MemoryDetail
    {
        public Guid Id { get; init; }
        public string Content { get; init; } = "";
        public string Layer { get; init; } = "";
        public double Confidence { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastReinforcedAt { get; init; }
        public int HalfLifeDays { get; init; }
        public bool IsActive { get; init; }
        public string? Source { get; init; }
        public string? ContentHash { get; init; }
        public IReadOnlyList<string> CausalParents { get; init; } = [];
        public IReadOnlyList<EntityLink> Entities { get; init; } = [];
        public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    }

    public sealed class EntityLink
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
    }
}
