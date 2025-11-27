using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SerialMemory.CoreExport.Models;

namespace SerialMemory.CoreExport.Services;

/// <summary>
/// Client for CORE API
/// Uses REST endpoints: POST /api/v1/search, GET /api/v1/spaces, etc.
/// Authentication: Bearer token in Authorization header
/// </summary>
public class CoreApiClient
{
    private readonly HttpClient _httpClient;
    private readonly CoreConfig _config;
    private readonly ILogger<CoreApiClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public CoreApiClient(
        HttpClient httpClient,
        IOptions<CoreConfig> config,
        ILogger<CoreApiClient> logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Configure HttpClient
        // Ensure BaseAddress ends with / for proper relative URL resolution
        var baseUrl = _config.BaseUrl.TrimEnd('/') + "/";
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        _httpClient.Timeout = TimeSpan.FromMinutes(5);

        _logger.LogInformation("CoreApiClient configured: BaseUrl={BaseUrl}, ApiKey={ApiKeyPrefix}...",
            _httpClient.BaseAddress,
            string.IsNullOrEmpty(_config.ApiKey) ? "(empty)" : _config.ApiKey[..Math.Min(10, _config.ApiKey.Length)]);
    }

    /// <summary>
    /// Get spaces (workspaces) using REST API
    /// GET /v1/spaces
    /// </summary>
    public async Task<List<Workspace>> GetWorkspaces(CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching spaces from CORE API");

        var response = await ExecuteWithRetry(async () =>
        {
            var result = await _httpClient.GetAsync("v1/spaces", ct);
            if (!result.IsSuccessStatusCode)
            {
                var errorContent = await result.Content.ReadAsStringAsync(ct);
                _logger.LogError("API error {StatusCode}: {Content}", result.StatusCode, errorContent);
            }
            result.EnsureSuccessStatusCode();
            return result;
        }, ct);

        var content = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Spaces response: {Content}", content.Length > 500 ? content[..500] + "..." : content);

        var spaces = JsonSerializer.Deserialize<List<CoreSpace>>(content, _jsonOptions) ?? [];

        var workspaces = spaces.Select(s => new Workspace
        {
            Id = s.Id,
            Name = s.Name ?? s.Id
        }).ToList();

        _logger.LogInformation("Retrieved {Count} spaces", workspaces.Count);
        return workspaces;
    }

    /// <summary>
    /// Search memories using REST API
    /// POST /v1/search
    /// </summary>
    public async Task<PagedResult<MemoryItem>> GetMemories(
        string workspaceId,
        string? cursor = null,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Searching memories via search API");

        // Use search endpoint with broad query to get all memories
        var searchRequest = new CoreSearchRequest
        {
            Query = "*", // Wildcard to match all
            Limit = _config.PageSize,
            SortBy = "recency"
        };

        var result = await SearchMemoriesInternal(searchRequest, ct);

        return new PagedResult<MemoryItem>
        {
            NextCursor = null, // No pagination in search API
            Items = result.Episodes.Select(e => new MemoryItem
            {
                Id = e.Id,
                Content = e.Content ?? string.Empty,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt,
                Metadata = e.Metadata,
                Relationships = e.Statements?.Select(s => new Relationship
                {
                    Type = s.Predicate ?? "related_to",
                    TargetId = s.Object ?? string.Empty
                }).ToList()
            }).ToList()
        };
    }

