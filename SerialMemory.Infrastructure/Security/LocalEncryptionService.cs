using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.Security;

public sealed class LocalEncryptionService : ILocalEncryption
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<LocalEncryptionService> _logger;
    private readonly byte[] _masterKey;

    private const string DefaultAlgorithm = "AES-256-GCM";
    private const int KeySizeBytes = 32;
    private const int IvSizeBytes = 12;
    private const int TagSizeBytes = 16;

    public LocalEncryptionService(
        NpgsqlDataSource dataSource,
        ILogger<LocalEncryptionService> logger,
        byte[]? masterKey = null)
    {
        _dataSource = dataSource;
        _logger = logger;

        // Master key from environment or generate deterministically from machine identity
        _masterKey = masterKey ?? GetOrCreateMasterKey();
    }

    private static byte[] GetOrCreateMasterKey()
    {
        // Try to load from environment
        var envKey = Environment.GetEnvironmentVariable("SERIAL_MEMORY_MASTER_KEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            return Convert.FromBase64String(envKey);
        }

        // Generate deterministic key from machine identity (for local development)
        // In production, use proper key management
        var machineId = $"{Environment.MachineName}:{Environment.UserName}:SerialMemory";
        return SHA256.HashData(Encoding.UTF8.GetBytes(machineId));
    }

    public async Task<EncryptionKey> CreateKeyAsync(
        string keyName,
        CancellationToken cancellationToken = default)
    {
        // Generate new data encryption key
        var dataKey = RandomNumberGenerator.GetBytes(KeySizeBytes);

        // Encrypt the data key with master key
        var encryptedKey = EncryptKeyWithMaster(dataKey);

        var keyId = Guid.CreateVersion7();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            INSERT INTO encryption_keys (
                key_id, key_name, encrypted_key, key_algorithm,
                key_version, is_active
            ) VALUES (
                @KeyId, @KeyName, @EncryptedKey, @KeyAlgorithm,
                1, TRUE
            )
            RETURNING key_id, key_name, key_algorithm, key_version, created_at, rotated_at, is_active";

        var row = await connection.QuerySingleAsync<KeyRow>(sql, new
        {
            KeyId = keyId,
            KeyName = keyName,
            EncryptedKey = encryptedKey,
            KeyAlgorithm = DefaultAlgorithm
        });

        _logger.LogInformation("Created encryption key {KeyName} ({KeyId})", keyName, keyId);

        // Clear the unencrypted key from memory
        Array.Clear(dataKey, 0, dataKey.Length);

        return new EncryptionKey(
            row.key_id,
            row.key_name,
            row.key_algorithm,
            row.key_version,
            row.created_at,
            row.rotated_at,
            row.is_active);
    }

    public async Task<EncryptionKey?> GetActiveKeyAsync(
        string keyName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT key_id, key_name, key_algorithm, key_version,
                   created_at, rotated_at, is_active
            FROM encryption_keys
            WHERE key_name = @KeyName AND is_active = TRUE
            ORDER BY key_version DESC
            LIMIT 1";

        var row = await connection.QuerySingleOrDefaultAsync<KeyRow>(sql, new { KeyName = keyName });

        if (row == null)
        {
            return null;
        }

        return new EncryptionKey(
            row.key_id,
            row.key_name,
            row.key_algorithm,
            row.key_version,
            row.created_at,
            row.rotated_at,
            row.is_active);
    }

    public async Task RotateKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Get current active key
            const string getCurrentSql = @"
                SELECT key_id, key_version, encrypted_key
                FROM encryption_keys
                WHERE key_name = @KeyName AND is_active = TRUE
                ORDER BY key_version DESC
                LIMIT 1";

            var current = await connection.QuerySingleOrDefaultAsync<(Guid key_id, int key_version, byte[] encrypted_key)>(
                getCurrentSql,
                new { KeyName = keyName },
                transaction);

            if (current == default)
            {
                throw new InvalidOperationException($"No active key found for {keyName}");
            }

            // Deactivate current key
            const string deactivateSql = @"
                UPDATE encryption_keys
                SET is_active = FALSE, rotated_at = NOW()
                WHERE key_id = @KeyId";

            await connection.ExecuteAsync(deactivateSql, new { KeyId = current.key_id }, transaction);

            // Generate new key
            var newDataKey = RandomNumberGenerator.GetBytes(KeySizeBytes);
            var newEncryptedKey = EncryptKeyWithMaster(newDataKey);
            var newKeyId = Guid.CreateVersion7();

            const string insertNewSql = @"
                INSERT INTO encryption_keys (
                    key_id, key_name, encrypted_key, key_algorithm,
                    key_version, is_active
                ) VALUES (
                    @KeyId, @KeyName, @EncryptedKey, @KeyAlgorithm,
                    @KeyVersion, TRUE
                )";

            await connection.ExecuteAsync(insertNewSql, new
            {
                KeyId = newKeyId,
                KeyName = keyName,
                EncryptedKey = newEncryptedKey,
                KeyAlgorithm = DefaultAlgorithm,
                KeyVersion = current.key_version + 1
            }, transaction);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Rotated encryption key {KeyName} from version {OldVersion} to {NewVersion}",
                keyName, current.key_version, current.key_version + 1);

            Array.Clear(newDataKey, 0, newDataKey.Length);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<EncryptedData> EncryptAsync(
        byte[] plaintext,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        // Get the encryption key
        var dataKey = await GetDecryptedKeyAsync(keyId, cancellationToken);

        try
        {
            // Generate IV
            var iv = RandomNumberGenerator.GetBytes(IvSizeBytes);

            // Encrypt using AES-GCM
            using var aesGcm = new AesGcm(dataKey, TagSizeBytes);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSizeBytes];

            aesGcm.Encrypt(iv, plaintext, ciphertext, tag);

            // Combine ciphertext and tag
            var combined = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);

            // Compute content hash
            var contentHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

            return new EncryptedData(combined, iv, keyId, DefaultAlgorithm, contentHash);
        }
        finally
        {
            Array.Clear(dataKey, 0, dataKey.Length);
        }
    }

    public async Task<byte[]> DecryptAsync(
        EncryptedData encryptedData,
        CancellationToken cancellationToken = default)
    {
        // Get the encryption key
        var dataKey = await GetDecryptedKeyAsync(encryptedData.KeyId, cancellationToken);

        try
        {
            // Split ciphertext and tag
            var ciphertext = new byte[encryptedData.Ciphertext.Length - TagSizeBytes];
            var tag = new byte[TagSizeBytes];

            Buffer.BlockCopy(encryptedData.Ciphertext, 0, ciphertext, 0, ciphertext.Length);
            Buffer.BlockCopy(encryptedData.Ciphertext, ciphertext.Length, tag, 0, TagSizeBytes);

            // Decrypt using AES-GCM
            using var aesGcm = new AesGcm(dataKey, TagSizeBytes);

            var plaintext = new byte[ciphertext.Length];
            aesGcm.Decrypt(encryptedData.Iv, ciphertext, tag, plaintext);

            // Verify content hash
            var contentHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
            if (contentHash != encryptedData.ContentHash)
            {
                throw new CryptographicException("Content hash verification failed");
            }

            return plaintext;
        }
        finally
        {
            Array.Clear(dataKey, 0, dataKey.Length);
        }
    }

    public async Task<Guid> StoreEncryptedMemoryAsync(
        Guid memoryId,
        string content,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var encrypted = await EncryptAsync(contentBytes, keyId, cancellationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            INSERT INTO encrypted_memories (
                memory_id, encrypted_content, encryption_key_id,
                encryption_algorithm, iv, content_hash
            ) VALUES (
                @MemoryId, @EncryptedContent, @KeyId,
                @Algorithm, @Iv, @ContentHash
            )
            ON CONFLICT (memory_id) DO UPDATE SET
                encrypted_content = EXCLUDED.encrypted_content,
                encryption_key_id = EXCLUDED.encryption_key_id,
                encryption_algorithm = EXCLUDED.encryption_algorithm,
                iv = EXCLUDED.iv,
                content_hash = EXCLUDED.content_hash,
                created_at = NOW()";

        await connection.ExecuteAsync(sql, new
        {
            MemoryId = memoryId,
            EncryptedContent = encrypted.Ciphertext,
            KeyId = keyId,
            Algorithm = encrypted.Algorithm,
            Iv = encrypted.Iv,
            ContentHash = encrypted.ContentHash
        });

        _logger.LogDebug("Stored encrypted memory {MemoryId}", memoryId);

        return memoryId;
    }

    public async Task<string> RetrieveDecryptedMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT encrypted_content, encryption_key_id, encryption_algorithm,
                   iv, content_hash
            FROM encrypted_memories
            WHERE memory_id = @MemoryId";

        var row = await connection.QuerySingleOrDefaultAsync<EncryptedMemoryRow>(
            sql,
            new { MemoryId = memoryId });

        if (row == null)
        {
            throw new InvalidOperationException($"Encrypted memory {memoryId} not found");
        }

        var encryptedData = new EncryptedData(
            row.encrypted_content,
            row.iv,
            row.encryption_key_id,
            row.encryption_algorithm,
            row.content_hash);

        var decryptedBytes = await DecryptAsync(encryptedData, cancellationToken);

        return Encoding.UTF8.GetString(decryptedBytes);
    }

    private byte[] EncryptKeyWithMaster(byte[] dataKey)
    {
        var iv = RandomNumberGenerator.GetBytes(IvSizeBytes);

        using var aesGcm = new AesGcm(_masterKey, TagSizeBytes);

        var ciphertext = new byte[dataKey.Length];
        var tag = new byte[TagSizeBytes];

        aesGcm.Encrypt(iv, dataKey, ciphertext, tag);

        // Format: IV (12) + Ciphertext (32) + Tag (16) = 60 bytes
        var result = new byte[IvSizeBytes + ciphertext.Length + TagSizeBytes];
        Buffer.BlockCopy(iv, 0, result, 0, IvSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, IvSizeBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, IvSizeBytes + ciphertext.Length, TagSizeBytes);

        return result;
    }

    private byte[] DecryptKeyWithMaster(byte[] encryptedKey)
    {
        // Extract IV, ciphertext, and tag
        var iv = new byte[IvSizeBytes];
        var ciphertext = new byte[encryptedKey.Length - IvSizeBytes - TagSizeBytes];
        var tag = new byte[TagSizeBytes];

        Buffer.BlockCopy(encryptedKey, 0, iv, 0, IvSizeBytes);
        Buffer.BlockCopy(encryptedKey, IvSizeBytes, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(encryptedKey, IvSizeBytes + ciphertext.Length, tag, 0, TagSizeBytes);

        using var aesGcm = new AesGcm(_masterKey, TagSizeBytes);

        var dataKey = new byte[ciphertext.Length];
        aesGcm.Decrypt(iv, ciphertext, tag, dataKey);

        return dataKey;
    }

    private async Task<byte[]> GetDecryptedKeyAsync(
        Guid keyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT encrypted_key
            FROM encryption_keys
            WHERE key_id = @KeyId";

        var encryptedKey = await connection.QuerySingleOrDefaultAsync<byte[]>(
            sql,
            new { KeyId = keyId });

        if (encryptedKey == null)
        {
            throw new InvalidOperationException($"Encryption key {keyId} not found");
        }

        return DecryptKeyWithMaster(encryptedKey);
    }

    private record KeyRow(
        Guid key_id,
        string key_name,
        string key_algorithm,
        int key_version,
        DateTime created_at,
        DateTime? rotated_at,
        bool is_active);

    private record EncryptedMemoryRow(
        byte[] encrypted_content,
        Guid encryption_key_id,
        string encryption_algorithm,
        byte[] iv,
        string content_hash);
}

