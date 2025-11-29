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
        Projects = ["SerialMemory", "FlexPilot", "FlexHPSDR", "RebateX"];
    }

    private async Task LoadGraphDataAsync()
    {
        try
        {
            var client = _apiClient.CreateClient();

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
                        return;
                    }
                }

                LoadSampleGraphData();
            }
        }
        catch (HttpRequestException)
        {
            LoadSampleGraphData();
        }
    }

    private void LoadSampleGraphData()
    {
        IsSampleData = true;
        // Sample graph data in force-graph-3d format
        GraphDataJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new { id = "user-service", name = "UserService", type = "service", risk = 0.2, group = 1 },
                new { id = "auth-module", name = "AuthModule", type = "module", risk = 0.8, group = 1 },
                new { id = "database", name = "PostgreSQL", type = "database", risk = 0.1, group = 2 },
                new { id = "redis", name = "Redis Cache", type = "cache", risk = 0.15, group = 2 },
                new { id = "api-gateway", name = "API Gateway", type = "gateway", risk = 0.4, group = 3 },
                new { id = "payment-service", name = "PaymentService", type = "service", risk = 0.9, group = 1 },
                new { id = "notification", name = "NotificationService", type = "service", risk = 0.3, group = 1 },
                new { id = "queue", name = "RabbitMQ", type = "queue", risk = 0.2, group = 2 },
                new { id = "config", name = "ConfigService", type = "config", risk = 0.5, group = 3 },
                new { id = "logging", name = "LoggingService", type = "service", risk = 0.1, group = 3 },
                new { id = "metrics", name = "MetricsCollector", type = "monitoring", risk = 0.1, group = 3 },
                new { id = "user-entity", name = "User", type = "entity", risk = 0.0, group = 4 },
                new { id = "order-entity", name = "Order", type = "entity", risk = 0.0, group = 4 },
                new { id = "product-entity", name = "Product", type = "entity", risk = 0.0, group = 4 }
            },
            links = new[]
            {
                new { source = "api-gateway", target = "user-service", type = "calls", critical = false },
                new { source = "api-gateway", target = "payment-service", type = "calls", critical = true },
                new { source = "user-service", target = "auth-module", type = "uses", critical = true },
                new { source = "user-service", target = "database", type = "reads", critical = false },
                new { source = "user-service", target = "redis", type = "caches", critical = false },
                new { source = "payment-service", target = "database", type = "writes", critical = true },
                new { source = "payment-service", target = "notification", type = "triggers", critical = false },
                new { source = "notification", target = "queue", type = "publishes", critical = false },
                new { source = "auth-module", target = "redis", type = "stores", critical = true },
                new { source = "config", target = "user-service", type = "configures", critical = false },
                new { source = "config", target = "payment-service", type = "configures", critical = false },
                new { source = "logging", target = "user-service", type = "monitors", critical = false },
                new { source = "logging", target = "payment-service", type = "monitors", critical = false },
                new { source = "metrics", target = "api-gateway", type = "collects", critical = false },
                new { source = "user-service", target = "user-entity", type = "manages", critical = false },
                new { source = "payment-service", target = "order-entity", type = "manages", critical = false },
                new { source = "order-entity", target = "product-entity", type = "contains", critical = false }
            },
            overlays = new
            {
                risks = new[]
                {
                    new { nodeId = "auth-module", level = "high", message = "Missing rate limiting" },
                    new { nodeId = "payment-service", level = "critical", message = "SQL injection vulnerability" }
                },
                criticalPaths = new[] { "api-gateway", "payment-service", "database" }
            }
        });
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
