// =============================================================================
// SerialMemory SDK Example: Personal Assistant
// =============================================================================
// This example demonstrates how to use SerialMemory as a persistent memory
// layer for a personal AI assistant. It shows:
//
// 1. Storing personal notes, decisions, and preferences
// 2. Searching for relevant context before responding
// 3. Building up a user persona over time
// 4. Multi-hop search to find related information
//
// Run with: dotnet run
// =============================================================================

using SerialMemory.Client;

// -----------------------------------------------------------------------------
// Configuration
// -----------------------------------------------------------------------------
// In production, load these from environment variables or configuration files

var client = new SerialMemoryClient(new SerialMemoryOptions
{
    // Point to your local SerialMemory instance
    BaseUrl = Environment.GetEnvironmentVariable("SERIALMEMORY_URL") ?? "http://localhost:5000",

    // Use API key authentication (recommended for server apps)
    ApiKey = Environment.GetEnvironmentVariable("SERIALMEMORY_API_KEY") ?? "demo-key",

    // Tag all memories with this source for easy filtering
    DefaultSource = "personal-assistant-demo",

    // Optional: callbacks for monitoring
    OnRetry = (attempt, ex) => Console.WriteLine($"⚠️ Retry attempt {attempt}: {ex?.Message}"),
    OnCircuitOpened = (duration) => Console.WriteLine($"🔴 Circuit breaker opened for {duration.TotalSeconds}s")
});

Console.WriteLine("🧠 SerialMemory Personal Assistant Demo");
Console.WriteLine("========================================\n");

// -----------------------------------------------------------------------------
// Part 1: Store Some Personal Notes and Decisions
// -----------------------------------------------------------------------------
Console.WriteLine("📝 Part 1: Storing personal notes...\n");

// Store a decision with context
var decision1 = await client.IngestAsync(
    content: """
    Decision: Adopt PostgreSQL as our primary database for the new project.
    Rationale: Excellent support for JSON, full-text search, and pgvector for AI embeddings.
    Alternatives considered: MongoDB (rejected due to cost), MySQL (rejected due to limited JSON support).
    Date: January 2024
    """,
    source: "architecture-decisions",
    metadata: new Dictionary<string, object>
    {
        ["type"] = "decision",
        ["project"] = "new-saas-app",
        ["importance"] = "high"
    }
);
Console.WriteLine($"✅ Stored decision: {decision1.MemoryId}");
Console.WriteLine($"   Extracted {decision1.ExtractedEntities.Count} entities: {string.Join(", ", decision1.ExtractedEntities.Select(e => e.Name))}\n");

// Store a learning/insight
var learning1 = await client.IngestAsync(
    content: """
    Learning: When using pgvector, always normalize embeddings to unit length before storage.
    This ensures cosine similarity works correctly and improves query performance.
    Discovered while debugging slow semantic search queries.
    """,
    source: "technical-learnings",
    metadata: new Dictionary<string, object>
    {
        ["type"] = "learning",
        ["topic"] = "database",
        ["difficulty_level"] = "intermediate"
    }
);
Console.WriteLine($"✅ Stored learning: {learning1.MemoryId}");

// Store a personal preference
var preference1 = await client.IngestAsync(
    content: """
    Personal preference: I prefer TypeScript over JavaScript for all new projects.
    Reason: Better tooling, type safety catches bugs early, improved IDE experience.
    I've been using TypeScript exclusively since 2022.
    """,
    source: "personal-preferences"
);
Console.WriteLine($"✅ Stored preference: {preference1.MemoryId}");

// Store a meeting note
var meeting1 = await client.IngestAsync(
    content: """
    Meeting with Sarah Chen from Acme Corp on January 15, 2024.
    Topics discussed:
    - Integration timeline: Q2 2024
    - API requirements: REST with OAuth 2.0
    - Data format: JSON with snake_case naming
    Action items:
    - Send API documentation by end of week
    - Schedule follow-up for February
    """,
    source: "meeting-notes",
    metadata: new Dictionary<string, object>
    {
        ["type"] = "meeting",
        ["attendees"] = new[] { "Sarah Chen" },
        ["company"] = "Acme Corp"
    }
);
Console.WriteLine($"✅ Stored meeting note: {meeting1.MemoryId}\n");

// -----------------------------------------------------------------------------
// Part 2: Set Up User Persona
// -----------------------------------------------------------------------------
Console.WriteLine("👤 Part 2: Building user persona...\n");

// Record skills
await client.SetUserPersonaAsync(
    PersonaAttributeType.Skill,
    "primary_language",
    "C#",
    confidence: 0.95f
);
Console.WriteLine("✅ Set skill: primary_language = C#");

