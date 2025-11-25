using StackExchange.Redis;
using SerialMemory.Core.Interfaces;

namespace SerialMemory.Infrastructure;

public class RedisContextStore(IConnectionMultiplexer redis) : IContextStore
{
    private readonly IDatabase _db = redis.GetDatabase();
    private const string Namespace = "context:";

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
        => await _db.StringGetAsync(Namespace + key);

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
        => await _db.StringSetAsync(Namespace + key, value);

    public async Task DeleteAsync(string key, CancellationToken ct = default)
        => await _db.KeyDeleteAsync(Namespace + key);

    public async Task<IEnumerable<string>> ListKeysAsync(CancellationToken ct = default)
    {
        var endpoints = redis.GetEndPoints();
        var server = redis.GetServer(endpoints.First());
        return server.Keys(pattern: Namespace + "*").Select(k => k.ToString()[Namespace.Length..]);
    }
}