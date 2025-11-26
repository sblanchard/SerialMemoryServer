using SerialMemory.Sdk;

Console.WriteLine("=== SerialMemory Second Brain Example ===\n");

// Configure the client
// In production, use environment variables or secure configuration
var baseUrl = Environment.GetEnvironmentVariable("SERIALMEMORY_URL") ?? "http://localhost:5000";
var apiKey = Environment.GetEnvironmentVariable("SERIALMEMORY_API_KEY") ?? "sm_live_demo_key";

using var client = new SerialMemoryClient(new SerialMemoryClientOptions
{
    BaseUrl = baseUrl,
    ApiKey = apiKey
});

// Subscribe to usage warnings
client.OnUsageWarning += warning =>
{
    var color = warning.Severity switch
    {
        "critical" => ConsoleColor.Red,
        "warning" => ConsoleColor.Yellow,
        _ => ConsoleColor.Cyan
    };
    Console.ForegroundColor = color;
    Console.WriteLine($"[USAGE WARNING] {warning.Message} ({warning.PercentUsed:F0}% used)");
    Console.ResetColor();
};

try
{
    // 1. Ingest some notes (simulating a "second brain")
    Console.WriteLine("1. Ingesting notes into your second brain...\n");

    var notes = new[]
    {
        "Today I learned about event sourcing. Key insight: events are immutable facts about what happened, not commands to do something. This makes audit trails trivial.",
        "Meeting with Sarah about the Q4 roadmap. Decision: prioritize the API refactoring over new features. Deadline is end of November.",
        "Interesting article about PostgreSQL indexes: BRIN indexes are great for time-series data because they're tiny and fast for range queries on naturally ordered data.",
        "Project idea: Build a personal knowledge graph that connects notes, meetings, and learnings. Could use semantic search to find connections.",
        "Bug fix today: The memory leak in the worker service was caused by undisposed HttpClient instances. Lesson: always use IHttpClientFactory or a single static instance."
    };

    var memoryIds = new List<string>();
    foreach (var note in notes)
    {
        var result = await client.IngestAsync(
            content: note,
            source: "second-brain-example",
            metadata: new Dictionary<string, object>
            {
                ["category"] = note.Contains("Meeting") ? "meetings" : "learnings",
                ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
            }
        );

        memoryIds.Add(result.MemoryId);
        Console.WriteLine($"  + Ingested: {note[..Math.Min(50, note.Length)]}...");
        Console.WriteLine($"    Memory ID: {result.MemoryId}");
        Console.WriteLine($"    Entities: {result.EntitiesExtracted}");
        Console.WriteLine();
    }

    // 2. Search for related memories
    Console.WriteLine("\n2. Searching your second brain...\n");

    var queries = new[]
    {
        "What did I learn about databases?",
        "meetings and decisions",
        "bugs and fixes"
    };

    foreach (var query in queries)
    {
        Console.WriteLine($"  Query: \"{query}\"");
        var search = await client.SearchAsync(query, limit: 3);

        if (search.Memories.Count == 0)
        {
            Console.WriteLine("    No results found.\n");
            continue;
        }

        foreach (var match in search.Memories)
        {
            Console.WriteLine($"    [{match.Score:P0}] {match.Content[..Math.Min(60, match.Content.Length)]}...");
            if (match.Entities?.Count > 0)
            {
                Console.WriteLine($"         Entities: {string.Join(", ", match.Entities.Select(e => $"{e.Name} ({e.Type})"))}");
            }
        }
        Console.WriteLine();
    }

    // 3. Set user persona (preferences)
    Console.WriteLine("\n3. Setting user persona...\n");

    await client.SetUserPersonaAsync(
        attributeType: "preference",
        attributeKey: "programming_language",
        attributeValue: "C#"
    );
    Console.WriteLine("  + Set preference: programming_language = C#");

    await client.SetUserPersonaAsync(
        attributeType: "skill",
        attributeKey: "databases",
        attributeValue: "PostgreSQL, SQL Server, Redis",
        confidence: 0.9f
    );
    Console.WriteLine("  + Set skill: databases = PostgreSQL, SQL Server, Redis");

    await client.SetUserPersonaAsync(
        attributeType: "goal",
        attributeKey: "current_project",
        attributeValue: "Build a personal knowledge management system"
    );
    Console.WriteLine("  + Set goal: current_project = Build PKM system");

    // 4. Retrieve user persona
    Console.WriteLine("\n4. Retrieving user persona...\n");

    var persona = await client.GetUserPersonaAsync();
    Console.WriteLine($"  User: {persona.UserId}");

    if (persona.Preferences?.Count > 0)
    {
        Console.WriteLine("  Preferences:");
        foreach (var pref in persona.Preferences)
            Console.WriteLine($"    - {pref.Key}: {pref.Value}");
    }

    if (persona.Skills?.Count > 0)
    {
        Console.WriteLine("  Skills:");
        foreach (var skill in persona.Skills)
            Console.WriteLine($"    - {skill.Key}: {skill.Value} (confidence: {skill.Confidence:P0})");
    }

    if (persona.Goals?.Count > 0)
    {
        Console.WriteLine("  Goals:");
        foreach (var goal in persona.Goals)
            Console.WriteLine($"    - {goal.Key}: {goal.Value}");
    }

    // 5. Check usage limits
    Console.WriteLine("\n5. Checking usage limits...\n");

    var limits = await client.GetLimitsAsync();
    Console.WriteLine($"  Plan: {limits.DisplayName}");
    Console.WriteLine($"  Credits: {limits.CreditsUsed}/{limits.MonthlyCredits} ({limits.CreditsRemaining} remaining)");
    Console.WriteLine($"  Cycle resets in: {limits.DaysUntilReset} days");
    Console.WriteLine($"  Rate limits: {limits.RateLimitRpm} RPM, {limits.RateLimitRpd} RPD");

    if (limits.MaxMemories.HasValue)
        Console.WriteLine($"  Memories: {limits.CurrentMemories}/{limits.MaxMemories}");

    Console.WriteLine("\n=== Example complete! ===");
}
catch (RateLimitException ex)
{
    Console.WriteLine($"Rate limited! Retry after {ex.RetryAfter.TotalSeconds}s");
}
catch (UsageLimitException)
{
    Console.WriteLine("Usage limit exceeded. Upgrade your plan or wait for credit reset.");
}
catch (AuthenticationException)
{
    Console.WriteLine("Authentication failed. Check your API key.");
}
catch (SerialMemoryException ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