await client.SetUserPersonaAsync(
    PersonaAttributeType.Skill,
    "databases",
    "PostgreSQL, Redis",
    confidence: 0.9f
);
Console.WriteLine("✅ Set skill: databases = PostgreSQL, Redis");

// Record preferences
await client.SetUserPersonaAsync(
    PersonaAttributeType.Preference,
    "code_style",
    "Prefer explicit over implicit, clear naming over comments",
    confidence: 0.85f
);
Console.WriteLine("✅ Set preference: code_style");

// Record a goal
await client.SetUserPersonaAsync(
    PersonaAttributeType.Goal,
    "current_project",
    "Build a production-ready SaaS platform",
    confidence: 1.0f
);
Console.WriteLine("✅ Set goal: current_project\n");

// Retrieve and display persona
var persona = await client.GetUserPersonaAsync();
Console.WriteLine("📋 Current User Persona:");
Console.WriteLine($"   Skills: {string.Join(", ", persona.Skills.Select(kv => $"{kv.Key}={kv.Value.Value}"))}");
Console.WriteLine($"   Preferences: {string.Join(", ", persona.Preferences.Select(kv => kv.Key))}");
Console.WriteLine($"   Goals: {string.Join(", ", persona.Goals.Select(kv => kv.Value.Value))}\n");

// -----------------------------------------------------------------------------
// Part 3: Search for Relevant Context
// -----------------------------------------------------------------------------
Console.WriteLine("🔍 Part 3: Searching for context...\n");

// Simulate: User asks "What database should I use?"
Console.WriteLine("User: What database should I use for the new project?\n");

var searchResults = await client.SearchAsync(
    query: "database decision project recommendations",
    mode: SearchMode.Hybrid,
    limit: 3,
    threshold: 0.5f
);

Console.WriteLine($"Found {searchResults.Memories.Count} relevant memories:\n");
foreach (var memory in searchResults.Memories)
{
    Console.WriteLine($"📄 [{memory.Similarity:P0} match] ({memory.Source})");
    // Show first 150 chars of content
    var preview = memory.Content.Length > 150
        ? memory.Content[..150] + "..."
        : memory.Content;
    Console.WriteLine($"   {preview.Replace("\n", " ")}\n");
}

// -----------------------------------------------------------------------------
// Part 4: Multi-Hop Search for Related Information
// -----------------------------------------------------------------------------
Console.WriteLine("🔗 Part 4: Multi-hop search (finding connections)...\n");

// Simulate: User asks about Acme Corp
Console.WriteLine("User: What do I know about Acme Corp?\n");

var multiHopResults = await client.MultiHopSearchAsync(
    query: "Acme Corp",
    hops: 2,
    maxResultsPerHop: 3
);

Console.WriteLine($"Initial matches: {multiHopResults.InitialMemories.Count}");
foreach (var memory in multiHopResults.InitialMemories)
{
    Console.WriteLine($"   • {memory.Content[..Math.Min(100, memory.Content.Length)]}...");
}

if (multiHopResults.Hops.Count > 0)
{
    Console.WriteLine($"\nRelated through knowledge graph ({multiHopResults.Hops.Count} hops):");
    foreach (var hop in multiHopResults.Hops)
    {
        Console.WriteLine($"   Hop {hop.HopNumber} via '{hop.SourceEntity}':");
        foreach (var memory in hop.Memories)
        {
            Console.WriteLine($"      • {memory.Content[..Math.Min(80, memory.Content.Length)]}...");
        }
    }
}

if (multiHopResults.Entities.Count > 0)
{
    Console.WriteLine($"\nDiscovered entities: {string.Join(", ", multiHopResults.Entities.Select(e => e.Name))}");
}

// -----------------------------------------------------------------------------
// Part 5: Knowledge Graph Statistics
// -----------------------------------------------------------------------------
Console.WriteLine("\n📊 Part 5: Knowledge graph statistics...\n");

var stats = await client.GetGraphStatisticsAsync();
Console.WriteLine($"Total memories: {stats.TotalMemories}");
Console.WriteLine($"Total entities: {stats.TotalEntities}");
Console.WriteLine($"Total relationships: {stats.TotalRelationships}");

if (stats.EntitiesByType.Count > 0)
{
    Console.WriteLine("Entities by type:");
    foreach (var (type, count) in stats.EntitiesByType)
    {
        Console.WriteLine($"   {type}: {count}");
    }
}

Console.WriteLine("\n✨ Demo complete! Your personal assistant now has persistent memory.");
Console.WriteLine("Run this demo again to see how memories accumulate over time.");
