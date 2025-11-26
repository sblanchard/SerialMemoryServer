using SerialMemory.Core.Models;

namespace SerialMemory.Core.Interfaces;

/// <summary>
/// Service for managing API keys and tenant signup.
/// </summary>
public interface IApiKeyService
{
    /// <summary>
    /// Creates a new tenant and user with initial API key.
    /// </summary>
    Task<SignupResult> SignupAsync(
        SignupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new API key for a tenant.
    /// </summary>
    Task<ApiKeyCreateResult> CreateApiKeyAsync(
        Guid tenantId,
        string userId,
        CreateApiKeyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all API keys for a tenant (without secrets).
    /// </summary>
    Task<IReadOnlyList<ApiKeyInfo>> ListApiKeysAsync(
        Guid tenantId,
        bool includeRevoked = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific API key by ID.
    /// </summary>
    Task<ApiKeyInfo?> GetApiKeyAsync(
        Guid tenantId,
        Guid keyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes an API key (soft delete).
    /// </summary>
    Task<bool> RevokeApiKeyAsync(
        Guid tenantId,
        Guid keyId,
        string revokedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates an API key and returns the associated tenant info.
    /// Updates last_used_at timestamp on successful validation.
    /// </summary>
    Task<ApiKeyValidationResult?> ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets tenant limits information for SDK error messages.
    /// </summary>
    Task<TenantLimitsResult> GetTenantLimitsAsync(
        Guid tenantId,
        string workspaceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of API key validation
/// </summary>
public sealed record ApiKeyValidationResult
{
    public required Guid KeyId { get; init; }
    public required Guid TenantId { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required string CreatedBy { get; init; }
    public string? TenantSlug { get; init; }
    public string? TenantName { get; init; }
}
