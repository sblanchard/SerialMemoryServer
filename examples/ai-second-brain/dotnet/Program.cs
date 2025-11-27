// =============================================================================
// AI Second Brain Example
// =============================================================================
// Demonstrates using SerialMemory as a persistent "second brain" for storing
// and retrieving long-term notes, decisions, and insights.
//
// Run: dotnet run
// =============================================================================

using SerialMemory.Client;

Console.WriteLine("🧠 AI Second Brain Example");
Console.WriteLine("==========================\n");

// Initialize client - uses local dev environment by default
var client = new SerialMemoryClient(new SerialMemoryOptions
{
    BaseUrl = Environment.GetEnvironmentVariable("SERIALMEMORY_URL") ?? "http://localhost:5000",
    ApiKey = Environment.GetEnvironmentVariable("SERIALMEMORY_API_KEY") ?? "demo-key",
    DefaultSource = "second-brain"
});

// =============================================================================
// PART 1: Store Different Types of Memories
// =============================================================================

Console.WriteLine("📝 Part 1: Storing memories of different types...\n");

// Store an architecture decision (ADR)
Console.WriteLine("Storing architecture decision...");
var adr = await client.IngestAsync(
    content: """
    Architecture Decision: Use PostgreSQL with pgvector

    Context:
    We need a database that supports both traditional queries and semantic
    vector search for our AI-powered features.

    Decision:
    Adopt PostgreSQL 16 with the pgvector extension for all persistent storage.

    Rationale:
    - Single database for both relational and vector data
    - Mature ecosystem and excellent tooling
    - Lower operational complexity than separate vector DB
    - Cost-effective compared to managed vector services

    Alternatives Considered:
    - Pinecone: Rejected due to cost at scale ($70/mo minimum)
    - Qdrant: Rejected due to operational complexity
    - MongoDB Atlas: Rejected due to weaker SQL support

    Consequences:
    - Need to manage pgvector extension updates
    - Vector index maintenance required for large datasets
    - Team needs training on vector similarity concepts

    Date: 2024-01-15
    """,
    metadata: new Dictionary<string, object>
    {
        ["type"] = "architecture-decision",
        ["status"] = "accepted",
        ["supersedes"] = "none"
    }
);
Console.WriteLine($"  ✅ Stored ADR: {adr.MemoryId}");
Console.WriteLine($"     Entities: {string.Join(", ", adr.ExtractedEntities.Select(e => e.Name))}");

// Store a bug fix analysis
Console.WriteLine("\nStoring bug fix analysis...");
var bugfix = await client.IngestAsync(
    content: """
    Bug Fix: Memory leak in WebSocket connection handler

    Issue: Server memory grew unbounded when clients disconnected unexpectedly

    Root Cause:
    The connection cleanup handler wasn't being invoked for connections that
    timed out (as opposed to clean disconnects). Event handlers remained
    subscribed, preventing garbage collection.

    Solution:
    1. Added timeout detection with a heartbeat mechanism
    2. Implemented IDisposable pattern for connection state
    3. Added defensive cleanup in finally blocks
    4. Set up memory pressure alerts in Grafana

    Affected Files:
    - src/WebSocket/ConnectionHandler.cs
    - src/WebSocket/HeartbeatService.cs
    - tests/WebSocket/ConnectionTests.cs

    Testing: Added load test simulating 10k connections with random drops.
    Memory now stable at ~500MB under load.

    Date: 2024-01-20
    """,
    metadata: new Dictionary<string, object>
    {
        ["type"] = "bug-fix",
        ["severity"] = "critical",
        ["component"] = "websocket"
    }
);
Console.WriteLine($"  ✅ Stored bug fix: {bugfix.MemoryId}");

// Store a learning/insight
Console.WriteLine("\nStoring technical learning...");
var learning = await client.IngestAsync(
    content: """
    Learning: pgvector index selection matters significantly

    Discovery:
    While investigating slow semantic search queries, I found that index type
    selection dramatically affects performance:

    - IVFFlat: Fast build, good for < 1M vectors, requires tuning lists param
    - HNSW: Slower build, better recall, recommended for production

    Key insight: Always benchmark with realistic data. Our test dataset was
    too small to reveal the IVFFlat limitations.

    Action: Switched to HNSW index, query time dropped from 200ms to 15ms
    for our 500k vector dataset.

    Reference: https://github.com/pgvector/pgvector#indexing
    """,
    metadata: new Dictionary<string, object>
    {
        ["type"] = "learning",
        ["topic"] = "database-optimization"
    }
);
Console.WriteLine($"  ✅ Stored learning: {learning.MemoryId}");

// Store a meeting note
Console.WriteLine("\nStoring meeting note...");
var meeting = await client.IngestAsync(
    content: """
    Meeting: Product sync with Sarah Chen (Acme Corp)
    Date: 2024-01-22
    Attendees: Me, Sarah Chen (Product Lead at Acme)

    Discussion:
    - Acme wants to integrate SerialMemory into their enterprise product
    - Timeline: POC by end of February, production Q2
    - Key requirement: SOC 2 compliance documentation
    - Budget: ~$50k/year for enterprise tier

    Action Items:
    1. [Me] Send SOC 2 compliance overview by Friday
    2. [Me] Set up sandbox environment for Acme
    3. [Sarah] Provide list of specific compliance questions
    4. [Both] Schedule technical deep-dive for next week

    Follow-up: February 5th
    """,
    metadata: new Dictionary<string, object>
    {
        ["type"] = "meeting",
        ["company"] = "Acme Corp",
        ["contact"] = "Sarah Chen"
    }
);
Console.WriteLine($"  ✅ Stored meeting: {meeting.MemoryId}");

