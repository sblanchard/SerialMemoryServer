using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialMemory.Sdk;

/// <summary>
/// Lightweight SerialMemory client SDK for .NET applications.
/// Provides typed methods for memory operations with built-in retry and rate limit handling.
/// Dependency-light: uses only System.Text.Json (built-in).
/// </summary>
public sealed class SerialMemoryClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SerialMemoryClientOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    /// <summary>
    /// Event raised when usage crosses a threshold (75%, 90%).
    /// </summary>
    public event Action<UsageWarning>? OnUsageWarning;

    /// <summary>
    /// Creates a new SerialMemory client.
    /// </summary>
    /// <param name="options">Client configuration options</param>
    public SerialMemoryClient(SerialMemoryClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            throw new ArgumentException("BaseUrl is required", nameof(options));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = options.Timeout
        };

        // Configure authentication
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            if (options.ApiKey.StartsWith("sm_", StringComparison.OrdinalIgnoreCase))
            {
                // API key format - use X-API-Key header
                _httpClient.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
            }
            else
            {
                // Assume JWT token
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }
        }

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }

    #region Core Memory Operations

    /// <summary>
    /// Search memories using semantic, text, or hybrid search.
    /// </summary>
    public async Task<SearchResult> SearchAsync(
        string query,
        SearchMode mode = SearchMode.Hybrid,
        int limit = 10,
        float threshold = 0.7f,
        bool includeEntities = true,
        CancellationToken ct = default)
    {
        var request = new
        {
            query,
            mode = mode.ToString().ToLowerInvariant(),
            limit,
            threshold,
            include_entities = includeEntities
        };

        return await PostAsync<SearchResult>("tools/memory_search", request, ct);
    }

    /// <summary>
    /// Ingest a new memory. Automatically extracts entities and generates embeddings.
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        string content,
        string? source = null,
        Dictionary<string, object>? metadata = null,
        bool extractEntities = true,
        CancellationToken ct = default)
    {
        var request = new
        {
            content,
            source = source ?? "sdk-dotnet",
            metadata,
            extract_entities = extractEntities
        };

        return await PostAsync<IngestResult>("tools/memory_ingest", request, ct);
    }

    /// <summary>
    /// Update an existing memory's content (creates new version).
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(
        string memoryId,
        string newContent,
        string? reason = null,
        CancellationToken ct = default)
    {
        var request = new
        {
            memory_id = memoryId,
            new_content = newContent,
            reason
        };

        return await PostAsync<UpdateResult>("tools/memory_update", request, ct);
    }

    /// <summary>
    /// Soft delete (invalidate) a memory.
    /// </summary>
    public async Task<DeleteResult> DeleteAsync(
        string memoryId,
        string reason,
        CancellationToken ct = default)
    {
        var request = new
        {
            memory_id = memoryId,
            reason
        };

        return await PostAsync<DeleteResult>("tools/memory_delete", request, ct);
    }

    #endregion

    #region User Persona

    /// <summary>
    /// Get user persona information (preferences, skills, goals, background).
    /// </summary>
    public async Task<UserPersona> GetUserPersonaAsync(
        string userId = "default_user",
        CancellationToken ct = default)
    {
        var request = new { user_id = userId };
        return await PostAsync<UserPersona>("tools/memory_about_user", request, ct);
    }

    /// <summary>
    /// Set or update a user persona attribute.
    /// </summary>
    public async Task<SetPersonaResult> SetUserPersonaAsync(
        string attributeType,
        string attributeKey,
        string attributeValue,
        float confidence = 1.0f,
        string userId = "default_user",
        CancellationToken ct = default)
    {
        var request = new
        {
            attribute_type = attributeType,
            attribute_key = attributeKey,
            attribute_value = attributeValue,
            confidence,
            user_id = userId
        };

        return await PostAsync<SetPersonaResult>("tools/set_user_persona", request, ct);
    }

    #endregion

    #region Limits & Usage

    /// <summary>
    /// Get current tenant limits and usage information.
    /// Useful for displaying usage to users or making decisions about operations.
    /// </summary>
    public async Task<TenantLimits> GetLimitsAsync(CancellationToken ct = default)
    {
        var limits = await GetAsync<TenantLimits>("tenant/limits", ct);

        // Check for warnings and raise events
        CheckUsageWarnings(limits);

        return limits;
    }

    private void CheckUsageWarnings(TenantLimits limits)
    {
        if (OnUsageWarning == null) return;

        foreach (var warning in limits.Warnings)
        {
            OnUsageWarning.Invoke(new UsageWarning
            {
                Type = warning.Type,
                Message = warning.Message,
                PercentUsed = warning.PercentUsed,
                Severity = warning.Severity
            });
        }
    }

    #endregion

    #region HTTP Helpers

    private async Task<T> PostAsync<T>(string endpoint, object request, CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var response = await _httpClient.PostAsJsonAsync(endpoint, request, _jsonOptions, ct);
            return await HandleResponseAsync<T>(response, ct);
        }, ct);
    }

    private async Task<T> GetAsync<T>(string endpoint, CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var response = await _httpClient.GetAsync(endpoint, ct);
            return await HandleResponseAsync<T>(response, ct);
        }, ct);
    }

    private async Task<T> HandleResponseAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct);
            return result ?? throw new SerialMemoryException("Empty response received");
        }

        var errorContent = await response.Content.ReadAsStringAsync(ct);

        // Handle specific error codes
        switch (response.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                throw new RateLimitException(retryAfter, errorContent);

            case (HttpStatusCode)402: // Payment Required - Usage limit exceeded
                throw new UsageLimitException(errorContent);

            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                throw new AuthenticationException(errorContent);

            default:
                throw new SerialMemoryException($"Request failed ({(int)response.StatusCode}): {errorContent}");
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var maxRetries = _options.MaxRetries;
        var delay = TimeSpan.FromMilliseconds(500);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (RateLimitException ex) when (attempt < maxRetries)
            {
                // Use retry-after header if available
                await Task.Delay(ex.RetryAfter, ct);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                // Transient error - exponential backoff with jitter
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
                await Task.Delay(delay + jitter, ct);
                delay *= 2; // Exponential backoff
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                // Timeout - retry with backoff
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }

        // Should not reach here, but just in case
        throw new SerialMemoryException("Max retries exceeded");
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _httpClient.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Client configuration options.
/// </summary>
public sealed class SerialMemoryClientOptions
{
    /// <summary>
    /// Base URL of the SerialMemory API (e.g., "http://localhost:5000").
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// API key (sm_live_xxx format) or JWT token for authentication.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// HTTP request timeout (default: 30 seconds).
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum retry attempts for transient failures (default: 3).
    /// </summary>
    public int MaxRetries { get; init; } = 3;
}

/// <summary>
/// Search mode for memory queries.
/// </summary>
public enum SearchMode
{
    /// <summary>Vector similarity search using embeddings.</summary>
    Semantic,
    /// <summary>Full-text search.</summary>
    Text,
    /// <summary>Combined semantic + text search (recommended).</summary>
    Hybrid
}
