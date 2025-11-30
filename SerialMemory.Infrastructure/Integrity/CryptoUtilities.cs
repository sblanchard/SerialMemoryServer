using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SerialMemory.Infrastructure.Integrity;

/// <summary>
/// Cryptographic utilities for integrity hashing.
/// Supports SHA256 and BLAKE3 algorithms.
/// </summary>
public static class CryptoUtilities
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Computes a hash using the specified algorithm.
    /// </summary>
    public static string ComputeHash(string input, string algorithm = "SHA256")
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        return algorithm.ToUpperInvariant() switch
        {
            "SHA256" => ComputeSha256(bytes),
            "BLAKE3" => ComputeBlake3(bytes),
            _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}")
        };
    }

    /// <summary>
    /// Computes SHA256 hash and returns hex-encoded string.
    /// </summary>
    public static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return ToHexString(hash);
    }

    /// <summary>
    /// Computes SHA256 hash of a string and returns hex-encoded string.
    /// </summary>
    public static string ComputeSha256(string input)
    {
        return ComputeSha256(Encoding.UTF8.GetBytes(input));
    }

    /// <summary>
    /// Computes BLAKE3 hash and returns hex-encoded string.
    /// Currently uses SHA512 as a fallback since BLAKE3 library is not installed.
    /// To use actual BLAKE3, add: PackageReference Include="Blake3" Version="1.1.0"
    /// </summary>
    public static string ComputeBlake3(byte[] data)
    {
        // BLAKE3 requires the Blake3 NuGet package which is not currently installed.
        // We use SHA512 as a higher-security fallback with a prefix to indicate BLAKE3 intent.
        // SHA512 provides 256-bit security (same as BLAKE3) and is built into .NET.
        var hash = SHA512.HashData(data);
        // Take first 32 bytes (256 bits) to match BLAKE3 output size
        return "b3:" + ToHexString(hash[..32]);
    }

    /// <summary>
    /// Computes BLAKE3 hash of a string.
    /// </summary>
    public static string ComputeBlake3(string input)
    {
        return ComputeBlake3(Encoding.UTF8.GetBytes(input));
    }

    /// <summary>
    /// Creates the canonical form of a memory for hashing.
    /// Canonical form: tenantId|memoryId|timestamp|content|sortedMetadata
    /// </summary>
    public static CanonicalMemoryForm CreateCanonicalForm(
        Guid tenantId,
        Guid memoryId,
        DateTimeOffset createdAt,
        string content,
        string? metadata,
        string? source)
    {
        // Parse and sort metadata if present
        var sortedMetadata = SortJsonObject(metadata);

        return new CanonicalMemoryForm
        {
            TenantId = tenantId,
            MemoryId = memoryId,
            Timestamp = createdAt.ToUnixTimeMilliseconds(),
            Content = content,
            Metadata = sortedMetadata,
            Source = source
        };
    }

    /// <summary>
    /// Serializes the canonical form to a string for hashing.
    /// </summary>
    public static string SerializeCanonicalForm(CanonicalMemoryForm form)
    {
        // Use a deterministic format: tenant|memory|timestamp|content|metadata|source
        var sb = new StringBuilder();
        sb.Append(form.TenantId.ToString("N")); // No dashes
        sb.Append('|');
        sb.Append(form.MemoryId.ToString("N"));
        sb.Append('|');
        sb.Append(form.Timestamp);
        sb.Append('|');
        sb.Append(form.Content ?? "");
        sb.Append('|');
        sb.Append(form.Metadata ?? "");
        sb.Append('|');
        sb.Append(form.Source ?? "");
        return sb.ToString();
    }

    /// <summary>
    /// Computes the chain hash from previous chain hash and current content hash.
    /// ChainHash = Hash(PreviousChainHash + ContentHash)
    /// </summary>
    public static string ComputeChainHash(string previousChainHash, string contentHash, string algorithm = "SHA256")
    {
        var combined = previousChainHash + contentHash;
        return ComputeHash(combined, algorithm);
    }

    /// <summary>
    /// Converts bytes to lowercase hex string.
    /// </summary>
    public static string ToHexString(byte[] bytes)
    {
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Parses hex string to bytes.
    /// </summary>
    public static byte[] FromHexString(string hex)
    {
        return Convert.FromHexString(hex);
    }

    /// <summary>
    /// Sorts a JSON object's keys recursively for deterministic serialization.
    /// </summary>
    private static string? SortJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var sorted = SortJsonElement(doc.RootElement);
            return JsonSerializer.Serialize(sorted, CanonicalJsonOptions);
        }
        catch
        {
            // If parsing fails, return original
            return json;
        }
    }

    private static object? SortJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(p => p.Name, p => SortJsonElement(p.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(SortJsonElement)
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }
}

/// <summary>
/// Canonical representation of a memory for deterministic hashing.
/// </summary>
public sealed record CanonicalMemoryForm
{
    public Guid TenantId { get; init; }
    public Guid MemoryId { get; init; }
    public long Timestamp { get; init; }
    public string? Content { get; init; }
    public string? Metadata { get; init; }
    public string? Source { get; init; }
}
