using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class ShadowModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public ShadowModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public IReadOnlyList<ShadowBranch> Branches { get; set; } = [];
    public ShadowBranch? SelectedBranch { get; set; }
    public IReadOnlyList<BranchMemory> BranchMemories { get; set; } = [];
    public BranchDiff? Diff { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    [BindProperty]
    public string? BranchName { get; set; }

    [BindProperty]
    public string? BranchDescription { get; set; }

    [BindProperty]
    public string? MemoryContent { get; set; }

    public async Task OnGetAsync(Guid? id)
    {
        await LoadBranchesAsync();

        if (id.HasValue)
        {
            await LoadBranchDetailAsync(id.Value);
        }
    }

    public async Task<IActionResult> OnPostCreateBranchAsync()
    {
        if (string.IsNullOrWhiteSpace(BranchName))
        {
            ErrorMessage = "Branch name is required";
            await LoadBranchesAsync();
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/shadow/branches", new
            {
                name = BranchName,
                description = BranchDescription
            });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CreateBranchResult>();
                SuccessMessage = $"Branch '{BranchName}' created successfully";
                return RedirectToPage(new { id = result?.BranchId });
            }
            else
            {
                ErrorMessage = "Failed to create branch";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadBranchesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddMemoryAsync(Guid branchId)
    {
        if (string.IsNullOrWhiteSpace(MemoryContent))
        {
            ErrorMessage = "Memory content is required";
            await LoadBranchesAsync();
            await LoadBranchDetailAsync(branchId);
            return Page();
        }

        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/shadow/branches/{branchId}/memories", new
            {
                content = MemoryContent
            });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Memory added to branch"
                : "Failed to add memory";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return RedirectToPage(new { id = branchId });
    }

    public async Task<IActionResult> OnPostPromoteBranchAsync(Guid branchId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/shadow/branches/{branchId}/promote", new { });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Branch promoted to main successfully"
                : "Failed to promote branch";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDiscardBranchAsync(Guid branchId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/shadow/branches/{branchId}/discard", new { reason = "User requested discard" });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Branch discarded"
                : "Failed to discard branch";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostFreezeBranchAsync(Guid branchId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/shadow/branches/{branchId}/freeze", new { });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Branch frozen"
                : "Failed to freeze branch";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return RedirectToPage(new { id = branchId });
    }

    public async Task<IActionResult> OnPostUnfreezeBranchAsync(Guid branchId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync($"/api/shadow/branches/{branchId}/unfreeze", new { });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Branch unfrozen"
                : "Failed to unfreeze branch";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        return RedirectToPage(new { id = branchId });
    }

    private async Task LoadBranchesAsync()
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.GetFromJsonAsync<BranchesResponse>("/api/shadow/branches");
            Branches = response?.Items ?? [];
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load branches: {ex.Message}";
        }
    }

    private async Task LoadBranchDetailAsync(Guid branchId)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");

            var branchTask = client.GetFromJsonAsync<ShadowBranch>($"/api/shadow/branches/{branchId}");
            var memoriesTask = client.GetFromJsonAsync<BranchMemoriesResponse>($"/api/shadow/branches/{branchId}/memories");
            var diffTask = client.GetFromJsonAsync<BranchDiff>($"/api/shadow/branches/{branchId}/diff");

            await Task.WhenAll(branchTask, memoriesTask, diffTask);

            SelectedBranch = await branchTask;
            BranchMemories = (await memoriesTask)?.Items ?? [];
            Diff = await diffTask;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load branch details: {ex.Message}";
        }
    }

    public sealed class BranchesResponse { public IReadOnlyList<ShadowBranch>? Items { get; init; } }
    public sealed class BranchMemoriesResponse { public IReadOnlyList<BranchMemory>? Items { get; init; } }
    public sealed class CreateBranchResult { public Guid BranchId { get; init; } }

    public sealed class ShadowBranch
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? FrozenAt { get; init; }
        public int MemoryCount { get; init; }
        public string? CreatedBy { get; init; }
    }

    public sealed class BranchMemory
    {
        public Guid Id { get; init; }
        public string Content { get; init; } = "";
        public string Layer { get; init; } = "";
        public double Confidence { get; init; }
        public DateTimeOffset AddedAt { get; init; }
        public bool IsNew { get; init; }
    }

    public sealed class BranchDiff
    {
        public int AddedCount { get; init; }
        public int ModifiedCount { get; init; }
        public int DeletedCount { get; init; }
        public IReadOnlyList<DiffItem> Items { get; init; } = [];
    }

    public sealed class DiffItem
    {
        public Guid MemoryId { get; init; }
        public string DiffType { get; init; } = "";
        public string? MainContent { get; init; }
        public string? BranchContent { get; init; }
    }
}
