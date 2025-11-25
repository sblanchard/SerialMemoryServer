using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure.DeterministicInference;

public sealed class EmbeddingCache : IEmbeddingCache
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<EmbeddingCache> _logger;
    private readonly string _modelVersion;

    public EmbeddingCache(
        NpgsqlDataSource dataSource,
        ILogger<EmbeddingCache> logger,
        string modelVersion = "onnx-minilm-v2")
    {
        _dataSource = dataSource;
        _logger = logger;
        _modelVersion = modelVersion;
    }

    public async Task<float[]?> GetAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var cacheKey = ComputeCacheKey(contentHash);

        const string sql = @"
            UPDATE embedding_cache
            SET last_accessed_at = NOW(), access_count = access_count + 1
            WHERE cache_key = @CacheKey AND model_version = @ModelVersion
            RETURNING embedding";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("CacheKey", cacheKey);
        cmd.Parameters.AddWithValue("ModelVersion", _modelVersion);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var vector = reader.GetFieldValue<Vector>(0);
            _logger.LogDebug("Cache hit for embedding {CacheKey}", cacheKey);
            return vector.ToArray();
        }

        return null;
    }

    public async Task SetAsync(
        string contentHash,
        float[] embedding,
        string modelVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var cacheKey = ComputeCacheKey(contentHash);

        // Use composite key (cache_key, model_version) so different models can coexist
        const string sql = @"
            INSERT INTO embedding_cache (cache_key, content_hash, embedding, model_version)
            VALUES (@CacheKey, @ContentHash, @Embedding, @ModelVersion)
            ON CONFLICT (cache_key, model_version)
            DO UPDATE SET
                embedding = EXCLUDED.embedding,
                last_accessed_at = NOW(),
                access_count = embedding_cache.access_count + 1";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("CacheKey", cacheKey);
        cmd.Parameters.AddWithValue("ContentHash", contentHash);
        cmd.Parameters.AddWithValue("Embedding", new Vector(embedding));
        cmd.Parameters.AddWithValue("ModelVersion", modelVersion);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("Cached embedding {CacheKey}", cacheKey);
    }

    public async Task<bool> ExistsAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var cacheKey = ComputeCacheKey(contentHash);

        const string sql = @"
            SELECT EXISTS(
                SELECT 1 FROM embedding_cache
                WHERE cache_key = @CacheKey AND model_version = @ModelVersion
            )";

        return await connection.ExecuteScalarAsync<bool>(
            sql,
            new { CacheKey = cacheKey, ModelVersion = _modelVersion });
    }

    public async Task MarkCompiledAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var cacheKey = ComputeCacheKey(contentHash);

        const string sql = @"
            UPDATE embedding_cache
            SET is_compiled = TRUE
            WHERE cache_key = @CacheKey AND model_version = @ModelVersion";

        await connection.ExecuteAsync(sql, new { CacheKey = cacheKey, ModelVersion = _modelVersion });
    }

    public async Task<int> GetCompiledCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT COUNT(*) FROM embedding_cache
            WHERE is_compiled = TRUE AND model_version = @ModelVersion";

        return await connection.ExecuteScalarAsync<int>(sql, new { ModelVersion = _modelVersion });
    }

    public async Task PruneUnusedAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            DELETE FROM embedding_cache
            WHERE is_compiled = FALSE
              AND last_accessed_at < NOW() - @MaxAge::interval
              AND access_count < 5";

        var deleted = await connection.ExecuteAsync(
            sql,
            new { MaxAge = maxAge.ToString() });

        if (deleted > 0)
        {
            _logger.LogInformation("Pruned {Count} unused embedding cache entries", deleted);
        }
    }

    private static string ComputeCacheKey(string contentHash)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(contentHash));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// Deterministic embedding service that caches all embeddings.
/// </summary>
public sealed class CachedEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingService _inner;
    private readonly IEmbeddingCache _cache;
    private readonly ILogger<CachedEmbeddingService> _logger;
    private readonly string _modelVersion;

    public CachedEmbeddingService(
        IEmbeddingService inner,
        IEmbeddingCache cache,
        ILogger<CachedEmbeddingService> logger,
        string modelVersion = "onnx-minilm-v2")
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
        _modelVersion = modelVersion;
    }

    public int EmbeddingDimension => _inner.EmbeddingDimension;

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var contentHash = ComputeContentHash(text);

        var cached = await _cache.GetAsync(contentHash, cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        var embedding = await _inner.EmbedTextAsync(text, cancellationToken);

        await _cache.SetAsync(contentHash, embedding, _modelVersion, cancellationToken);

        return embedding;
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new float[texts.Count][];
        var uncachedIndices = new List<int>();
        var uncachedTexts = new List<string>();

        // Check cache for each text
        for (int i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contentHash = ComputeContentHash(texts[i]);
            var cached = await _cache.GetAsync(contentHash, cancellationToken);

            if (cached != null)
            {
                results[i] = cached;
            }
            else
            {
                uncachedIndices.Add(i);
                uncachedTexts.Add(texts[i]);
            }
        }

        // Batch process uncached texts
        if (uncachedTexts.Count > 0)
        {
            var newEmbeddings = await _inner.EmbedBatchAsync(uncachedTexts, cancellationToken);

            for (int i = 0; i < uncachedIndices.Count; i++)
            {
                var idx = uncachedIndices[i];
                results[idx] = newEmbeddings[i];

                var contentHash = ComputeContentHash(texts[idx]);
                await _cache.SetAsync(contentHash, newEmbeddings[i], _modelVersion, cancellationToken);
            }

            _logger.LogDebug(
                "Embedded {Uncached} uncached texts, {Cached} from cache",
                uncachedTexts.Count,
                texts.Count - uncachedTexts.Count);
        }

        return results.ToList();
    }

    private static string ComputeContentHash(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
