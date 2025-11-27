using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SerialMemory.Core.Performance;

/// <summary>
/// Multi-level cache with L1 (in-memory) and L2 (external) support.
/// </summary>
public interface ICacheLayer
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default) where T : class;
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// High-performance in-memory cache (L1) with LRU eviction.
/// </summary>
public sealed class MemoryCache : ICacheLayer, IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly LinkedList<string> _lruList = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new();
    private readonly object _lruLock = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;
    private readonly Timer _cleanupTimer;
    private readonly CacheLayerMetrics? _metrics;

    public MemoryCache(int maxEntries = 10000, TimeSpan? defaultTtl = null, CacheLayerMetrics? metrics = null)
    {
        _maxEntries = maxEntries;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
        _metrics = metrics;
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        if (!_cache.TryGetValue(key, out var entry))
        {
            _metrics?.RecordMiss();
            return Task.FromResult<T?>(null);
        }

        if (entry.IsExpired)
        {
            _cache.TryRemove(key, out _);
            RemoveFromLru(key);
            _metrics?.RecordMiss();
            _metrics?.RecordEviction();
            return Task.FromResult<T?>(null);
        }

        MoveToFront(key);
        _metrics?.RecordHit();
        return Task.FromResult(entry.Value as T);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default) where T : class
    {
        var entry = new CacheEntry(value, ttl ?? _defaultTtl);

        // Evict if at capacity
        while (_cache.Count >= _maxEntries)
        {
            EvictLru();
        }

        _cache[key] = entry;
        AddToLru(key);
        _metrics?.RecordWrite();
        _metrics?.SetSize(_cache.Count);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _cache.TryRemove(key, out _);
        RemoveFromLru(key);
        _metrics?.SetSize(_cache.Count);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(key, out var entry))
            return Task.FromResult(false);

        if (entry.IsExpired)
        {
            _cache.TryRemove(key, out _);
            RemoveFromLru(key);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _cache.Clear();
        lock (_lruLock)
        {
            _lruList.Clear();
            _lruIndex.Clear();
        }
        _metrics?.SetSize(0);
        return Task.CompletedTask;
    }

    private void AddToLru(string key)
    {
        lock (_lruLock)
        {
            if (_lruIndex.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
            }
            var newNode = _lruList.AddFirst(key);
            _lruIndex[key] = newNode;
        }
    }

    private void MoveToFront(string key)
    {
        lock (_lruLock)
        {
            if (_lruIndex.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                var newNode = _lruList.AddFirst(key);
                _lruIndex[key] = newNode;
            }
        }
    }

    private void RemoveFromLru(string key)
    {
        lock (_lruLock)
        {
            if (_lruIndex.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruIndex.Remove(key);
            }
        }
    }

    private void EvictLru()
    {
        string? keyToEvict = null;

        lock (_lruLock)
        {
            if (_lruList.Last != null)
            {
                keyToEvict = _lruList.Last.Value;
                _lruList.RemoveLast();
                _lruIndex.Remove(keyToEvict);
            }
        }

        if (keyToEvict != null)
        {
            _cache.TryRemove(keyToEvict, out _);
            _metrics?.RecordEviction();
        }
    }

    private void CleanupExpired(object? state)
    {
        var expiredKeys = _cache
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
            RemoveFromLru(key);
            _metrics?.RecordEviction();
        }

        _metrics?.SetSize(_cache.Count);
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }

    private sealed class CacheEntry
    {
        public object Value { get; }
        public DateTimeOffset ExpiresAt { get; }
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

        public CacheEntry(object value, TimeSpan ttl)
        {
            Value = value;
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
        }
    }
}

/// <summary>
/// Redis-backed cache (L2) implementation.
/// </summary>
public sealed class RedisCacheLayer : ICacheLayer
{
    private readonly string _connectionString;
    private readonly string _prefix;
    private readonly TimeSpan _defaultTtl;
    private readonly CacheLayerMetrics? _metrics;
    private readonly Func<string, string, TimeSpan?, Task>? _setAsync;
    private readonly Func<string, Task<string?>>? _getAsync;
    private readonly Func<string, Task<bool>>? _deleteAsync;
    private readonly Func<Task>? _flushAsync;

