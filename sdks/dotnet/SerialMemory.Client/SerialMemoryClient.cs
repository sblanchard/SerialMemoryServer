using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace SerialMemory.Client;

/// <summary>
/// SerialMemory client SDK for .NET applications.
/// Provides high-level methods for memory operations with built-in retry, rate limit handling, and circuit breaker.
/// </summary>
public sealed class SerialMemoryClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SerialMemoryOptions _options;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    /// <summary>
    /// Creates a new SerialMemory client with the specified options.
    /// </summary>
    public SerialMemoryClient(SerialMemoryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = options.Timeout
        };

        // Configure authentication
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
        }
        else if (!string.IsNullOrEmpty(options.JwtToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.JwtToken);
        }

        // Add tenant header if specified
        if (options.TenantId.HasValue)
        {
            _httpClient.DefaultRequestHeaders.Add("X-Tenant-Id", options.TenantId.Value.ToString());
        }

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        // Build resilience pipeline: retry -> circuit breaker
        _pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(500),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => IsTransientError(r.StatusCode))
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                OnRetry = args =>
                {
                    options.OnRetry?.Invoke(args.AttemptNumber, args.Outcome.Exception);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = options.CircuitBreakerDuration,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError)
                    .Handle<HttpRequestException>(),
                OnOpened = args =>
                {
                    options.OnCircuitOpened?.Invoke(args.BreakDuration);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    #region Memory Operations

    /// <summary>
    /// Search memories using semantic, text, or hybrid search.
    /// </summary>
    /// <param name="query">Natural language search query</param>
    /// <param name="mode">Search mode: "semantic", "text", or "hybrid" (default)</param>
    /// <param name="limit">Maximum results to return (default: 10)</param>
    /// <param name="threshold">Minimum similarity threshold 0.0-1.0 (default: 0.7)</param>
    /// <param name="includeEntities">Include linked entities in results (default: true)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Search results with memories and similarity scores</returns>
    public async Task<MemorySearchResult> SearchAsync(
        string query,
        SearchMode mode = SearchMode.Hybrid,
        int limit = 10,
        float threshold = 0.7f,
        bool includeEntities = true,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            query,
            mode = mode.ToString().ToLowerInvariant(),
            limit,
            threshold,
            include_entities = includeEntities
        };

        return await PostAsync<MemorySearchResult>("memory/search", request, cancellationToken);
    }

    /// <summary>
    /// Ingest a new memory into the knowledge graph.
    /// Automatically extracts entities and generates embeddings.
    /// </summary>
    /// <param name="content">Memory content to store</param>
    /// <param name="source">Source identifier (e.g., "my-app", "claude-desktop")</param>
    /// <param name="metadata">Optional metadata dictionary</param>
    /// <param name="extractEntities">Whether to extract entities (default: true)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created memory with ID and extracted entities</returns>
    public async Task<MemoryIngestResult> IngestAsync(
        string content,
        string? source = null,
        Dictionary<string, object>? metadata = null,
        bool extractEntities = true,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            content,
            source = source ?? _options.DefaultSource,
            metadata,
            extract_entities = extractEntities
        };

        return await PostAsync<MemoryIngestResult>("memory/ingest", request, cancellationToken);
    }

    /// <summary>
    /// Update an existing memory's content.
    /// Creates a new version (event-sourced, immutable).
    /// </summary>
    /// <param name="memoryId">UUID of the memory to update</param>
    /// <param name="newContent">New content to replace existing</param>
    /// <param name="reason">Reason for update (audit trail)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated memory details</returns>
    public async Task<MemoryUpdateResult> UpdateAsync(
        Guid memoryId,
        string newContent,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            memory_id = memoryId.ToString(),
            new_content = newContent,
            reason
        };

        return await PostAsync<MemoryUpdateResult>("memory/update", request, cancellationToken);
    }

    /// <summary>
    /// Soft delete (invalidate) a memory.
    /// Memory remains for audit trail but is marked inactive.
    /// </summary>
    /// <param name="memoryId">UUID of the memory to delete</param>
    /// <param name="reason">Reason for deletion (required for audit)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Deletion confirmation</returns>
    public async Task<MemoryDeleteResult> DeleteAsync(
        Guid memoryId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            memory_id = memoryId.ToString(),
            reason
        };

        return await PostAsync<MemoryDeleteResult>("memory/delete", request, cancellationToken);
    }

    /// <summary>
    /// Perform multi-hop search by traversing the knowledge graph.
    /// Finds related memories through entity relationships.
    /// </summary>
    /// <param name="query">Initial search query</param>
    /// <param name="hops">Number of relationship hops to traverse (default: 2)</param>
    /// <param name="maxResultsPerHop">Maximum results per hop (default: 5)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Multi-hop search results with traversal paths</returns>
    public async Task<MultiHopSearchResult> MultiHopSearchAsync(
        string query,
        int hops = 2,
        int maxResultsPerHop = 5,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            query,
            hops,
            max_results_per_hop = maxResultsPerHop
        };

        return await PostAsync<MultiHopSearchResult>("memory/multi-hop-search", request, cancellationToken);
    }

    #endregion

    #region User Persona Operations

    /// <summary>
    /// Get structured information about the user's persona.
    /// Includes preferences, skills, goals, and background.
    /// </summary>
    /// <param name="userId">User identifier (default: "default_user")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User persona with all attributes</returns>
    public async Task<UserPersonaResult> GetUserPersonaAsync(
        string userId = "default_user",
        CancellationToken cancellationToken = default)
    {
        var request = new { user_id = userId };
        return await PostAsync<UserPersonaResult>("memory/about-user", request, cancellationToken);
    }

    /// <summary>
    /// Set or update a user persona attribute.
    /// </summary>
    /// <param name="attributeType">Type: "preference", "skill", "goal", "background"</param>
    /// <param name="attributeKey">Attribute name (e.g., "programming_language")</param>
    /// <param name="attributeValue">Attribute value</param>
    /// <param name="confidence">Confidence score 0.0-1.0 (default: 1.0)</param>
    /// <param name="userId">User identifier (default: "default_user")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated attribute confirmation</returns>
    public async Task<SetPersonaResult> SetUserPersonaAsync(
        PersonaAttributeType attributeType,
        string attributeKey,
        string attributeValue,
        float confidence = 1.0f,
        string userId = "default_user",
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            attribute_type = attributeType.ToString().ToLowerInvariant(),
            attribute_key = attributeKey,
            attribute_value = attributeValue,
            confidence,
            user_id = userId
        };

        return await PostAsync<SetPersonaResult>("memory/set-persona", request, cancellationToken);
    }

    #endregion

    #region Session Management

    /// <summary>
    /// Initialize a new conversation session for context tracking.
    /// </summary>
    /// <param name="sessionName">Optional session name/title</param>
    /// <param name="clientType">Client type identifier</param>
    /// <param name="metadata">Optional session metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session details with ID</returns>
    public async Task<SessionResult> InitializeSessionAsync(
        string? sessionName = null,
        string? clientType = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            session_name = sessionName,
            client_type = clientType ?? _options.DefaultSource,
            metadata
        };

        return await PostAsync<SessionResult>("session/initialize", request, cancellationToken);
    }

    /// <summary>
    /// End the current conversation session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session end confirmation</returns>
    public async Task<SessionResult> EndSessionAsync(CancellationToken cancellationToken = default)
    {
        return await PostAsync<SessionResult>("session/end", new { }, cancellationToken);
    }

    #endregion

    #region Graph Statistics

    /// <summary>
    /// Get statistics about the knowledge graph.
    /// Includes entity and relationship counts by type.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Graph statistics</returns>
    public async Task<GraphStatisticsResult> GetGraphStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        return await PostAsync<GraphStatisticsResult>("graph/statistics", new { }, cancellationToken);
    }

    #endregion

    #region Private Helpers

    private async Task<T> PostAsync<T>(string endpoint, object request, CancellationToken cancellationToken)
    {
        var response = await _pipeline.ExecuteAsync(async ct =>
        {
            var httpResponse = await _httpClient.PostAsJsonAsync(endpoint, request, _jsonOptions, ct);
            return httpResponse;
        }, cancellationToken);

        // Handle specific error codes
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                throw new RateLimitExceededException(retryAfter, errorContent);
            }

            if (response.StatusCode == (HttpStatusCode)402) // Payment Required
            {
                throw new UsageLimitExceededException(errorContent);
            }

            throw new SerialMemoryException(
                $"Request failed with status {response.StatusCode}: {errorContent}",
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
        return result ?? throw new SerialMemoryException("Empty response received");
    }

    private static bool IsTransientError(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.TooManyRequests;

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _httpClient.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Configuration options for SerialMemoryClient.
/// </summary>
public class SerialMemoryOptions
{
    /// <summary>
    /// Base URL of the SerialMemory API (e.g., "http://localhost:5000")
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// API key for authentication (preferred for server-to-server)
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// JWT token for authentication (alternative to API key)
    /// </summary>
    public string? JwtToken { get; init; }

    /// <summary>
    /// Tenant ID for multi-tenant deployments
    /// </summary>
    public Guid? TenantId { get; init; }

    /// <summary>
    /// Default source identifier for ingested memories
    /// </summary>
    public string DefaultSource { get; init; } = "serialmemory-sdk-dotnet";

    /// <summary>
    /// HTTP request timeout (default: 30 seconds)
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum retry attempts for transient failures (default: 3)
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Circuit breaker open duration (default: 30 seconds)
    /// </summary>
    public TimeSpan CircuitBreakerDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Callback invoked on retry attempts
    /// </summary>
    public Action<int, Exception?>? OnRetry { get; init; }

    /// <summary>
    /// Callback invoked when circuit breaker opens
    /// </summary>
    public Action<TimeSpan>? OnCircuitOpened { get; init; }
}

/// <summary>
/// Search mode for memory queries
/// </summary>
public enum SearchMode
{
    /// <summary>Vector similarity search using embeddings</summary>
    Semantic,
    /// <summary>Full-text search with PostgreSQL ts_rank</summary>
    Text,
    /// <summary>Combined semantic + text search (recommended)</summary>
    Hybrid
}

/// <summary>
/// User persona attribute types
/// </summary>
public enum PersonaAttributeType
{
    Preference,
    Skill,
    Goal,
    Background
}
