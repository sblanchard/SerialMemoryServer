using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class PrivacyModel : PageModel
{
    private readonly ApiClientService _apiClient;

    public PrivacyModel(ApiClientService apiClient)
    {
        _apiClient = apiClient;
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
            var client = _apiClient.CreateClient();
            var response = await client.PostAsJsonAsync("/api/privacy/audit/verify", new { memoryId = id });

            if (response.IsSuccessStatusCode)
            {
                var apiResult = await response.Content.ReadFromJsonAsync<VerifyApiResult>();
                if (apiResult != null)
                {
                    LastVerification = new VerificationResult
                    {
                        MemoryId = id,
                        IsValid = apiResult.IsValid,
                        StoredHash = apiResult.StoredHash ?? "",
                        ComputedHash = apiResult.ComputedHash ?? "",
                        Content = "",
                        VerifiedAt = DateTimeOffset.UtcNow,
                        DiscrepancyReason = apiResult.IsValid ? null : "Hash mismatch detected"
                    };
                    SuccessMessage = apiResult.IsValid
                        ? "Memory integrity verified successfully"
                        : "Memory integrity verification failed";
                }
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

    private sealed class VerifyApiResult
    {
        public bool IsValid { get; init; }
        public string? StoredHash { get; init; }
        public string? ComputedHash { get; init; }
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
            var client = _apiClient.CreateClient();
            var response = await client.PostAsJsonAsync("/api/privacy/audit/proof", new { memoryId = id });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ProofApiResult>();
                GeneratedProof = result?.ProofJson ?? result?.Proof;
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

    private sealed class ProofApiResult
    {
        public Guid ProofId { get; init; }
        public string? Proof { get; init; }
        public string? ProofJson { get; init; }
    }

    public async Task<IActionResult> OnPostRunIntegrityCheckAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();
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
            var client = _apiClient.CreateClient();
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
            var client = _apiClient.CreateClient();

            // Load audit trail and stats from actual API endpoints
            var auditTask = client.GetAsync("/api/privacy/audit?limit=100");
            var statsTask = client.GetAsync("/api/privacy/audit/stats");

            await Task.WhenAll(auditTask, statsTask);

            if (auditTask.Result.IsSuccessStatusCode)
            {
                var auditResponse = await auditTask.Result.Content.ReadFromJsonAsync<AuditApiResponse>();
                AuditTrail = auditResponse?.Items?.Select(a => new HashAuditEntry
                {
                    MemoryId = a.MemoryId,
                    Operation = a.EventType,
                    OldHash = null,
                    NewHash = a.ContentHash ?? "",
                    ActorId = a.ActorId,
                    Timestamp = a.Timestamp,
                    Reason = null
                }).ToList() ?? [];
            }

            if (statsTask.Result.IsSuccessStatusCode)
            {
                var statsResponse = await statsTask.Result.Content.ReadFromJsonAsync<AuditStatsResponse>();
                if (statsResponse != null)
                {
                    Stats = new PrivacyStats
                    {
                        TotalMemories = statsResponse.TotalMemories,
                        VerifiedCount = statsResponse.TotalMemories - (statsResponse.CorruptedCount ?? 0),
                        CorruptedCount = statsResponse.CorruptedCount ?? 0,
                        UnverifiedCount = 0,
                        IntegrityRate = statsResponse.TotalMemories > 0
                            ? (double)(statsResponse.TotalMemories - (statsResponse.CorruptedCount ?? 0)) / statsResponse.TotalMemories
                            : 1.0,
                        LastFullCheck = statsResponse.LastVerifiedAt
                    };
                }
            }

            // For integrity results, we just use the audit trail for now
            IntegrityResults = [];
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load privacy data: {ex.Message}";
        }
    }

    private sealed class AuditApiResponse
    {
        public IReadOnlyList<AuditApiItem>? Items { get; init; }
    }

    private sealed class AuditApiItem
    {
        public Guid MemoryId { get; init; }
        public string EventType { get; init; } = "";
        public string? ContentHash { get; init; }
        public string? ActorId { get; init; }
        public DateTimeOffset Timestamp { get; init; }
    }

    private sealed class AuditStatsResponse
    {
        public long TotalMemories { get; init; }
        public long? CorruptedCount { get; init; }
        public DateTimeOffset? LastVerifiedAt { get; init; }
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
