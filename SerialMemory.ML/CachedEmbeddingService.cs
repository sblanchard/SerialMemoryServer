using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.ML;

/// <summary>
/// Decorator that adds LRU caching to any IEmbeddingService implementation.
/// Uses SHA256 hash of input text as cache key with a 5-minute sliding expiration
/// and a 512-entry size limit.
/// </summary>
public sealed class CachedEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly IEmbeddingService _inner;
    private readonly MemoryCache _cache;
    private const int MaxEntries = 512;
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(5);

    public CachedEmbeddingService(IEmbeddingService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = MaxEntries
        });
    }

    public int EmbeddingDimension => _inner.EmbeddingDimension;

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var key = ComputeKey(text);

        if (_cache.TryGetValue(key, out float[]? cached))
            return cached!;

        var embedding = await _inner.EmbedTextAsync(text, cancellationToken);

        var options = new MemoryCacheEntryOptions
        {
            SlidingExpiration = SlidingExpiration,
            Size = 1
        };
        _cache.Set(key, embedding, options);

        return embedding;
    }

    public async Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new float[texts.Count][];
        var uncachedTexts = new List<string>();
        var uncachedIndices = new List<int>();

        for (int i = 0; i < texts.Count; i++)
        {
            var key = ComputeKey(texts[i]);
            if (_cache.TryGetValue(key, out float[]? cached))
            {
                results[i] = cached!;
            }
            else
            {
                uncachedTexts.Add(texts[i]);
                uncachedIndices.Add(i);
            }
        }

        if (uncachedTexts.Count > 0)
        {
            var newEmbeddings = await _inner.EmbedBatchAsync(uncachedTexts, cancellationToken);

            for (int i = 0; i < uncachedTexts.Count; i++)
            {
                results[uncachedIndices[i]] = newEmbeddings[i];

                var options = new MemoryCacheEntryOptions
                {
                    SlidingExpiration = SlidingExpiration,
                    Size = 1
                };
                _cache.Set(ComputeKey(uncachedTexts[i]), newEmbeddings[i], options);
            }
        }

        return [.. results];
    }

    private static string ComputeKey(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