// =============================================================================
// PART 2: Build User Persona
// =============================================================================

Console.WriteLine("\n👤 Part 2: Building user persona...\n");

await client.SetUserPersonaAsync(
    PersonaAttributeType.Skill,
    "programming_languages",
    "C#, TypeScript, Python - primary languages",
    confidence: 0.95f
);
Console.WriteLine("  ✅ Set skill: programming_languages");

await client.SetUserPersonaAsync(
    PersonaAttributeType.Skill,
    "infrastructure",
    "PostgreSQL, Redis, Docker, Kubernetes",
    confidence: 0.9f
);
Console.WriteLine("  ✅ Set skill: infrastructure");

await client.SetUserPersonaAsync(
    PersonaAttributeType.Preference,
    "code_style",
    "Prefer explicit code over clever abstractions, good error messages",
    confidence: 0.85f
);
Console.WriteLine("  ✅ Set preference: code_style");

await client.SetUserPersonaAsync(
    PersonaAttributeType.Goal,
    "current_focus",
    "Launch SerialMemory to public, acquire first 10 paying customers",
    confidence: 1.0f
);
Console.WriteLine("  ✅ Set goal: current_focus");

// Retrieve and display persona
var persona = await client.GetUserPersonaAsync();
Console.WriteLine("\n  📋 Current Persona:");
foreach (var (key, attr) in persona.Skills)
    Console.WriteLine($"     Skill: {key} = {attr.Value}");
foreach (var (key, attr) in persona.Preferences)
    Console.WriteLine($"     Pref: {key} = {attr.Value}");
foreach (var (key, attr) in persona.Goals)
    Console.WriteLine($"     Goal: {key} = {attr.Value}");

// =============================================================================
// PART 3: Search for Context
// =============================================================================

Console.WriteLine("\n🔍 Part 3: Searching for context...\n");

// Simulate: "What did we decide about databases?"
Console.WriteLine("Query: \"database decision architecture\"\n");

var dbResults = await client.SearchAsync(
    query: "database decision architecture",
    mode: SearchMode.Hybrid,
    limit: 3,
    threshold: 0.4f
);

foreach (var memory in dbResults.Memories)
{
    var preview = memory.Content.Split('\n')[0];
    Console.WriteLine($"  📄 [{memory.Similarity:P0}] {preview}");
}

// Simulate: "What's the status with Acme?"
Console.WriteLine("\nQuery: \"Acme Corp status meeting\"\n");

var acmeResults = await client.SearchAsync(
    query: "Acme Corp status meeting",
    limit: 3
);

foreach (var memory in acmeResults.Memories)
{
    var preview = memory.Content.Split('\n')[0];
    Console.WriteLine($"  📄 [{memory.Similarity:P0}] {preview}");
}

// =============================================================================
// PART 4: Multi-Hop Search
// =============================================================================

Console.WriteLine("\n🔗 Part 4: Multi-hop search (finding connections)...\n");

Console.WriteLine("Query: \"Sarah Chen\" (finding related context)\n");

var multiHop = await client.MultiHopSearchAsync(
    query: "Sarah Chen",
    hops: 2,
    maxResultsPerHop: 3
);

Console.WriteLine($"  Direct matches: {multiHop.InitialMemories.Count}");
foreach (var memory in multiHop.InitialMemories)
{
    var preview = memory.Content.Split('\n')[0];
    Console.WriteLine($"    • {preview}");
}

if (multiHop.Hops.Count > 0)
{
    Console.WriteLine($"\n  Related via knowledge graph:");
    foreach (var hop in multiHop.Hops)
    {
        Console.WriteLine($"    Hop {hop.HopNumber} via '{hop.SourceEntity}':");
        foreach (var memory in hop.Memories.Take(2))
        {
            var preview = memory.Content.Split('\n')[0];
            Console.WriteLine($"      • {preview}");
        }
    }
}

if (multiHop.Entities.Count > 0)
{
    Console.WriteLine($"\n  Discovered entities: {string.Join(", ", multiHop.Entities.Select(e => e.Name))}");
}

// =============================================================================
// PART 5: Statistics
// =============================================================================

Console.WriteLine("\n📊 Part 5: Knowledge graph statistics...\n");

var stats = await client.GetGraphStatisticsAsync();
Console.WriteLine($"  Total memories: {stats.TotalMemories}");
Console.WriteLine($"  Total entities: {stats.TotalEntities}");
Console.WriteLine($"  Total relationships: {stats.TotalRelationships}");

if (stats.EntitiesByType.Count > 0)
{
    Console.WriteLine("  Entities by type:");
    foreach (var (type, count) in stats.EntitiesByType.OrderByDescending(x => x.Value).Take(5))
    {
        Console.WriteLine($"    {type}: {count}");
    }
}

Console.WriteLine("\n✨ Demo complete!");
Console.WriteLine("Your 'second brain' now has persistent memory that grows over time.");
Console.WriteLine("Run again to see memories accumulate and improve search relevance.");
