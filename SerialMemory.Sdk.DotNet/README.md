# SerialMemory .NET SDK

Lightweight, dependency-free .NET SDK for [SerialMemory](https://github.com/serialmemory/serialmemory) - a temporal knowledge graph memory system.

## Installation

```bash
dotnet add package SerialMemory.Sdk
```

Or reference the project directly:
```bash
dotnet add reference path/to/SerialMemory.Sdk.DotNet.csproj
```

## Quick Start

```csharp
using SerialMemory.Sdk;

// Create client
var client = new SerialMemoryClient(new SerialMemoryClientOptions
{
    BaseUrl = "http://localhost:5000",
    ApiKey = "sm_live_your_api_key_here"
});

// Subscribe to usage warnings (75%/90% thresholds)
client.OnUsageWarning += warning =>
{
    Console.WriteLine($"[{warning.Severity}] {warning.Message} ({warning.PercentUsed}% used)");
};

// Ingest a memory
var result = await client.IngestAsync(
    "John works at Acme Corp as a software engineer.",
    source: "my-app"
);
Console.WriteLine($"Memory created: {result.MemoryId}");

// Search memories
var search = await client.SearchAsync("Who works at Acme?");
foreach (var match in search.Memories)
{
    Console.WriteLine($"[{match.Score:P0}] {match.Content}");
}

// Check usage limits
var limits = await client.GetLimitsAsync();
Console.WriteLine($"Credits: {limits.CreditsUsed}/{limits.MonthlyCredits}");
```

## API Reference

### Core Operations

| Method | Description |
|--------|-------------|
| `SearchAsync` | Search memories using semantic/text/hybrid search |
| `IngestAsync` | Store a new memory with automatic entity extraction |
| `UpdateAsync` | Update memory content (creates new version) |
| `DeleteAsync` | Soft delete a memory |

### User Persona

| Method | Description |
|--------|-------------|
| `GetUserPersonaAsync` | Get user preferences, skills, goals, background |
| `SetUserPersonaAsync` | Set or update a persona attribute |

### Limits & Usage

| Method | Description |
|--------|-------------|
| `GetLimitsAsync` | Get current plan limits and usage |

### Events

| Event | Description |
|-------|-------------|
| `OnUsageWarning` | Fired when usage crosses 75% or 90% threshold |

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
}
catch (UsageLimitException)
{
    // Upgrade plan or wait for credit reset
}
catch (AuthenticationException)
{
    // Check API key validity
}
catch (SerialMemoryException ex)
{
    // Other errors
    Console.WriteLine(ex.Message);
}
```

## Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `BaseUrl` | required | SerialMemory API URL |
| `ApiKey` | required | API key (sm_*) or JWT token |
| `Timeout` | 30s | HTTP request timeout |
| `MaxRetries` | 3 | Max retry attempts for transient failures |

## Features

- **Zero dependencies** - Uses only built-in System.Text.Json
- **Automatic retry** - Exponential backoff for transient failures
- **Rate limit handling** - Respects Retry-After headers
- **Usage tracking** - Events for 75%/90% usage thresholds
- **Typed exceptions** - Specific exceptions for each error type

## License

MIT
