// =============================================================================
// Project Context Memory Example
// =============================================================================
// Demonstrates project-specific memory isolation using SerialMemory.
// Ideal for IDEs, development tools, or AI assistants working across projects.
//
// Run: dotnet run
// =============================================================================

using SerialMemory.Client;

Console.WriteLine("🗂️ Project Context Memory Example");
Console.WriteLine("==================================\n");

// Initialize client
var client = new SerialMemoryClient(new SerialMemoryOptions
{
    BaseUrl = Environment.GetEnvironmentVariable("SERIALMEMORY_URL") ?? "http://localhost:5000",
    ApiKey = Environment.GetEnvironmentVariable("SERIALMEMORY_API_KEY") ?? "demo-key",
    DefaultSource = "project-context-demo"
});

// =============================================================================
// Project Memory Manager - Wrapper for project-scoped operations
// =============================================================================

class ProjectMemory
{
    private readonly SerialMemoryClient _client;
    private readonly string _projectId;
    private readonly string _projectName;

    public ProjectMemory(SerialMemoryClient client, string projectId, string projectName)
    {
        _client = client;
        _projectId = projectId;
        _projectName = projectName;
    }

    public async Task<Guid> StoreAsync(string content, string category, Dictionary<string, object>? extra = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["project_id"] = _projectId,
            ["project_name"] = _projectName,
            ["category"] = category,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
        if (extra != null)
            foreach (var kvp in extra)
                metadata[kvp.Key] = kvp.Value;

        var result = await _client.IngestAsync(
            content: $"[{_projectName}] {content}",
            source: $"project-{_projectId}",
            metadata: metadata
        );
        return result.MemoryId;
    }

    public async Task<List<MemoryMatch>> SearchAsync(string query, int limit = 5)
    {
        var contextualQuery = $"{_projectName} {query}";
        var results = await _client.SearchAsync(contextualQuery, limit: limit * 2, threshold: 0.3f);

        return results.Memories
            .Where(m => m.Source == $"project-{_projectId}" ||
                       m.Content.StartsWith($"[{_projectName}]"))
            .Take(limit)
            .ToList();
    }

    public Task<Guid> StoreDecisionAsync(string title, string context, string decision, string consequences) =>
        StoreAsync($"""
            Architecture Decision: {title}

            Context:
            {context}

            Decision:
            {decision}

            Consequences:
            {consequences}
            """, "architecture-decision", new() { ["adr_title"] = title });

    public Task<Guid> StoreBugFixAsync(string issue, string rootCause, string solution, string[] files) =>
        StoreAsync($"""
            Bug Fix: {issue}

            Root Cause:
            {rootCause}

            Solution:
            {solution}

            Affected Files:
            {string.Join("\n", files.Select(f => $"- {f}"))}
            """, "bug-fix", new() { ["files"] = files });

    public Task<Guid> StorePatternAsync(string name, string description, string example) =>
        StoreAsync($"""
            Code Pattern: {name}

            Description:
            {description}

            Example:
            ```
            {example}
            ```
            """, "code-pattern", new() { ["pattern_name"] = name });
}

// =============================================================================
// Create Two Projects
// =============================================================================

var backendProject = new ProjectMemory(client, "ecommerce-backend", "E-commerce Backend");
var frontendProject = new ProjectMemory(client, "ecommerce-frontend", "E-commerce Frontend");

Console.WriteLine("📦 Setting up two projects with isolated memory...\n");

// =============================================================================
// Backend Project Memories
// =============================================================================

Console.WriteLine("Project: E-commerce Backend");
Console.WriteLine("----------------------------");

await backendProject.StoreDecisionAsync(
    "Use Stripe for Payments",
    "Need payment processing with support for subscriptions and multi-currency.",
    "Adopt Stripe with their .NET SDK. Use webhooks for async events.",
    "2.9% + $0.30 per transaction. Need PCI compliance review."
);
Console.WriteLine("✅ Stored ADR: Payment processor");

await backendProject.StoreBugFixAsync(
    "Order total calculation off by $0.01",
    "Floating point arithmetic caused rounding errors when calculating discounts.",
    "Switched to decimal type for all monetary calculations. Round only at final display.",
    ["Services/OrderService.cs", "Models/Order.cs", "Tests/OrderCalculationTests.cs"]
);
Console.WriteLine("✅ Stored bug fix: Rounding error");

