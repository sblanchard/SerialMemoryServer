namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for developer onboarding flow.
/// Handles tenant provisioning, API key generation, and welcome emails.
/// </summary>
public interface IDeveloperOnboardingService
{
    /// <summary>
    /// Provisions a new tenant after email verification succeeds.
    /// Creates tenant, workspace, user linkage, and generates API key.
    /// </summary>
    Task<OnboardingResult> ProvisionTenantAsync(
        string email,
        string? tenantName = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the one-time API key for display after onboarding.
    /// Only returns the key if it hasn't been viewed before.
    /// </summary>
    Task<OneTimeApiKeyResult?> GetOneTimeApiKeyAsync(
        Guid tenantId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the API key as viewed (no longer retrievable).
    /// </summary>
    Task MarkApiKeyViewedAsync(
        Guid tenantId,
        Guid keyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Regenerates API key for a tenant. Old key is revoked.
    /// </summary>
    Task<OneTimeApiKeyResult> RegenerateApiKeyAsync(
        Guid tenantId,
        string userId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates Docker run command for the tenant's API key.
    /// </summary>
    string GenerateDockerCommand(string apiKey, string? serverUrl = null);

    /// <summary>
    /// Generates MCP config JSON for Claude Desktop.
    /// </summary>
    string GenerateMcpConfig(string apiKey, string? serverUrl = null);
}

/// <summary>
/// Result of tenant onboarding.
/// </summary>
public sealed record OnboardingResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Guid TenantId { get; init; }
    public string TenantSlug { get; init; } = "";
    public Guid WorkspaceId { get; init; }
    public string UserId { get; init; } = "";
    public Guid ApiKeyId { get; init; }
    public string ApiKey { get; init; } = "";
    public string ApiKeyPrefix { get; init; } = "";
    public string DockerCommand { get; init; } = "";
    public string McpConfig { get; init; } = "";
}

/// <summary>
/// One-time API key result (displayed once only).
/// </summary>
public sealed record OneTimeApiKeyResult
{
    public Guid KeyId { get; init; }
    public string Key { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public bool WasViewed { get; init; }
    public string DockerCommand { get; init; } = "";
    public string McpConfig { get; init; } = "";
}
