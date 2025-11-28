using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class SecurityModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _appConfig;

    public SecurityModel(ApiClientService apiClient, AppConfig appConfig)
    {
        _apiClient = apiClient;
        _appConfig = appConfig;
    }

    // Tenant Isolation
    public Guid TenantId { get; set; }
    public string RlsStatus { get; set; } = "Unknown";
    public bool GuardsActive { get; set; }
    public DateTimeOffset? LastIsolationAudit { get; set; }
    public string IsolationStatus { get; set; } = "Unknown";

    // System Integrity
    public string HashChainStatus { get; set; } = "Unknown";
    public string AuditLogStatus { get; set; } = "Unknown";
    public bool TamperDetected { get; set; }
    public string IntegrityStatus { get; set; } = "Unknown";

    // Data Residency
    public string Region { get; set; } = "Unknown";
    public string RetentionPolicy { get; set; } = "Unknown";
    public string ComplianceProof { get; set; } = "Unknown";
    public string ResidencyStatus { get; set; } = "Unknown";

    // Verification Results
    public string? LastVerificationResult { get; set; }
    public bool IsSampleData { get; set; }

    public async Task OnGetAsync()
    {
        await LoadSecurityDataAsync();
    }

    public async Task<IActionResult> OnPostVerifyAsync(string action)
    {
        await LoadSecurityDataAsync();

        try
        {
            var client = _apiClient.CreateClient();

            var response = await client.PostAsJsonAsync("/api/proof/verify", new { action });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<VerifyResponse>();
                LastVerificationResult = result?.Message ?? $"{action} verification completed successfully";
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                LastVerificationResult = $"Verification failed: {error}";
            }
        }
        catch (HttpRequestException ex)
        {
            LastVerificationResult = $"API unavailable: {ex.Message}";
        }

        return Page();
    }

    private async Task LoadSecurityDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();

            var isolationTask = client.GetAsync("/api/proof/isolation");
            var integrityTask = client.GetAsync("/api/proof/integrity");
            var residencyTask = client.GetAsync("/api/proof/residency");
            var secIntegrityTask = client.GetAsync("/api/security/integrity");
            var anomaliesTask = client.GetAsync("/api/security/anomalies");

            await Task.WhenAll(isolationTask, integrityTask, residencyTask, secIntegrityTask, anomaliesTask);

            // Process isolation response
            if (isolationTask.Result.IsSuccessStatusCode)
            {
                var isolation = await isolationTask.Result.Content.ReadFromJsonAsync<IsolationProofResponse>();
                if (isolation != null)
                {
                    TenantId = isolation.TenantId;
                    RlsStatus = isolation.RlsEnabled ? "Enabled" : "Disabled";
                    GuardsActive = isolation.GuardsActive;
                    LastIsolationAudit = isolation.LastAuditTimestamp;
                    IsolationStatus = DetermineStatus(isolation.RlsEnabled && isolation.GuardsActive, isolation.RlsEnabled || isolation.GuardsActive);
                }
            }

            // Process integrity response
            if (integrityTask.Result.IsSuccessStatusCode)
            {
                var integrity = await integrityTask.Result.Content.ReadFromJsonAsync<IntegrityProofResponse>();
                if (integrity != null)
                {
                    HashChainStatus = integrity.HashChainValid ? "Valid" : "Invalid";
                    AuditLogStatus = integrity.AuditLogIntact ? "Intact" : "Compromised";
                    TamperDetected = integrity.TamperDetected;
                    IntegrityStatus = DetermineStatus(!integrity.TamperDetected && integrity.HashChainValid, integrity.HashChainValid);
                }
            }

            // Process residency response
            if (residencyTask.Result.IsSuccessStatusCode)
            {
                var residency = await residencyTask.Result.Content.ReadFromJsonAsync<ResidencyProofResponse>();
                if (residency != null)
                {
                    Region = residency.Region;
                    RetentionPolicy = residency.RetentionPolicy;
                    ComplianceProof = residency.ComplianceStatus;
                    ResidencyStatus = DetermineStatus(residency.ComplianceStatus == "Compliant", residency.ComplianceStatus != "Non-Compliant");
                }
            }

            // Process security integrity
            if (secIntegrityTask.Result.IsSuccessStatusCode)
            {
                var secIntegrity = await secIntegrityTask.Result.Content.ReadFromJsonAsync<SecurityIntegrityResponse>();
                if (secIntegrity != null)
                {
                    // Update integrity info with real checks
                    if (secIntegrity.Failed == 0)
                    {
                        HashChainStatus = $"Valid ({secIntegrity.Checked} checked)";
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            LoadSampleData();
        }
    }

    private void LoadSampleData()
    {
        IsSampleData = true;
        TenantId = Guid.Parse("01973c88-7b2e-7000-8000-000000000001");
        RlsStatus = "Enabled";
        GuardsActive = true;
        LastIsolationAudit = DateTimeOffset.UtcNow.AddHours(-2);
        IsolationStatus = "OK";

        HashChainStatus = "Valid";
        AuditLogStatus = "Intact";
        TamperDetected = false;
        IntegrityStatus = "OK";

        // Self-hosted shows local/self-managed, SaaS shows cloud regions
        if (_appConfig.IsSelfHosted)
        {
            Region = "Local (Self-Hosted)";
            RetentionPolicy = "Self-Managed";
            ComplianceProof = "Self-Managed";
        }
        else
        {
            Region = "US-East";
            RetentionPolicy = "90 days";
            ComplianceProof = "Compliant";
        }
        ResidencyStatus = "OK";
    }

    private static string DetermineStatus(bool isOk, bool isWarning)
    {
        if (isOk) return "OK";
        if (isWarning) return "Warning";
        return "Critical";
    }

    // Response DTOs
    private sealed class IsolationProofResponse
    {
        public Guid TenantId { get; init; }
        public bool RlsEnabled { get; init; }
        public bool GuardsActive { get; init; }
        public DateTimeOffset? LastAuditTimestamp { get; init; }
    }

    private sealed class IntegrityProofResponse
    {
        public bool HashChainValid { get; init; }
        public bool AuditLogIntact { get; init; }
        public bool TamperDetected { get; init; }
    }

    private sealed class ResidencyProofResponse
    {
        public string Region { get; init; } = "";
        public string RetentionPolicy { get; init; } = "";
        public string ComplianceStatus { get; init; } = "";
    }

    private sealed class VerifyResponse
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
    }

    private sealed class SecurityIntegrityResponse
    {
        public int Checked { get; init; }
        public int Failed { get; init; }
        public string LastCheckUtc { get; init; } = "";
    }
}