await backendProject.StorePatternAsync(
    "CQRS with Dapper",
    "Separate read and write models. Queries use Dapper for performance, commands use EF Core.",
    """
    // Query (Dapper)
    public class GetOrderQuery : IQuery<OrderDto>
    {
        public async Task<OrderDto> ExecuteAsync(IDbConnection db)
            => await db.QueryFirstAsync<OrderDto>(
                "SELECT * FROM orders WHERE id = @Id", new { Id });
    }

    // Command (EF Core)
    public class CreateOrderCommand : ICommand
    {
        public async Task ExecuteAsync(AppDbContext db)
        {
            db.Orders.Add(new Order { ... });
            await db.SaveChangesAsync();
        }
    }
    """
);
Console.WriteLine("✅ Stored pattern: CQRS with Dapper\n");

// =============================================================================
// Frontend Project Memories
// =============================================================================

Console.WriteLine("Project: E-commerce Frontend");
Console.WriteLine("-----------------------------");

await frontendProject.StoreDecisionAsync(
    "Use Next.js App Router",
    "Need SSR for SEO, good DX, and seamless deployment to Vercel.",
    "Adopt Next.js 14 with App Router. Use Server Components by default.",
    "Team needs training on RSC patterns. Some library compatibility issues."
);
Console.WriteLine("✅ Stored ADR: Framework selection");

await frontendProject.StoreBugFixAsync(
    "Cart count not updating after add",
    "State mutation bug - was modifying cart array directly instead of creating new reference.",
    "Used immer for immutable state updates. Added React.StrictMode to catch mutations.",
    ["store/cartSlice.ts", "components/CartButton.tsx"]
);
Console.WriteLine("✅ Stored bug fix: State mutation");

await frontendProject.StorePatternAsync(
    "Optimistic UI Updates",
    "Update UI immediately, revert on error. Improves perceived performance.",
    """
    async function addToCart(item) {
      // Optimistically update
      const prevCart = cart;
      setCart([...cart, item]);

      try {
        await api.addToCart(item);
      } catch (error) {
        // Revert on failure
        setCart(prevCart);
        toast.error('Failed to add item');
      }
    }
    """
);
Console.WriteLine("✅ Stored pattern: Optimistic UI\n");

// =============================================================================
// Project-Scoped Search
// =============================================================================

Console.WriteLine("🔍 Project-scoped search demo...\n");

// Search in backend project
Console.WriteLine("Query 'payment' in Backend:");
var backendPayment = await backendProject.SearchAsync("payment", 3);
if (backendPayment.Count > 0)
{
    foreach (var m in backendPayment)
        Console.WriteLine($"  [{m.Similarity:P0}] {m.Content.Split('\n')[0]}");
}
else
{
    Console.WriteLine("  (No results)");
}

Console.WriteLine();

// Same query in frontend - should find nothing about payments
Console.WriteLine("Query 'payment' in Frontend:");
var frontendPayment = await frontendProject.SearchAsync("payment", 3);
if (frontendPayment.Count > 0)
{
    foreach (var m in frontendPayment)
        Console.WriteLine($"  [{m.Similarity:P0}] {m.Content.Split('\n')[0]}");
}
else
{
    Console.WriteLine("  (No results - payment is backend concern)");
}

Console.WriteLine();

// Search for state management in frontend
Console.WriteLine("Query 'state bug' in Frontend:");
var frontendState = await frontendProject.SearchAsync("state bug", 3);
foreach (var m in frontendState)
    Console.WriteLine($"  [{m.Similarity:P0}] {m.Content.Split('\n')[0]}");

// =============================================================================
// Cross-Project Search
// =============================================================================

Console.WriteLine("\n🌐 Cross-project search (all projects)...\n");

var globalResults = await client.SearchAsync(
    query: "code patterns best practices",
    mode: SearchMode.Hybrid,
    limit: 5
);

Console.WriteLine("Query 'code patterns' (global):");
foreach (var m in globalResults.Memories)
{
    var tag = m.Content.Split(']')[0] + "]";
    var content = m.Content.Replace(tag, "").Trim().Split('\n')[0];
    Console.WriteLine($"  {tag} [{m.Similarity:P0}] {content}");
}

// =============================================================================
// Summary
// =============================================================================

Console.WriteLine("\n📊 Summary...\n");

var stats = await client.GetGraphStatisticsAsync();
Console.WriteLine($"  Total memories: {stats.TotalMemories}");
Console.WriteLine($"  Total entities: {stats.TotalEntities}");
Console.WriteLine($"  Total relationships: {stats.TotalRelationships}");

Console.WriteLine("\n✨ Demo complete!");
Console.WriteLine("Key points:");
Console.WriteLine("  • Each project has isolated memory context");
Console.WriteLine("  • Searches are scoped by project");
Console.WriteLine("  • Cross-project search is available when needed");
Console.WriteLine("  • Use metadata for categorization and filtering");