    /// <summary>
    /// Export all data using multiple strategies:
    /// 1. Try GET /v1/episodes (list all)
    /// 2. Try GET /v1/graph/clustered (graph data)
    /// 3. Fallback to paginated search
    /// </summary>
    public async Task<McpExportResponse?> ExportAll(
        string? cursor = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Exporting all data - trying multiple endpoints");

        var allMemories = new List<McpMemory>();
        var allEntities = new List<McpEntity>();
        var allRelations = new List<McpRelation>();

        // Strategy 1: Try GET /v1/episodes (list all episodes)
        try
        {
            _logger.LogInformation("Trying GET /v1/episodes...");
            var episodesResponse = await _httpClient.GetAsync("v1/episodes", ct);
            if (episodesResponse.IsSuccessStatusCode)
            {
                var content = await episodesResponse.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("Episodes endpoint returned {Length} chars", content.Length);
                var episodes = JsonSerializer.Deserialize<List<CoreEpisode>>(content, _jsonOptions) ?? [];
                _logger.LogInformation("Found {Count} episodes via /v1/episodes", episodes.Count);
                allMemories.AddRange(episodes.Select(MapEpisodeToMemory));
            }
            else
            {
                var error = await episodesResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("/v1/episodes returned {Status}: {Error}", episodesResponse.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "/v1/episodes failed");
        }

        // Strategy 2: Try GET /v1/graph/clustered (full graph)
        try
        {
            _logger.LogInformation("Trying GET /v1/graph/clustered...");
            var graphResponse = await _httpClient.GetAsync("v1/graph/clustered", ct);
            if (graphResponse.IsSuccessStatusCode)
            {
                var content = await graphResponse.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("Graph endpoint returned {Length} chars", content.Length);

                // Save raw response for analysis
                var rawPath = Path.Combine(_config.ExportPath, "raw-graph-response.json");
                Directory.CreateDirectory(_config.ExportPath);
                await File.WriteAllTextAsync(rawPath, content, ct);
                _logger.LogInformation("Saved raw graph response to {Path}", rawPath);

                // Log first 2000 chars to understand structure
                var preview = content.Length > 2000 ? content[..2000] : content;
                _logger.LogInformation("Graph response preview: {Preview}", preview);

                // Try parsing as different structures
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                _logger.LogInformation("Graph root is {Kind}, has {Count} properties",
                    root.ValueKind, root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Count() : 0);

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject().Take(10))
                    {
                        _logger.LogInformation("  Property '{Name}': {Kind}", prop.Name, prop.Value.ValueKind);
                    }
                }

                // Try standard structure
                var graph = JsonSerializer.Deserialize<CoreGraphResponse>(content, _jsonOptions);
                if (graph != null)
                {
                    _logger.LogInformation("Parsed {Entities} entities, {Relations} relations via /v1/graph/clustered",
                        graph.Entities?.Count ?? 0, graph.Relations?.Count ?? 0);

                    if (graph.Entities != null)
                    {
                        allEntities.AddRange(graph.Entities.Select(e => new McpEntity
                        {
                            Name = e.Name ?? string.Empty,
                            EntityType = e.Type ?? "UNKNOWN",
                            Observations = e.Observations
                        }));
                    }
                    if (graph.Relations != null)
                    {
                        allRelations.AddRange(graph.Relations.Select(r => new McpRelation
                        {
                            From = r.From ?? string.Empty,
                            To = r.To ?? string.Empty,
                            RelationType = r.Type ?? "related_to"
                        }));
                    }
                }

                // Parse CORE's triplets format: { success: true, data: { triplets: [...] } }
                if (root.TryGetProperty("data", out var dataElem) &&
                    dataElem.TryGetProperty("triplets", out var tripletsElem) &&
                    tripletsElem.ValueKind == JsonValueKind.Array)
                {
                    var tripletCount = tripletsElem.GetArrayLength();
                    _logger.LogInformation("Found 'data.triplets' array with {Count} items", tripletCount);

                    var seenEpisodes = new HashSet<string>();
                    var seenEntities = new HashSet<string>();

                    foreach (var triplet in tripletsElem.EnumerateArray())
                    {
                        // Extract sourceNode (Episode)
                        if (triplet.TryGetProperty("sourceNode", out var sourceNode))
                        {
                            var labels = sourceNode.TryGetProperty("labels", out var labelsElem)
                                ? labelsElem.EnumerateArray().Select(l => l.GetString()).ToList()
                                : new List<string?>();

                            var nodeType = sourceNode.TryGetProperty("attributes", out var attrs) &&
                                          attrs.TryGetProperty("nodeType", out var nt)
                                ? nt.GetString()
                                : labels.FirstOrDefault();

                            var uuid = attrs.TryGetProperty("episodeUuid", out var euuid) ? euuid.GetString() :
                                       sourceNode.TryGetProperty("uuid", out var suuid) ? suuid.GetString() : null;

                            if (nodeType == "Episode" && !string.IsNullOrEmpty(uuid) && seenEpisodes.Add(uuid))
                            {
                                var episodeContent = attrs.TryGetProperty("content", out var contentElem)
                                    ? contentElem.GetString()
                                    : null;

                                // Get timestamp from sourceNode.createdAt
                                DateTime createdAt = DateTime.UtcNow;
                                if (sourceNode.TryGetProperty("createdAt", out var createdAtElem))
                                {
                                    if (DateTime.TryParse(createdAtElem.GetString(), out var parsed))
                                        createdAt = parsed;
                                }

                                if (!string.IsNullOrEmpty(episodeContent))
                                {
                                    allMemories.Add(new McpMemory
                                    {
                                        Id = uuid,
                                        Content = episodeContent,
                                        CreatedAt = createdAt
                                    });
                                }
                            }
                        }

                        // Extract targetNode (Entity) - entities are in targetNode with labels: ['Entity']
                        if (triplet.TryGetProperty("targetNode", out var targetNode))
                        {
                            var targetLabels = targetNode.TryGetProperty("labels", out var tlElem)
                                ? tlElem.EnumerateArray().Select(l => l.GetString()).ToList()
                                : new List<string?>();

                            if (targetLabels.Contains("Entity"))
                            {
                                var entityName = targetNode.TryGetProperty("name", out var tnElem)
                                    ? tnElem.GetString()
                                    : null;

                                if (!string.IsNullOrEmpty(entityName) && seenEntities.Add(entityName))
                                {
                                    var entityType = targetNode.TryGetProperty("attributes", out var tattrs) &&
                                                    tattrs.TryGetProperty("entityType", out var tetElem)
                                        ? tetElem.GetString()
                                        : "UNKNOWN";

                                    allEntities.Add(new McpEntity
                                    {
                                        Name = entityName,
                                        EntityType = entityType ?? "UNKNOWN"
                                    });
                                }
                            }
                        }

                        // Extract edge (relation type) - HAS_SUBJECT, HAS_PREDICATE, HAS_OBJECT
                        if (triplet.TryGetProperty("edge", out var edge) &&
                            triplet.TryGetProperty("sourceNode", out var sn2) &&
                            triplet.TryGetProperty("targetNode", out var tn2))
                        {
                            var edgeType = edge.TryGetProperty("type", out var etElem)
                                ? etElem.GetString()
                                : null;
                            var sourceId = sn2.TryGetProperty("uuid", out var sidElem)
                                ? sidElem.GetString()
                                : null;
                            var targetName = tn2.TryGetProperty("name", out var tnElem2)
                                ? tnElem2.GetString()
                                : null;

                            // Create relation from episode to entity
                            if (!string.IsNullOrEmpty(sourceId) && !string.IsNullOrEmpty(targetName) && !string.IsNullOrEmpty(edgeType))
                            {
                                allRelations.Add(new McpRelation
                                {
                                    From = sourceId, // Episode UUID
                                    To = targetName, // Entity name
                                    RelationType = edgeType
                                });
                            }
                        }
                    }

                    _logger.LogInformation("Extracted {Episodes} episodes, {Entities} entities, {Relations} relations from triplets",
                        allMemories.Count, allEntities.Count, allRelations.Count);
                }
            }
            else
            {
                var error = await graphResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("/v1/graph/clustered returned {Status}: {Error}", graphResponse.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "/v1/graph/clustered failed");
        }

        // Note: Entity-based search is slow (3746 entities = hours of API calls)
        // We already have episodes from the graph, so skip entity search

        // Strategy 4: Try wildcard search if we still have no memories
        if (allMemories.Count == 0)
        {
            _logger.LogInformation("Trying wildcard search...");
            try
            {
                var searchRequest = new CoreSearchRequest
                {
                    Query = "*",
                    Limit = 100,
                    SortBy = "recency"
                };

                var result = await SearchMemoriesInternal(searchRequest, ct);
                _logger.LogInformation("Wildcard search: {Count} episodes", result.Episodes.Count);
                allMemories.AddRange(result.Episodes.Select(MapEpisodeToMemory));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Wildcard search failed");
            }
        }

        return new McpExportResponse
        {
            NextCursor = null,
            TotalCount = allMemories.Count,
            Memories = allMemories,
            Entities = allEntities,
            Relations = allRelations
        };
    }

