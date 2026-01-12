using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SerialMemory.Web.Services;

namespace SerialMemory.Web.Pages.Dashboard;

[Authorize]
public sealed class VisualizeModel : PageModel
{
    private readonly ApiClientService _apiClient;
    private readonly AppConfig _appConfig;

    public VisualizeModel(ApiClientService apiClient, AppConfig appConfig)
    {
        _apiClient = apiClient;
        _appConfig = appConfig;
    }

    public string ApiBaseUrl => _appConfig.ApiBaseUrl;

    // SignalR access token (from InternalTokenMiddleware)
    public string? SignalRToken => HttpContext.Items["InternalToken"] as string;

    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? MemoryId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Mode { get; set; } = "mixed";

    public string GraphDataJson { get; set; } = "{}";
    public IReadOnlyList<string> Projects { get; set; } = [];
    public bool IsSampleData { get; set; }

    public async Task OnGetAsync()
    {
        await LoadProjectsAsync();
        await LoadGraphDataAsync();
    }

    public async Task<IActionResult> OnGetGraphDataAsync()
    {
        await LoadGraphDataAsync();
        return new JsonResult(GraphDataJson);
    }

    private async Task LoadProjectsAsync()
    {
        try
        {
            // Fetch real projects from the API
            var client = _apiClient.CreateClient("Api");
            var response = await client.GetAsync("/api/graph/projects");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ProjectsResponse>();
                Projects = result?.Projects ?? [];
            }
            else
            {
                Projects = [];
            }
        }
        catch
        {
            Projects = [];
        }
    }

    private async Task LoadGraphDataAsync()
    {
        try
        {
            // Use main API client for graph endpoints
            var client = _apiClient.CreateClient("Api");

            var request = new
            {
                project = Project,
                memory_id = MemoryId?.ToString(),
                mode = Mode,
                include_overlays = true
            };

            var response = await client.PostAsJsonAsync("/api/visualize/graph", request);

            if (response.IsSuccessStatusCode)
            {
                GraphDataJson = await response.Content.ReadAsStringAsync();
                IsSampleData = false;
            }
            else
            {
                // Try getting raw graph data and format it
                var nodesTask = client.GetAsync("/api/graph/nodes?limit=50");
                var edgesTask = client.GetAsync("/api/graph/edges?limit=100");
                var statsTask = client.GetAsync("/api/graph/stats");

                await Task.WhenAll(nodesTask, edgesTask, statsTask);

                if (nodesTask.Result.IsSuccessStatusCode && edgesTask.Result.IsSuccessStatusCode)
                {
                    var nodesResult = await nodesTask.Result.Content.ReadFromJsonAsync<NodesApiResponse>();
                    var edgesResult = await edgesTask.Result.Content.ReadFromJsonAsync<EdgesApiResponse>();

                    if (nodesResult?.Items is { Count: > 0 })
                    {
                        var graphData = new
                        {
                            nodes = nodesResult.Items.Select((n, i) => new
                            {
                                n.Id,
                                n.Name,
                                type = n.Type.ToLower() switch
                                {
                                    "person" => "service",
                                    "org" => "module",
                                    "gpe" => "database",
                                    _ => "entity"
                                },
                                risk = new Random(n.Id.GetHashCode()).NextDouble() * 0.5,
                                group = i % 4 + 1
                            }),
                            links = edgesResult?.Items.Select(e => new
                            {
                                source = e.Source,
                                target = e.Target,
                                type = e.Type.ToLower().Replace(" ", "_"),
                                critical = e.Confidence > 0.8
                            }) ?? [],
                            overlays = new { risks = Array.Empty<object>(), criticalPaths = Array.Empty<string>() }
                        };

                        GraphDataJson = System.Text.Json.JsonSerializer.Serialize(graphData);
                        IsSampleData = false;
                        return;
                    }
                }

                // No data available - show empty graph, not fake sample data
                LoadEmptyGraphData();
            }
        }
        catch (HttpRequestException)
        {
            // API unavailable - show empty graph, not fake sample data
            LoadEmptyGraphData();
        }
    }

    private void LoadEmptyGraphData()
    {
        IsSampleData = false; // Not sample data, just empty
        GraphDataJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = Array.Empty<object>(),
            links = Array.Empty<object>(),
            overlays = new { risks = Array.Empty<object>(), criticalPaths = Array.Empty<string>() },
            empty = true,
            message = "No graph data available. Add some memories to see your knowledge graph."
        });
    }

    private sealed class ProjectsResponse
    {
        public IReadOnlyList<string> Projects { get; init; } = [];
    }

    private sealed class NodesApiResponse
    {
        public IReadOnlyList<NodeItem> Items { get; init; } = [];
    }

    private sealed class NodeItem
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class EdgesApiResponse
    {
        public IReadOnlyList<EdgeItem> Items { get; init; } = [];
    }

    private sealed class EdgeItem
    {
        public string Id { get; init; } = "";
        public string Source { get; init; } = "";
        public string Target { get; init; } = "";
        public string Type { get; init; } = "";
        public float Confidence { get; init; }
    }
}
