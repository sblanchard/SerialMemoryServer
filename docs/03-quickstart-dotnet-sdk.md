# Quick Start: .NET SDK

The SerialMemory .NET SDK provides a lightweight, dependency-free client for integrating memory capabilities into your .NET applications.

## Installation

```bash
# Via NuGet (when published)
dotnet add package SerialMemory.Sdk

# Or reference the project directly
dotnet add reference path/to/SerialMemory.Sdk.DotNet.csproj
```

## Basic Usage

```csharp
using SerialMemory.Sdk;

// Create client
var client = new SerialMemoryClient(new SerialMemoryClientOptions
{
    BaseUrl = "http://localhost:5000",
    ApiKey = "sm_live_your_api_key_here"
});

// Store a memory
var result = await client.IngestAsync(
    content: "John works at Acme Corp as a software engineer.",
    source: "my-app"
);
Console.WriteLine($"Memory ID: {result.MemoryId}");

// Search memories
var search = await client.SearchAsync("Who works at Acme?");
foreach (var match in search.Memories)
{
    Console.WriteLine($"[{match.Score:P0}] {match.Content}");
}

// Get user persona
var persona = await client.GetUserPersonaAsync();

// Set user preference
await client.SetUserPersonaAsync(
    attributeType: "preference",
    attributeKey: "language",
    attributeValue: "C#"
);

// Check usage limits
var limits = await client.GetLimitsAsync();
Console.WriteLine($"Credits: {limits.CreditsUsed}/{limits.MonthlyCredits}");
```

## Usage Warnings

Subscribe to usage threshold warnings:

```csharp
client.OnUsageWarning += warning =>
{
    if (warning.Severity == "critical")
    {
        // Take action - maybe pause non-essential operations
        Console.WriteLine($"WARNING: {warning.Message}");
    }
};

// This will trigger warnings if usage is at 75% or 90%
var limits = await client.GetLimitsAsync();
```

## Error Handling

```csharp
try
{
    var result = await client.SearchAsync("query");
}
catch (RateLimitException ex)
{
    // Wait and retry
    await Task.Delay(ex.RetryAfter);
    // Retry...
}
catch (UsageLimitException)
{
    // Upgrade plan or wait for credit reset
    Console.WriteLine("Usage limit exceeded");
}
catch (AuthenticationException)
{
    // Check API key
    Console.WriteLine("Invalid API key");
}
catch (SerialMemoryException ex)
{
    // Other errors
    Console.WriteLine($"Error: {ex.Message}");
}
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `BaseUrl` | required | SerialMemory API URL |
| `ApiKey` | required | API key (sm_*) or JWT token |
| `Timeout` | 30s | HTTP request timeout |
| `MaxRetries` | 3 | Retry attempts for transient failures |

## Full Example

See the complete example project:

```bash
cd SerialMemory.Examples.DotNet.SecondBrain
dotnet run
```

This demonstrates:
- Ingesting multiple notes
- Semantic search
- User persona management
- Usage limit checking

## API Reference

### Memory Operations

| Method | Description |
|--------|-------------|
| `SearchAsync(query, options)` | Search memories |
| `IngestAsync(content, options)` | Store new memory |
| `UpdateAsync(id, content, reason)` | Update memory |
| `DeleteAsync(id, reason)` | Soft-delete memory |

### User Persona

| Method | Description |
|--------|-------------|
| `GetUserPersonaAsync(userId)` | Get persona attributes |
| `SetUserPersonaAsync(...)` | Set/update attribute |

### Limits

| Method | Description |
|--------|-------------|
| `GetLimitsAsync()` | Get plan limits and usage |

## Next Steps

- [Node.js SDK Quick Start](./04-quickstart-node-sdk.md)
- [Plans and Limits](./05-plans-and-limits.md)
- [Self-Hosting Guide](./07-self-hosting.md)