    private McpMemory MapEpisodeToMemory(CoreEpisode e) => new()
    {
        Id = e.Id,
        Content = e.Content ?? string.Empty,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        Metadata = e.Metadata,
        Entities = e.Entities?.Select(ent => new McpEntity
        {
            Name = ent.Name ?? string.Empty,
            EntityType = ent.Type ?? "UNKNOWN"
        }).ToList(),
        Relations = e.Statements?.Select(s => new McpRelation
        {
            From = s.Subject ?? string.Empty,
            To = s.Object ?? string.Empty,
            RelationType = s.Predicate ?? "related_to"
        }).ToList()
    };

    /// <summary>
    /// Search memories using POST /v1/search
    /// </summary>
    public async Task<McpMemorySearchResponse?> SearchMemories(
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Searching memories: query={Query}, limit={Limit}", query, limit);

        var searchRequest = new CoreSearchRequest
        {
            Query = query,
            Limit = limit
        };

        var result = await SearchMemoriesInternal(searchRequest, ct);

        return new McpMemorySearchResponse
        {
            NextCursor = result.NextCursor,
            TotalCount = result.TotalCount,
            Memories = result.Episodes.Select(e => new McpMemory
            {
                Id = e.Id,
                Content = e.Content ?? string.Empty,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt,
                Score = e.Score
            }).ToList()
        };
    }

