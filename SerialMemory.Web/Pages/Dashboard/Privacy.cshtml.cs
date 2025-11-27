using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class PrivacyModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public PrivacyModel(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public IReadOnlyList<HashAuditEntry> AuditTrail { get; set; } = [];
    public IReadOnlyList<IntegrityCheckResult> IntegrityResults { get; set; } = [];
    public PrivacyStats? Stats { get; set; }
    public VerificationResult? LastVerification { get; set; }
    public string? GeneratedProof { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    [BindProperty]
    public string? MemoryIdToVerify { get; set; }

    [BindProperty]
    public string? MemoryIdForProof { get; set; }

    public async Task OnGetAsync()
    {
        await LoadPrivacyDataAsync();
    }

    public async Task<IActionResult> OnPostVerifyMemoryAsync()
    {
        if (!Guid.TryParse(MemoryIdToVerify, out var id))
        {
            ErrorMessage = "Invalid memory ID format";
            await LoadPrivacyDataAsync();
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/privacy/verify", new { memoryId = id });

            if (response.IsSuccessStatusCode)
            {
                LastVerification = await response.Content.ReadFromJsonAsync<VerificationResult>();
                SuccessMessage = LastVerification?.IsValid == true
                    ? "Memory integrity verified successfully"
                    : "Memory integrity verification failed";
            }
            else
            {
                ErrorMessage = "Failed to verify memory";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadPrivacyDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostGenerateProofAsync()
    {
        if (!Guid.TryParse(MemoryIdForProof, out var id))
        {
            ErrorMessage = "Invalid memory ID format";
            await LoadPrivacyDataAsync();
            return Page();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/privacy/generate-proof", new { memoryId = id });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ProofResult>();
                GeneratedProof = result?.Proof;
                SuccessMessage = "Proof generated successfully";
            }
            else
            {
                ErrorMessage = "Failed to generate proof";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadPrivacyDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRunIntegrityCheckAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsync("/api/privacy/integrity-check", null);

            SuccessMessage = response.IsSuccessStatusCode
                ? "Integrity check started"
                : "Failed to start integrity check";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadPrivacyDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRepairHashAsync(Guid memoryId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/privacy/repair-hash", new { memoryId });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Hash repaired successfully"
                : "Failed to repair hash";
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadPrivacyDataAsync();
        return Page();
    }

    private async Task LoadPrivacyDataAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");

            var auditTask = client.GetFromJsonAsync<AuditResponse>("/api/privacy/audit-trail?limit=100");
            var integrityTask = client.GetFromJsonAsync<IntegrityResponse>("/api/privacy/integrity-results?limit=50");
            var statsTask = client.GetFromJsonAsync<PrivacyStats>("/api/privacy/stats");

            await Task.WhenAll(auditTask, integrityTask, statsTask);

            AuditTrail = (await auditTask)?.Items ?? [];
            IntegrityResults = (await integrityTask)?.Items ?? [];
            Stats = await statsTask;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load privacy data: {ex.Message}";
        }
    }

    public sealed class AuditResponse { public IReadOnlyList<HashAuditEntry>? Items { get; init; } }
    public sealed class IntegrityResponse { public IReadOnlyList<IntegrityCheckResult>? Items { get; init; } }
    public sealed class ProofResult { public string? Proof { get; init; } }

    public sealed class HashAuditEntry
    {
        public Guid MemoryId { get; init; }
        public string Operation { get; init; } = "";
        public string? OldHash { get; init; }
        public string NewHash { get; init; } = "";
        public string? ActorId { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public string? Reason { get; init; }
    }

    public sealed class IntegrityCheckResult
    {
        public Guid MemoryId { get; init; }
        public string Content { get; init; } = "";
        public string StoredHash { get; init; } = "";
        public string ComputedHash { get; init; } = "";
        public bool IsValid { get; init; }
        public DateTimeOffset CheckedAt { get; init; }
    }

    public sealed class PrivacyStats
    {
        public long TotalMemories { get; init; }
        public long VerifiedCount { get; init; }
        public long CorruptedCount { get; init; }
        public long UnverifiedCount { get; init; }
        public double IntegrityRate { get; init; }
        public DateTimeOffset? LastFullCheck { get; init; }
    }

    public sealed class VerificationResult
    {
        public Guid MemoryId { get; init; }
        public bool IsValid { get; init; }
        public string StoredHash { get; init; } = "";
        public string ComputedHash { get; init; } = "";
        public string Content { get; init; } = "";
        public DateTimeOffset VerifiedAt { get; init; }
        public string? DiscrepancyReason { get; init; }
    }
}
