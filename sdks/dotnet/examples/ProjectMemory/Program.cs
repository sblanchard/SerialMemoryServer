// =============================================================================
// SerialMemory SDK Example: Project Context Memory
// =============================================================================
// This example demonstrates how to use SerialMemory for project-specific
// context isolation. It shows:
//
// 1. Using metadata to associate memories with specific projects
// 2. Filtering searches by project context
// 3. Multi-tenant memory isolation patterns
// 4. Project-scoped knowledge graphs
//
// Use case: A development tool that maintains separate memory contexts
// for different projects/repositories the user is working on.
//
// Run with: dotnet run
// =============================================================================

using SerialMemory.Client;

// -----------------------------------------------------------------------------
// Project Memory Manager - Wrapper for project-scoped memory operations
// -----------------------------------------------------------------------------

/// <summary>
/// Manages project-scoped memories using SerialMemory.
/// Each project gets isolated memory context through metadata filtering.
/// </summary>
class ProjectMemoryManager
{
    private readonly SerialMemoryClient _client;
    private readonly string _projectId;
    private readonly string _projectName;

    public ProjectMemoryManager(SerialMemoryClient client, string projectId, string projectName)
    {
        _client = client;
        _projectId = projectId;
        _projectName = projectName;
    }

    /// <summary>
    /// Store a memory scoped to this project.
    /// </summary>
    public async Task<Guid> StoreAsync(string content, string category, Dictionary<string, object>? extraMetadata = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["project_id"] = _projectId,
            ["project_name"] = _projectName,
            ["category"] = category,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };

        // Merge extra metadata if provided
        if (extraMetadata != null)
        {
            foreach (var kvp in extraMetadata)
            {
                metadata[kvp.Key] = kvp.Value;
            }
        }

        var result = await _client.IngestAsync(
            content: $"[{_projectName}] {content}",
            source: $"project-{_projectId}",
            metadata: metadata
        );

        return result.MemoryId;
    }

    /// <summary>
    /// Search memories within this project's context.
    /// </summary>
    public async Task<List<MemoryMatch>> SearchAsync(string query, int limit = 5)
    {
        // Search with project context in the query for better relevance
        var contextualQuery = $"{_projectName} {query}";

        var results = await _client.SearchAsync(
            query: contextualQuery,
            mode: SearchMode.Hybrid,
            limit: limit * 2, // Fetch extra to filter
            threshold: 0.4f
        );

        // Filter to only this project's memories
        // In production, you'd use server-side filtering with metadata queries
        return results.Memories
            .Where(m => m.Source == $"project-{_projectId}" ||
                       m.Content.StartsWith($"[{_projectName}]"))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Store an architecture decision record (ADR).
    /// </summary>
    public Task<Guid> StoreDecisionAsync(string title, string context, string decision, string consequences)
    {
        var content = $"""
            Architecture Decision: {title}

            Context:
            {context}

            Decision:
            {decision}

            Consequences:
            {consequences}
            """;

        return StoreAsync(content, "architecture-decision", new Dictionary<string, object>
        {
            ["adr_title"] = title
        });
    }

    /// <summary>
    /// Store a bug fix record for future reference.
    /// </summary>
    public Task<Guid> StoreBugFixAsync(string issue, string rootCause, string solution, string[] affectedFiles)
    {
        var content = $"""
            Bug Fix: {issue}

            Root Cause:
            {rootCause}

            Solution:
            {solution}

            Affected Files:
            {string.Join("\n", affectedFiles.Select(f => $"- {f}"))}
            """;

        return StoreAsync(content, "bug-fix", new Dictionary<string, object>
        {
            ["affected_files"] = affectedFiles
        });
    }

    /// <summary>
    /// Store a code pattern or convention.
    /// </summary>
    public Task<Guid> StorePatternAsync(string name, string description, string example)
    {
        var content = $"""
            Code Pattern: {name}

            Description:
            {description}

            Example:
            ```
            {example}
            ```
            """;

        return StoreAsync(content, "code-pattern", new Dictionary<string, object>
        {
            ["pattern_name"] = name
        });
    }
}

// -----------------------------------------------------------------------------
// Demo Application
// -----------------------------------------------------------------------------

var client = new SerialMemoryClient(new SerialMemoryOptions
{
    BaseUrl = Environment.GetEnvironmentVariable("SERIALMEMORY_URL") ?? "http://localhost:5000",
    ApiKey = Environment.GetEnvironmentVariable("SERIALMEMORY_API_KEY") ?? "demo-key",
    DefaultSource = "project-memory-demo"
});

Console.WriteLine("🗂️ SerialMemory Project Context Memory Demo");
Console.WriteLine("============================================\n");

// -----------------------------------------------------------------------------
// Simulate Two Different Projects
// -----------------------------------------------------------------------------

// Project A: E-commerce Backend
var ecommerceProject = new ProjectMemoryManager(
    client,
    projectId: "ecommerce-api",
    projectName: "E-commerce Backend"
);

// Project B: Mobile App
var mobileProject = new ProjectMemoryManager(
    client,
    projectId: "mobile-app",
    projectName: "Mobile App"
);

Console.WriteLine("📦 Setting up two projects with isolated memory contexts...\n");

// -----------------------------------------------------------------------------
// Store E-commerce Project Memories
// -----------------------------------------------------------------------------
Console.WriteLine("Project: E-commerce Backend");
Console.WriteLine("----------------------------");

await ecommerceProject.StoreDecisionAsync(
    title: "Use Stripe for payments",
    context: "Need a payment processor that supports multiple currencies and subscription billing.",
    decision: "Adopt Stripe as our payment processor with their .NET SDK.",
    consequences: "Requires PCI compliance review. 2.9% + $0.30 per transaction."
);
Console.WriteLine("✅ Stored ADR: Payment processor selection");