    // Constructor for real Redis connection
    public RedisCacheLayer(
        string connectionString,
        string prefix = "cache:",
        TimeSpan? defaultTtl = null,
        CacheLayerMetrics? metrics = null)
    {
        _connectionString = connectionString;
        _prefix = prefix;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(30);
        _metrics = metrics;
    }

    // Constructor for testing/mocking
    public RedisCacheLayer(
        Func<string, Task<string?>> getAsync,
        Func<string, string, TimeSpan?, Task> setAsync,
        Func<string, Task<bool>> deleteAsync,
        Func<Task>? flushAsync = null,
        string prefix = "cache:",
        TimeSpan? defaultTtl = null,
        CacheLayerMetrics? metrics = null)
    {
        _connectionString = "";
        _getAsync = getAsync;
        _setAsync = setAsync;
        _deleteAsync = deleteAsync;
        _flushAsync = flushAsync;
        _prefix = prefix;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(30);
        _metrics = metrics;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        var fullKey = _prefix + key;

        try
        {
            string? json = _getAsync != null
                ? await _getAsync(fullKey)
                : await GetFromRedisAsync(fullKey, ct);

            if (json == null)
            {
                _metrics?.RecordMiss();
                return null;
            }

            _metrics?.RecordHit();
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            _metrics?.RecordMiss();
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default) where T : class
    {
        var fullKey = _prefix + key;
        var json = JsonSerializer.Serialize(value);
        var expiry = ttl ?? _defaultTtl;

        if (_setAsync != null)
        {
            await _setAsync(fullKey, json, expiry);
        }
        else
        {
            await SetInRedisAsync(fullKey, json, expiry, ct);
        }

        _metrics?.RecordWrite();
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        var fullKey = _prefix + key;

        if (_deleteAsync != null)
        {
            await _deleteAsync(fullKey);
        }
        else
        {
            await DeleteFromRedisAsync(fullKey, ct);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        var result = await GetAsync<object>(key, ct);
        return result != null;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (_flushAsync != null)
        {
            await _flushAsync();
        }
        // Real implementation would use SCAN + DEL for prefix-based clearing
    }

    // Placeholder for actual Redis operations - would use StackExchange.Redis
    private Task<string?> GetFromRedisAsync(string key, CancellationToken ct)
    {
        // Real implementation: _redis.StringGetAsync(key)
        return Task.FromResult<string?>(null);
    }

    private Task SetInRedisAsync(string key, string value, TimeSpan expiry, CancellationToken ct)
    {
        // Real implementation: _redis.StringSetAsync(key, value, expiry)
        return Task.CompletedTask;
    }

    private Task DeleteFromRedisAsync(string key, CancellationToken ct)
    {
        // Real implementation: _redis.KeyDeleteAsync(key)
        return Task.CompletedTask;
    }
}

/// <summary>
/// Multi-level cache that checks L1 first, then L2.
/// </summary>
public sealed class MultiLevelCache : ICacheLayer
{
    private readonly ICacheLayer _l1;
    private readonly ICacheLayer? _l2;
    private readonly TimeSpan _l1Ttl;

    public MultiLevelCache(ICacheLayer l1, ICacheLayer? l2 = null, TimeSpan? l1Ttl = null)
    {
        _l1 = l1;
        _l2 = l2;
        _l1Ttl = l1Ttl ?? TimeSpan.FromMinutes(1);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        // Try L1 first
        var result = await _l1.GetAsync<T>(key, ct);
        if (result != null)
            return result;

        // Try L2
        if (_l2 != null)
        {
            result = await _l2.GetAsync<T>(key, ct);
            if (result != null)
            {
                // Populate L1
                await _l1.SetAsync(key, result, _l1Ttl, ct);
                return result;
            }
        }

        return null;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default) where T : class
    {
        // Write to both levels
        await _l1.SetAsync(key, value, _l1Ttl, ct);

        if (_l2 != null)
        {
            await _l2.SetAsync(key, value, ttl, ct);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _l1.RemoveAsync(key, ct);
        if (_l2 != null)
        {
            await _l2.RemoveAsync(key, ct);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (await _l1.ExistsAsync(key, ct))
            return true;

        return _l2 != null && await _l2.ExistsAsync(key, ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _l1.ClearAsync(ct);
        if (_l2 != null)
        {
            await _l2.ClearAsync(ct);
        }
    }
}

/// <summary>
/// Cache key builder with consistent hashing.
/// </summary>
public static class CacheKey
{
    public static string Build(string prefix, params object[] parts)
    {
        var sb = new StringBuilder(prefix);
        foreach (var part in parts)
        {
            sb.Append(':');
            sb.Append(part?.ToString() ?? "null");
        }
        return sb.ToString();
    }

    public static string BuildHashed(string prefix, params object[] parts)
    {
        var key = Build(prefix, parts);
        if (key.Length <= 64) return key;

        // Hash long keys
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"{prefix}:{Convert.ToHexStringLower(hash[..16])}";
    }

    public static string ForMemory(Guid memoryId) => Build("memory", memoryId);
    public static string ForEntity(Guid entityId) => Build("entity", entityId);
    public static string ForSearch(string query, string mode) => BuildHashed("search", query, mode);
    public static string ForEmbedding(string content) => BuildHashed("embedding", content);
    public static string ForUser(string userId) => Build("user", userId);
    public static string ForSession(string sessionId) => Build("session", sessionId);
}

/// <summary>
/// Cache-aside pattern helper.
/// </summary>
public sealed class CacheAside<T> where T : class
{
    private readonly ICacheLayer _cache;
    private readonly TimeSpan _ttl;
    private readonly PerformanceRegistry? _perf;
    private readonly string _operationName;

    public CacheAside(
        ICacheLayer cache,
        TimeSpan ttl,
        PerformanceRegistry? perf = null,
        string operationName = "cache_aside")
    {
        _cache = cache;
        _ttl = ttl;
        _perf = perf;
        _operationName = operationName;
    }

    public async Task<T?> GetOrLoadAsync(
        string key,
        Func<Task<T?>> loader,
        CancellationToken ct = default)
    {
        // Try cache
        var cached = await _cache.GetAsync<T>(key, ct);
        if (cached != null)
            return cached;

        // Load from source
        T? loaded;
        if (_perf != null)
        {
            loaded = await _perf.MeasureAsync(_operationName, loader);
        }
        else
        {
            loaded = await loader();
        }

        // Cache if found
        if (loaded != null)
        {
            await _cache.SetAsync(key, loaded, _ttl, ct);
        }

        return loaded;
    }

    public async Task InvalidateAsync(string key, CancellationToken ct = default)
    {
        await _cache.RemoveAsync(key, ct);
    }

    public async Task RefreshAsync(string key, Func<Task<T?>> loader, CancellationToken ct = default)
    {
        await _cache.RemoveAsync(key, ct);
        await GetOrLoadAsync(key, loader, ct);
    }
}

/// <summary>
/// Write-through cache pattern.
/// </summary>
public sealed class WriteThrough<T> where T : class
{
    private readonly ICacheLayer _cache;
    private readonly Func<string, T, Task> _writer;
    private readonly TimeSpan _ttl;

    public WriteThrough(ICacheLayer cache, Func<string, T, Task> writer, TimeSpan ttl)
    {
        _cache = cache;
        _writer = writer;
        _ttl = ttl;
    }

    public async Task WriteAsync(string key, T value, CancellationToken ct = default)
    {
        // Write to persistent store first
        await _writer(key, value);

        // Then update cache
        await _cache.SetAsync(key, value, _ttl, ct);
    }
}