    /// <summary>
    /// Internal search implementation
    /// POST /v1/search
    /// </summary>
    private async Task<CoreSearchResponse> SearchMemoriesInternal(
        CoreSearchRequest request,
        CancellationToken ct)
    {
        var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
        _logger.LogInformation("Search request body: {Content}", jsonContent);

        var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await ExecuteWithRetry(async () =>
        {
            var result = await _httpClient.PostAsync("v1/search", httpContent, ct);

            if (!result.IsSuccessStatusCode)
            {
                var errorContent = await result.Content.ReadAsStringAsync(ct);
                _logger.LogError("Search API error {StatusCode}: {Content}",
                    result.StatusCode, errorContent);
            }

            result.EnsureSuccessStatusCode();
            return result;
        }, ct);

        var content = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Search response: {Content}", content.Length > 500 ? content[..500] + "..." : content);

        return JsonSerializer.Deserialize<CoreSearchResponse>(content, _jsonOptions)
            ?? new CoreSearchResponse();
    }

    private async Task<HttpResponseMessage> ExecuteWithRetry(
        Func<Task<HttpResponseMessage>> action,
        CancellationToken ct,
        int maxRetries = 5)
    {
        int retryCount = 0;
        TimeSpan delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (retryCount < maxRetries)
            {
                var statusCode = ex.StatusCode;

                // Handle 429 (Rate Limit) and 5xx errors
                if (statusCode == HttpStatusCode.TooManyRequests ||
                    (statusCode >= HttpStatusCode.InternalServerError &&
                     statusCode < (HttpStatusCode)600))
                {
                    retryCount++;

                    // Exponential backoff with jitter
                    var jitter = Random.Shared.Next(0, 1000);
                    var backoff = TimeSpan.FromMilliseconds(
                        delay.TotalMilliseconds * Math.Pow(2, retryCount) + jitter);

                    _logger.LogWarning(
                        "Request failed with {StatusCode}. Retry {Retry}/{MaxRetries} after {Delay}ms",
                        statusCode, retryCount, maxRetries, (int)backoff.TotalMilliseconds);

                    await Task.Delay(backoff, ct);
                    continue;
                }

                throw;
            }
            catch (TaskCanceledException) when (retryCount < maxRetries)
            {
                retryCount++;

                var jitter = Random.Shared.Next(0, 1000);
                var backoff = TimeSpan.FromMilliseconds(
                    delay.TotalMilliseconds * Math.Pow(2, retryCount) + jitter);

                _logger.LogWarning(
                    "Request timed out. Retry {Retry}/{MaxRetries} after {Delay}ms",
                    retryCount, maxRetries, (int)backoff.TotalMilliseconds);

                await Task.Delay(backoff, ct);
            }
        }
    }
}