await ecommerceProject.StoreBugFixAsync(
    issue: "Cart totals incorrect with percentage discounts",
    rootCause: "Discount calculation was using integer division, losing decimal precision.",
    solution: "Changed to decimal arithmetic and added rounding at the final step only.",
    affectedFiles: ["Services/CartService.cs", "Models/CartItem.cs"]
);
Console.WriteLine("✅ Stored bug fix: Cart calculation issue");

await ecommerceProject.StorePatternAsync(
    name: "Repository Pattern",
    description: "All database access goes through repository interfaces. No direct DbContext usage in services.",
    example: """
        public class OrderService
        {
            private readonly IOrderRepository _orders;

            public async Task<Order> GetAsync(Guid id)
                => await _orders.GetByIdAsync(id);
        }
        """
);
Console.WriteLine("✅ Stored code pattern: Repository Pattern\n");

// -----------------------------------------------------------------------------
// Store Mobile App Project Memories
// -----------------------------------------------------------------------------
Console.WriteLine("Project: Mobile App");
Console.WriteLine("--------------------");

await mobileProject.StoreDecisionAsync(
    title: "Use React Native",
    context: "Need cross-platform mobile support with a shared codebase.",
    decision: "Use React Native with TypeScript for iOS and Android development.",
    consequences: "Team needs React Native training. Some native modules may require platform-specific code."
);
Console.WriteLine("✅ Stored ADR: Framework selection");

await mobileProject.StoreBugFixAsync(
    issue: "App crashes on Android when switching tabs rapidly",
    rootCause: "Race condition in navigation state updates. Multiple screens mounting simultaneously.",
    solution: "Added debouncing to tab switches and proper cleanup in useEffect hooks.",
    affectedFiles: ["src/navigation/TabNavigator.tsx", "src/hooks/useTabSwitch.ts"]
);
Console.WriteLine("✅ Stored bug fix: Tab navigation crash");

await mobileProject.StorePatternAsync(
    name: "Custom Hook Pattern",
    description: "Extract reusable logic into custom hooks prefixed with 'use'. Keep components thin.",
    example: """
        // Bad: Logic in component
        const MyComponent = () => {
            const [data, setData] = useState(null);
            useEffect(() => { fetch()... }, []);
            return <View>...</View>;
        };

        // Good: Logic in hook
        const useMyData = () => {
            const [data, setData] = useState(null);
            useEffect(() => { fetch()... }, []);
            return data;
        };
        """
);
Console.WriteLine("✅ Stored code pattern: Custom Hook Pattern\n");

// -----------------------------------------------------------------------------
// Demonstrate Project-Scoped Search
// -----------------------------------------------------------------------------
Console.WriteLine("🔍 Demonstrating project-scoped search...\n");

// Search within E-commerce project
Console.WriteLine("Query: 'payment' in E-commerce Backend:");
var ecommerceResults = await ecommerceProject.SearchAsync("payment", limit: 3);
foreach (var memory in ecommerceResults)
{
    var preview = memory.Content.Split('\n')[0];
    Console.WriteLine($"   [{memory.Similarity:P0}] {preview}");
}

Console.WriteLine();

// Same query in Mobile App project - should return different/no results
Console.WriteLine("Query: 'payment' in Mobile App:");
var mobileResults = await mobileProject.SearchAsync("payment", limit: 3);
if (mobileResults.Count == 0)
{
    Console.WriteLine("   (No results - payment decisions are in E-commerce project)");
}
else
{
    foreach (var memory in mobileResults)
    {
        var preview = memory.Content.Split('\n')[0];
        Console.WriteLine($"   [{memory.Similarity:P0}] {preview}");
    }
}

Console.WriteLine();

// Search for bug fixes in Mobile App
Console.WriteLine("Query: 'crash navigation bug' in Mobile App:");
var bugResults = await mobileProject.SearchAsync("crash navigation bug", limit: 3);
foreach (var memory in bugResults)
{
    var preview = memory.Content.Split('\n')[0];
    Console.WriteLine($"   [{memory.Similarity:P0}] {preview}");
}

// -----------------------------------------------------------------------------
// Cross-Project Search (Global Context)
// -----------------------------------------------------------------------------
Console.WriteLine("\n🌐 Cross-project search (all projects)...\n");

// Direct client search without project filtering
var globalResults = await client.SearchAsync(
    query: "code patterns best practices",
    mode: SearchMode.Hybrid,
    limit: 5
);

Console.WriteLine("Query: 'code patterns best practices' (all projects):");
foreach (var memory in globalResults.Memories)
{
    var projectTag = memory.Content.StartsWith("[") ?
        memory.Content.Split(']')[0] + "]" :
        $"({memory.Source})";
    var preview = memory.Content.Replace(projectTag, "").Trim().Split('\n')[0];
    Console.WriteLine($"   {projectTag} [{memory.Similarity:P0}] {preview}");
}

// -----------------------------------------------------------------------------
// Summary
// -----------------------------------------------------------------------------
Console.WriteLine("\n📊 Project Memory Summary");
Console.WriteLine("-------------------------");

var stats = await client.GetGraphStatisticsAsync();
Console.WriteLine($"Total memories across all projects: {stats.TotalMemories}");
Console.WriteLine($"Total entities extracted: {stats.TotalEntities}");
Console.WriteLine($"Total relationships discovered: {stats.TotalRelationships}");

Console.WriteLine("\n✨ Demo complete!");
Console.WriteLine("Key takeaways:");
Console.WriteLine("  • Use metadata (project_id) to scope memories to projects");
Console.WriteLine("  • Prefix content with project name for better search relevance");
Console.WriteLine("  • Use source field for filtering at query time");
Console.WriteLine("  • Cross-project search is still possible when needed");
