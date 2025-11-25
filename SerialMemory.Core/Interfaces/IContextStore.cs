namespace SerialMemory.Core.Interfaces;

public interface IContextStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<IEnumerable<string>> ListKeysAsync(CancellationToken ct = default);
}