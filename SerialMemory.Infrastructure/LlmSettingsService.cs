using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;

namespace SerialMemory.Infrastructure;

/// <summary>
/// Service for managing tenant LLM settings including provider selection and API keys.
/// </summary>
public sealed class LlmSettingsService
{
    private readonly TenantDbConnectionFactory _dbFactory;
    private readonly ILogger<LlmSettingsService> _logger;
    private readonly byte[] _encryptionKey;

    public LlmSettingsService(
        TenantDbConnectionFactory dbFactory,
        ILogger<LlmSettingsService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;

        // Use environment variable or derive from machine identity
        var keyEnv = Environment.GetEnvironmentVariable("SERIAL_MEMORY_MASTER_KEY");
        _encryptionKey = !string.IsNullOrEmpty(keyEnv)
            ? Convert.FromBase64String(keyEnv)
            : SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.MachineName}:SerialMemory:LLM"));
    }

    /// <summary>
    /// Get LLM settings for a tenant.
    /// </summary>
    public async Task<LlmSettings> GetSettingsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var conn = await _dbFactory.OpenAsync(tenantId, ct);

        var settings = await conn.QueryFirstOrDefaultAsync<LlmSettingsRow>(
            """
            SELECT
                llm_provider,
                openai_key_id,
                openai_model,
                openai_embed_model,
                openai_endpoint,
                embedding_provider,
                entity_extraction_provider
            FROM tenant_settings
            WHERE tenant_id = @TenantId
            """,
            new { TenantId = tenantId });

        if (settings == null)
        {
            return new LlmSettings
            {
                TenantId = tenantId,
                LlmProvider = "ollama",
                EmbeddingProvider = "ollama",
                EntityExtractionProvider = "ollama",
                OpenAiModel = "gpt-5-nano",
                OpenAiEmbedModel = "text-embedding-3-small"
            };
        }

        // Get decrypted API key if available
        string? apiKey = null;
        if (!string.IsNullOrEmpty(settings.OpenaiKeyId))
        {
            apiKey = await GetSecretAsync(conn, tenantId, "openai_api_key", ct);
        }

        return new LlmSettings
        {
            TenantId = tenantId,
            LlmProvider = settings.LlmProvider ?? "ollama",
            OpenAiApiKey = apiKey,
            OpenAiModel = settings.OpenaiModel ?? "gpt-5-nano",
            OpenAiEmbedModel = settings.OpenaiEmbedModel ?? "text-embedding-3-small",
            OpenAiEndpoint = settings.OpenaiEndpoint,
            EmbeddingProvider = settings.EmbeddingProvider ?? "ollama",
            EntityExtractionProvider = settings.EntityExtractionProvider ?? "ollama"
        };
    }

    /// <summary>
    /// Update LLM settings for a tenant.
    /// </summary>
    public async Task UpdateSettingsAsync(Guid tenantId, LlmSettingsUpdate update, CancellationToken ct = default)
    {
        using var conn = await _dbFactory.OpenAsync(tenantId, ct);

        // Update API key if provided
        if (update.OpenAiApiKey != null)
        {
            await SetSecretAsync(conn, tenantId, "openai_api_key", update.OpenAiApiKey, ct);

            // Set key_id to indicate key is stored
            await conn.ExecuteAsync(
                """
                UPDATE tenant_settings
                SET openai_key_id = 'stored'
                WHERE tenant_id = @TenantId
                """,
                new { TenantId = tenantId });
        }

        // Build dynamic update query
        var updates = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId);

        if (update.LlmProvider != null)
        {
            updates.Add("llm_provider = @LlmProvider");
            parameters.Add("LlmProvider", update.LlmProvider);
        }

        if (update.OpenAiModel != null)
        {
            updates.Add("openai_model = @OpenAiModel");
            parameters.Add("OpenAiModel", update.OpenAiModel);
        }

        if (update.OpenAiEmbedModel != null)
        {
            updates.Add("openai_embed_model = @OpenAiEmbedModel");
            parameters.Add("OpenAiEmbedModel", update.OpenAiEmbedModel);
        }

        if (update.OpenAiEndpoint != null)
        {
            updates.Add("openai_endpoint = @OpenAiEndpoint");
            parameters.Add("OpenAiEndpoint", update.OpenAiEndpoint);
        }

        if (update.EmbeddingProvider != null)
        {
            updates.Add("embedding_provider = @EmbeddingProvider");
            parameters.Add("EmbeddingProvider", update.EmbeddingProvider);
        }

        if (update.EntityExtractionProvider != null)
        {
            updates.Add("entity_extraction_provider = @EntityExtractionProvider");
            parameters.Add("EntityExtractionProvider", update.EntityExtractionProvider);
        }

        if (updates.Count > 0)
        {
            updates.Add("updated_at = NOW()");
            var sql = $"UPDATE tenant_settings SET {string.Join(", ", updates)} WHERE tenant_id = @TenantId";
            await conn.ExecuteAsync(sql, parameters);
        }

        _logger.LogInformation("Updated LLM settings for tenant {TenantId}", tenantId);
    }

    /// <summary>
    /// Delete OpenAI API key for a tenant.
    /// </summary>
    public async Task DeleteApiKeyAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var conn = await _dbFactory.OpenAsync(tenantId, ct);

        await conn.ExecuteAsync(
            """
            DELETE FROM tenant_secrets WHERE tenant_id = @TenantId AND name = 'openai_api_key';
            UPDATE tenant_settings SET openai_key_id = NULL WHERE tenant_id = @TenantId;
            """,
            new { TenantId = tenantId });

        _logger.LogInformation("Deleted OpenAI API key for tenant {TenantId}", tenantId);
    }

    private async Task<string?> GetSecretAsync(IDbConnection conn, Guid tenantId, string name, CancellationToken ct)
    {
        var encrypted = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT encrypted_value FROM tenant_secrets WHERE tenant_id = @TenantId AND name = @Name",
            new { TenantId = tenantId, Name = name });

        if (string.IsNullOrEmpty(encrypted))
            return null;

        return Decrypt(encrypted);
    }

    private async Task SetSecretAsync(IDbConnection conn, Guid tenantId, string name, string value, CancellationToken ct)
    {
        var encrypted = Encrypt(value);

        await conn.ExecuteAsync(
            """
            INSERT INTO tenant_secrets (id, tenant_id, name, encrypted_value)
            VALUES (@Id, @TenantId, @Name, @EncryptedValue)
            ON CONFLICT (tenant_id, name)
            DO UPDATE SET encrypted_value = @EncryptedValue, updated_at = NOW()
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = name,
                EncryptedValue = encrypted
            });
    }

    private string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        // Combine IV + ciphertext
        var result = new byte[aes.IV.Length + ciphertext.Length];
        aes.IV.CopyTo(result, 0);
        ciphertext.CopyTo(result, aes.IV.Length);

        return Convert.ToBase64String(result);
    }

    private string Decrypt(string ciphertext)
    {
        var combined = Convert.FromBase64String(ciphertext);

        using var aes = Aes.Create();
        aes.Key = _encryptionKey;

        // Extract IV (first 16 bytes)
        var iv = combined[..16];
        var encrypted = combined[16..];

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        return Encoding.UTF8.GetString(plaintext);
    }

    private sealed class LlmSettingsRow
    {
        public string? LlmProvider { get; init; }
        public string? OpenaiKeyId { get; init; }
        public string? OpenaiModel { get; init; }
        public string? OpenaiEmbedModel { get; init; }
        public string? OpenaiEndpoint { get; init; }
        public string? EmbeddingProvider { get; init; }
        public string? EntityExtractionProvider { get; init; }
    }
}

/// <summary>
/// LLM settings for a tenant.
/// </summary>
public sealed class LlmSettings
{
    public Guid TenantId { get; init; }

    /// <summary>
    /// Primary LLM provider: 'ollama', 'openai', 'azure'
    /// </summary>
    public string LlmProvider { get; init; } = "ollama";

    /// <summary>
    /// OpenAI API key (decrypted, null if not set)
    /// </summary>
    public string? OpenAiApiKey { get; init; }

    /// <summary>
    /// OpenAI chat model (default: gpt-5-nano)
    /// </summary>
    public string OpenAiModel { get; init; } = "gpt-5-nano";

    /// <summary>
    /// OpenAI embedding model (default: text-embedding-3-small)
    /// </summary>
    public string OpenAiEmbedModel { get; init; } = "text-embedding-3-small";

    /// <summary>
    /// Custom endpoint for Azure OpenAI or compatible APIs
    /// </summary>
    public string? OpenAiEndpoint { get; init; }

    /// <summary>
    /// Embedding provider (can differ from chat): 'ollama', 'openai', 'azure'
    /// </summary>
    public string EmbeddingProvider { get; init; } = "ollama";

    /// <summary>
    /// Entity extraction provider: 'ollama', 'openai', 'azure'
    /// </summary>
    public string EntityExtractionProvider { get; init; } = "ollama";

    /// <summary>
    /// Check if OpenAI is configured and available
    /// </summary>
    public bool IsOpenAiAvailable => !string.IsNullOrEmpty(OpenAiApiKey);
}

/// <summary>
/// Update request for LLM settings.
/// </summary>
public sealed class LlmSettingsUpdate
{
    public string? LlmProvider { get; init; }
    public string? OpenAiApiKey { get; init; }
    public string? OpenAiModel { get; init; }
    public string? OpenAiEmbedModel { get; init; }
    public string? OpenAiEndpoint { get; init; }
    public string? EmbeddingProvider { get; init; }
    public string? EntityExtractionProvider { get; init; }
}
