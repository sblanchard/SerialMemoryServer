using System.Security.Cryptography;
using System.Text;

namespace SerialMemory.Core.Models;

/// <summary>
/// Represents an API key for programmatic tenant access.
/// API keys are stored with SHA-256 hashed values - never store plaintext!
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string KeyHash { get; set; } // SHA-256 hash of the key
    public required string KeyPrefix { get; set; } // First 8 chars for identification (e.g., "sm_live_")
    public IReadOnlyList<string> Scopes { get; set; } = Array.Empty<string>();
    public required string CreatedBy { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Checks if the API key is currently valid (not revoked, not expired).
    /// </summary>
    public bool IsValid =>
        RevokedAt == null &&
        (ExpiresAt == null || ExpiresAt > DateTimeOffset.UtcNow);
}

/// <summary>
/// Result of creating a new API key - contains the plaintext key (shown once only!)
/// </summary>
public sealed record ApiKeyCreateResult
{
    /// <summary>The API key ID</summary>
    public required Guid Id { get; init; }

    /// <summary>User-friendly name for the key</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The plaintext API key. IMPORTANT: This is shown only once!
    /// Format: sm_{env}_{random} e.g., sm_live_a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Key prefix for identification (e.g., "sm_live_")</summary>
    public required string Prefix { get; init; }

    /// <summary>Scopes granted to this key</summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>When the key expires (null = never)</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the key was created</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// API key info for listing (without the secret)
/// </summary>
public sealed record ApiKeyInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Prefix { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public bool IsActive => RevokedAt == null && (ExpiresAt == null || ExpiresAt > DateTimeOffset.UtcNow);
}

/// <summary>
/// Request to create a new API key
/// </summary>
public sealed record CreateApiKeyRequest
{
    /// <summary>Human-readable name for the key (e.g., "Production Server")</summary>
    public required string Name { get; init; }

    /// <summary>Scopes to grant (null = default scopes based on role)</summary>
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>When the key should expire (null = never)</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Signup request for creating a new tenant
/// </summary>
public sealed record SignupRequest
{
    /// <summary>Organization/tenant name</summary>
    public required string TenantName { get; init; }

    /// <summary>URL-safe slug (auto-generated if not provided)</summary>
    public string? Slug { get; init; }

    /// <summary>Primary user's email (used as user ID)</summary>
    public required string Email { get; init; }

    /// <summary>Plan to start with (default: "free")</summary>
    public string? Plan { get; init; }
}

/// <summary>
/// Result of tenant signup
/// </summary>
public sealed record SignupResult
{
    public required Guid TenantId { get; init; }
    public required string TenantName { get; init; }
    public required string Slug { get; init; }
    public required string UserId { get; init; }
    public required string Role { get; init; }
    public required string Plan { get; init; }

    /// <summary>Initial API key for the new tenant</summary>
    public required ApiKeyCreateResult ApiKey { get; init; }

    /// <summary>JWT token for immediate authentication</summary>
    public required string Token { get; init; }

    /// <summary>Token expiration</summary>
    public required DateTimeOffset TokenExpiresAt { get; init; }
}

/// <summary>
/// Tenant limits information for SDK error messages
/// </summary>
public sealed record TenantLimitsResult
{
    public required string Plan { get; init; }
    public required string DisplayName { get; init; }

    // Credit limits
    public required decimal MonthlyCredits { get; init; }
    public required decimal CreditsUsed { get; init; }
    public required decimal CreditsRemaining { get; init; }
    public required int DaysUntilReset { get; init; }
    public required DateTimeOffset CycleResetAt { get; init; }

    // Rate limits
    public required int RateLimitRpm { get; init; } // Requests per minute
    public required int RateLimitRpd { get; init; } // Requests per day

    // Hard caps
    public int? MaxMemories { get; init; }
    public int? MaxEntities { get; init; }
    public int? MaxMemorySizeBytes { get; init; }
    public int? MaxExportsPerDay { get; init; }

    // Current usage towards caps
    public required int CurrentMemories { get; init; }
    public required int CurrentEntities { get; init; }

    // Warnings
    public required IReadOnlyList<LimitWarning> Warnings { get; init; }
}

/// <summary>
/// Warning about approaching limits
/// </summary>
public sealed record LimitWarning
{
    public required string Type { get; init; } // "credits", "memories", "rate_limit"
    public required string Message { get; init; }
    public required decimal PercentUsed { get; init; }
    public required string Severity { get; init; } // "info", "warning", "critical"
}

/// <summary>
/// API key generation utilities
/// </summary>
public static class ApiKeyGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int KeyLength = 32;

    /// <summary>
    /// Generates a new API key with the format: sm_{env}_{random}
    /// The random part is 32 characters of alphanumeric.
    /// </summary>
    /// <param name="environment">Environment prefix (e.g., "live", "test", "dev")</param>
    /// <returns>Tuple of (plaintext key, SHA-256 hash, prefix)</returns>
    public static (string Key, string Hash, string Prefix) Generate(string environment = "live")
    {
        var randomPart = GenerateSecureRandom(KeyLength);
        var prefix = $"sm_{environment}_";
        var key = prefix + randomPart;
        var hash = ComputeHash(key);

        return (key, hash, prefix);
    }

    /// <summary>
    /// Computes SHA-256 hash of an API key.
    /// </summary>
    public static string ComputeHash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts the prefix from an API key (e.g., "sm_live_" from "sm_live_abc123...")
    /// </summary>
    public static string ExtractPrefix(string key)
    {
        // Format: sm_{env}_...
        var parts = key.Split('_');
        if (parts.Length >= 3)
        {
            return $"{parts[0]}_{parts[1]}_";
        }
        return key.Length >= 8 ? key[..8] : key;
    }

    private static string GenerateSecureRandom(int length)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = Alphabet[buffer[i] % Alphabet.Length];
        }
        return new string(chars);
    }
}