/// <summary>
/// Secure memory manager that integrates encryption with the memory store.
/// </summary>
public sealed class SecureMemoryManager
{
    private readonly ILocalEncryption _encryption;
    private readonly IKnowledgeGraphStore _store;
    private readonly ILogger<SecureMemoryManager> _logger;
    private readonly string _defaultKeyName;

    public SecureMemoryManager(
        ILocalEncryption encryption,
        IKnowledgeGraphStore store,
        ILogger<SecureMemoryManager> logger,
        string defaultKeyName = "memory-encryption-key")
    {
        _encryption = encryption;
        _store = store;
        _logger = logger;
        _defaultKeyName = defaultKeyName;
    }

    public async Task<Guid> IngestSecureMemoryAsync(
        string content,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        // Ensure encryption key exists
        var key = await _encryption.GetActiveKeyAsync(_defaultKeyName, cancellationToken);
        if (key == null)
        {
            key = await _encryption.CreateKeyAsync(_defaultKeyName, cancellationToken);
        }

        // Create the memory first
        var memory = new SerialMemory.Core.Models.Memory
        {
            Id = Guid.CreateVersion7(),
            Content = content,
            Source = source,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var memoryId = await _store.CreateMemoryAsync(memory);

        // Store encrypted version
        await _encryption.StoreEncryptedMemoryAsync(memoryId, content, key.KeyId, cancellationToken);

        _logger.LogDebug("Ingested secure memory {MemoryId} with encryption key {KeyId}", memoryId, key.KeyId);

        return memoryId;
    }

    public async Task<string> RetrieveSecureMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        // Try encrypted store first
        try
        {
            return await _encryption.RetrieveDecryptedMemoryAsync(memoryId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Fall back to unencrypted store
            var memory = await _store.GetMemoryByIdAsync(memoryId);
            if (memory == null)
            {
                throw new InvalidOperationException($"Memory {memoryId} not found");
            }
            return memory.Content;
        }
    }

    public async Task<EncryptionStats> GetEncryptionStatsAsync(CancellationToken cancellationToken = default)
    {
        // This would query statistics about encrypted vs unencrypted memories
        return new EncryptionStats(0, 0, 0);
    }
}

public record EncryptionStats(
    int TotalMemories,
    int EncryptedMemories,
    int UnencryptedMemories);
