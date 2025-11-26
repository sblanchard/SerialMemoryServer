# SerialMemory .NET Client SDK

Official .NET client SDK for [SerialMemory](https://github.com/serialmemory/serialmemory-server) - a temporal knowledge graph memory system for AI applications.

## Installation

```bash
dotnet add package SerialMemory.Client
```

Or via NuGet Package Manager:
```
Install-Package SerialMemory.Client
```

## Quick Start

```csharp
using SerialMemory.Client;

// Create client with API key authentication
var client = new SerialMemoryClient(new SerialMemoryOptions
{
    BaseUrl = "http://localhost:5000",
    ApiKey = "your-api-key"
});

// Ingest a memory
var result = await client.IngestAsync(
    content: "John Smith works at Acme Corp as a senior engineer. He specializes in C# and distributed systems.",
    source: "my-app"
);
Console.WriteLine($"Created memory: {result.MemoryId}");
Console.WriteLine($"Extracted entities: {string.Join(", ", result.ExtractedEntities.Select(e => e.Name))}");

// Search for related memories
var searchResults = await client.SearchAsync(
    query: "Who works at Acme Corp?",
    mode: SearchMode.Hybrid,
    limit: 5
);

foreach (var memory in searchResults.Memories)
{
    Console.WriteLine($"[{memory.Similarity:P0}] {memory.Content}");
}
```

## Features

- **Authentication**: API key or JWT token support
- **Resilience**: Automatic retry with exponential backoff
- **Rate Limiting**: Built-in handling with retry-after support
- **Circuit Breaker**: Protects against cascading failures
- **Multi-tenant**: Tenant ID header support for SaaS deployments

## Configuration

```csharp
var client = new SerialMemoryClient(new SerialMemoryOptions
{
    // Required
    BaseUrl = "http://localhost:5000",

    // Authentication (choose one)
    ApiKey = "sk-...",                // API key (preferred for server-to-server)
    JwtToken = "eyJ...",              // JWT token (alternative)

    // Multi-tenant (optional)
    TenantId = Guid.Parse("..."),     // Tenant ID for SaaS deployments

    // Defaults
    DefaultSource = "my-app",          // Source tag for ingested memories
    Timeout = TimeSpan.FromSeconds(30),
    MaxRetries = 3,
    CircuitBreakerDuration = TimeSpan.FromSeconds(30),

    // Callbacks (optional)
    OnRetry = (attempt, ex) => Console.WriteLine($"Retry {attempt}: {ex?.Message}"),
    OnCircuitOpened = (duration) => Console.WriteLine($"Circuit opened for {duration}")
});
```

## API Reference

### Memory Operations

#### Search Memories

```csharp
var results = await client.SearchAsync(
    query: "machine learning algorithms",
    mode: SearchMode.Hybrid,    // Semantic, Text, or Hybrid
    limit: 10,
    threshold: 0.7f,            // Minimum similarity (0.0-1.0)
    includeEntities: true
);
```

#### Ingest Memory

```csharp
var result = await client.IngestAsync(
    content: "Important decision: We chose PostgreSQL for the database.",
    source: "meeting-notes",
    metadata: new Dictionary<string, object>
    {
        ["project"] = "acme-app",
        ["meeting_date"] = "2024-01-15"
    },
    extractEntities: true
);
```

#### Update Memory

```csharp
var result = await client.UpdateAsync(
    memoryId: Guid.Parse("..."),
    newContent: "Updated content here",
    reason: "Fixed typo"
);
```

#### Delete Memory (Soft Delete)

```csharp
var result = await client.DeleteAsync(
    memoryId: Guid.Parse("..."),
    reason: "No longer relevant"
);
```

#### Multi-Hop Search

Traverse the knowledge graph to find related information through entity relationships:

```csharp
var results = await client.MultiHopSearchAsync(
    query: "John Smith",
    hops: 2,
    maxResultsPerHop: 5
);

// Initial memories about John Smith
foreach (var memory in results.InitialMemories)
    Console.WriteLine(memory.Content);

// Related memories through entity connections
foreach (var hop in results.Hops)
{
    Console.WriteLine($"Hop {hop.HopNumber} via {hop.SourceEntity}:");
    foreach (var memory in hop.Memories)
        Console.WriteLine($"  - {memory.Content}");
}
```

### User Persona

#### Get User Persona

```csharp
var persona = await client.GetUserPersonaAsync(userId: "user123");

Console.WriteLine("Preferences:");
foreach (var (key, attr) in persona.Preferences)
    Console.WriteLine($"  {key}: {attr.Value} (confidence: {attr.Confidence:P0})");

Console.WriteLine("Skills:");
foreach (var (key, attr) in persona.Skills)
    Console.WriteLine($"  {key}: {attr.Value}");
```

#### Set Persona Attribute

```csharp
await client.SetUserPersonaAsync(
    attributeType: PersonaAttributeType.Preference,
    attributeKey: "preferred_language",
    attributeValue: "C#",
    confidence: 0.9f,
    userId: "user123"
);
```

### Session Management

```csharp
// Start a session
var session = await client.InitializeSessionAsync(
    sessionName: "Code Review Session",
    clientType: "my-app"
);

// ... do work ...

// End the session
await client.EndSessionAsync();
```

### Graph Statistics

```csharp
var stats = await client.GetGraphStatisticsAsync();

Console.WriteLine($"Total memories: {stats.TotalMemories}");
Console.WriteLine($"Total entities: {stats.TotalEntities}");
Console.WriteLine("Entities by type:");
foreach (var (type, count) in stats.EntitiesByType)
    Console.WriteLine($"  {type}: {count}");
```

## Error Handling

The SDK provides typed exceptions for different error scenarios:

```csharp
try
{
    var result = await client.SearchAsync("query");
}
catch (RateLimitExceededException ex)
{
    // HTTP 429 - Wait before retrying
    Console.WriteLine($"Rate limited. Retry after: {ex.RetryAfter}");
    await Task.Delay(ex.RetryAfter);
}
catch (UsageLimitExceededException ex)
{
    // HTTP 402 - Usage quota exceeded
    Console.WriteLine("Usage limit exceeded. Please upgrade your plan.");
}
catch (AuthenticationException ex)
{
    // HTTP 401/403 - Auth failure
    Console.WriteLine($"Authentication failed: {ex.Message}");
}
catch (SerialMemoryException ex)
{
    // Other API errors
    Console.WriteLine($"Error ({ex.StatusCode}): {ex.Message}");
}
```

## Dependency Injection

For ASP.NET Core applications:

```csharp
// In Program.cs or Startup.cs
builder.Services.AddSingleton<SerialMemoryClient>(sp =>
    new SerialMemoryClient(new SerialMemoryOptions
    {
        BaseUrl = builder.Configuration["SerialMemory:BaseUrl"]!,
        ApiKey = builder.Configuration["SerialMemory:ApiKey"]
    })
);

// In your service
public class MyService
{
    private readonly SerialMemoryClient _memory;

    public MyService(SerialMemoryClient memory)
    {
        _memory = memory;
    }

    public async Task<string> GetContextAsync(string query)
    {
        var results = await _memory.SearchAsync(query, limit: 3);
        return string.Join("\n", results.Memories.Select(m => m.Content));
    }
}
```

## License

MIT License - see [LICENSE](LICENSE) for details.
