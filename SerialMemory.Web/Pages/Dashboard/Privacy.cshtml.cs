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
    public PrivacySettings? Settings { get; set; }
    public VerificationResult? LastVerification { get; set; }
    public ChainVerificationResult? LastChainVerification { get; set; }
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
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsync($"/api/integrity/verify/{id}", null);

            if (response.IsSuccessStatusCode)
            {
                var apiResult = await response.Content.ReadFromJsonAsync<IntegrityVerifyResult>();
                if (apiResult != null)
                {
                    LastVerification = new VerificationResult
                    {
                        MemoryId = id,
                        IsValid = apiResult.IsValid,
                        StoredHash = apiResult.ActualContentHash ?? "",
                        ComputedHash = apiResult.ExpectedContentHash ?? "",
                        Content = "",
                        VerifiedAt = DateTimeOffset.UtcNow,
                        DiscrepancyReason = apiResult.FailureReason
                    };
                    SuccessMessage = apiResult.IsValid
                        ? "Memory integrity verified successfully"
                        : "Memory integrity verification failed";
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed to verify memory: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"API error: {ex.Message}";
        }

        await LoadPrivacyDataAsync();
        return Page();
    }

    private sealed class IntegrityVerifyResult
    {
        public Guid MemoryId { get; init; }
        public bool IsValid { get; init; }
        public string? Status { get; init; }
        public string? ExpectedContentHash { get; init; }
        public string? ActualContentHash { get; init; }
        public string? ExpectedChainHash { get; init; }
        public string? ActualChainHash { get; init; }
        public string? FailureReason { get; init; }
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
            var client = _apiClient.CreateClient("Api");
            var response = await client.GetAsync($"/api/integrity/proof/{id}");

            if (response.IsSuccessStatusCode)
            {
                var proof = await response.Content.ReadAsStringAsync();
                GeneratedProof = proof;
                SuccessMessage = "Proof retrieved successfully";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Try to compute the proof first
                var computeResponse = await client.PostAsync($"/api/integrity/compute/{id}", null);
                if (computeResponse.IsSuccessStatusCode)
                {
                    var proof = await computeResponse.Content.ReadAsStringAsync();
                    GeneratedProof = proof;
                    SuccessMessage = "Proof computed and retrieved successfully";
                }
                else
                {
                    ErrorMessage = "Memory not found or proof could not be computed";
                }
            }
            else
            {
                ErrorMessage = "Failed to retrieve proof";
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
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsync("/api/integrity/verify-all", null);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ChainVerificationResult>();
                if (result != null)
                {
                    LastChainVerification = result;
                    SuccessMessage = result.IsValid
                        ? $"Integrity check completed: {result.VerifiedCount} memories verified successfully"
                        : $"Integrity check completed with issues: {result.InvalidCount} invalid, {result.OrphanCount} orphaned";
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Failed to start integrity check: {error}";
            }
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
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsync($"/api/integrity/compute/{memoryId}", null);

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

    public async Task<IActionResult> OnPostUpdateSettingsAsync(
        bool enablePrivacyMode,
        bool enableHashVerification,
        bool enableAuditLogs,
        string hashAlgorithm,
        int autoVerifyIntervalHours)
    {
        try
        {
            var client = _apiClient.CreateClient("Api");
            var response = await client.PostAsJsonAsync("/api/privacy/settings", new
            {
                enablePrivacyMode,
                enableHashVerification,
                enableAuditLogs,
                hashAlgorithm,
                autoVerifyIntervalHours
            });

            SuccessMessage = response.IsSuccessStatusCode
                ? "Settings updated successfully"
                : "Failed to update settings";
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
            var client = _apiClient.CreateClient("Api");

            // Load all data in parallel
            var statsTask = client.GetAsync("/api/integrity/stats");
            var settingsTask = client.GetAsync("/api/privacy/settings");
            var auditTask = client.GetAsync("/api/privacy/audit?pageSize=50");

            await Task.WhenAll(statsTask, settingsTask, auditTask);

            // Parse stats
            if (statsTask.Result.IsSuccessStatusCode)
            {
                var statsResponse = await statsTask.Result.Content.ReadFromJsonAsync<StatsApiResponse>();
                if (statsResponse != null)
                {
                    Stats = new PrivacyStats
                    {
                        TotalMemories = statsResponse.TotalMemories,
                        VerifiedCount = statsResponse.VerifiedCount,
                        CorruptedCount = statsResponse.CorruptedCount,
                        UnverifiedCount = statsResponse.PendingCount,
                        IntegrityRate = statsResponse.IntegrityRate,
                        LastFullCheck = statsResponse.LastFullCheck
                    };
                }
            }

            // Parse settings
            if (settingsTask.Result.IsSuccessStatusCode)
            {
                var settingsResponse = await settingsTask.Result.Content.ReadFromJsonAsync<SettingsApiResponse>();
                if (settingsResponse != null)
                {
                    Settings = new PrivacySettings
                    {
                        EnablePrivacyMode = settingsResponse.EnablePrivacyMode,
                        EnableHashVerification = settingsResponse.EnableHashVerification,
                        EnableAuditLogs = settingsResponse.EnableAuditLogs,
                        HashAlgorithm = settingsResponse.HashAlgorithm,
                        AutoVerifyIntervalHours = settingsResponse.AutoVerifyIntervalHours
                    };
                }
            }

            // Parse audit trail
            if (auditTask.Result.IsSuccessStatusCode)
            {
                var auditResponse = await auditTask.Result.Content.ReadFromJsonAsync<AuditPagedResponse>();
                AuditTrail = auditResponse?.Items?.Select(a => new HashAuditEntry
                {
                    MemoryId = a.MemoryId ?? Guid.Empty,
                    Operation = a.Action,
                    OldHash = null,
                    NewHash = "",
                    ActorId = a.UserId,
                    Timestamp = a.Timestamp,
                    Reason = null
                }).ToList() ?? [];
            }

            // Set defaults if not loaded
            Stats ??= new PrivacyStats();
            Settings ??= new PrivacySettings
            {
                EnableHashVerification = true,
                EnableAuditLogs = true,
                HashAlgorithm = "SHA256",
                AutoVerifyIntervalHours = 24
            };
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Failed to load privacy data: {ex.Message}";
            Stats ??= new PrivacyStats();
            Settings ??= new PrivacySettings
            {
                EnableHashVerification = true,
                EnableAuditLogs = true,
                HashAlgorithm = "SHA256",
                AutoVerifyIntervalHours = 24
            };
        }
    }

    private sealed class StatsApiResponse
    {
        public long TotalMemories { get; init; }
        public long VerifiedCount { get; init; }
        public long CorruptedCount { get; init; }
        public long PendingCount { get; init; }
        public double IntegrityRate { get; init; }
        public DateTimeOffset? LastFullCheck { get; init; }
        public string? LastRunStatus { get; init; }
    }

    private sealed class SettingsApiResponse
    {
        public Guid TenantId { get; init; }
        public bool EnablePrivacyMode { get; init; }
        public bool EnableHashVerification { get; init; }
        public bool EnableAuditLogs { get; init; }
        public string HashAlgorithm { get; init; } = "SHA256";
        public int AutoVerifyIntervalHours { get; init; }
    }

    private sealed class AuditPagedResponse
    {
        public IReadOnlyList<AuditEntryApiItem>? Items { get; init; }
        public int TotalCount { get; init; }
        public int Page { get; init; }
        public int PageSize { get; init; }
    }

    private sealed class AuditEntryApiItem
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public string? UserId { get; init; }
        public string Action { get; init; } = "";
        public Guid? MemoryId { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
        public bool? IntegrityValid { get; init; }
    }

    public sealed class ChainVerificationResult
    {
        public Guid TenantId { get; init; }
        public Guid VerificationRunId { get; init; }
        public bool IsValid { get; init; }
        public int TotalMemories { get; init; }
        public int VerifiedCount { get; init; }
        public int InvalidCount { get; init; }
        public int OrphanCount { get; init; }
        public int PendingCount { get; init; }
        public IReadOnlyList<Guid>? InvalidMemoryIds { get; init; }
        public IReadOnlyList<Guid>? OrphanMemoryIds { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset CompletedAt { get; init; }
        public string? ErrorMessage { get; init; }
    }

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

    public sealed class PrivacySettings
    {
        public bool EnablePrivacyMode { get; init; }
        public bool EnableHashVerification { get; init; }
        public bool EnableAuditLogs { get; init; }
        public string HashAlgorithm { get; init; } = "SHA256";
        public int AutoVerifyIntervalHours { get; init; }
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
