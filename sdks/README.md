# SerialMemory Client SDKs

Official client libraries for SerialMemory with built-in resilience patterns.

## Available SDKs

| SDK | Package | Status |
|-----|---------|--------|
| [.NET](./dotnet/) | `SerialMemory.Client` | ✅ Production Ready |
| [Node.js/TypeScript](./node/) | `@serialmemory/client` | ✅ Production Ready |

## Features

All SDKs provide:

- **Authentication** - API key and JWT token support
- **Automatic Retry** - Exponential backoff with jitter
- **Rate Limit Handling** - Respects retry-after headers
- **Circuit Breaker** - Protects against cascading failures
- **Usage Limit Errors** - Typed exceptions for plan limits
- **Multi-tenant** - Tenant ID header support

## Quick Comparison

| Feature | .NET | Node.js |
|---------|------|---------|
| Runtime | .NET 8+ | Node.js 18+ |
| Dependencies | Polly | Zero deps (native fetch) |
| Type Safety | C# records | Full TypeScript |
| Async | async/await | async/await |

## Installation

### .NET

```bash
# Via NuGet (when published)
dotnet add package SerialMemory.Client

# Or reference local project
dotnet add reference path/to/SerialMemory.Client.csproj
```

### Node.js

```bash
# Via npm (when published)
npm install @serialmemory/client

# Or link local package
cd sdks/node && npm link
cd your-project && npm link @serialmemory/client
```

## Basic Usage

### .NET

```csharp
using SerialMemory.Client;

var client = new SerialMemoryClient(new SerialMemoryOptions
{
    BaseUrl = "http://localhost:5000",
    ApiKey = "your-api-key"
});

// Search
var results = await client.SearchAsync("query");

// Ingest
var memory = await client.IngestAsync("Content to remember");

// User persona
var persona = await client.GetUserPersonaAsync();
```

### Node.js / TypeScript

```typescript
import { SerialMemoryClient } from '@serialmemory/client';

const client = new SerialMemoryClient({
  baseUrl: 'http://localhost:5000',
  apiKey: 'your-api-key'
});

// Search
const results = await client.search('query');

// Ingest
const memory = await client.ingest('Content to remember');

// User persona
const persona = await client.getUserPersona();
```

## Error Handling

Both SDKs provide typed exceptions:

| Exception | HTTP Code | Description |
|-----------|-----------|-------------|
| `RateLimitExceededError` | 429 | Too many requests, retry after delay |
| `UsageLimitExceededError` | 402 | Plan quota exhausted |
| `AuthenticationError` | 401/403 | Invalid credentials |
| `CircuitBreakerOpenError` | N/A | Service temporarily unavailable |

## Examples

Each SDK includes example projects:

- **Personal Assistant** - AI second brain pattern
- **Project Memory** - Multi-project isolation

See [examples/](../examples/) for standalone example projects.

## Configuration Reference

| Option | .NET | Node.js | Default |
|--------|------|---------|---------|
| Base URL | `BaseUrl` | `baseUrl` | Required |
| API Key | `ApiKey` | `apiKey` | - |
| JWT Token | `JwtToken` | `jwtToken` | - |
| Tenant ID | `TenantId` | `tenantId` | - |
| Timeout | `Timeout` | `timeout` | 30s |
| Max Retries | `MaxRetries` | `maxRetries` | 3 |
| Retry Delay | - | `retryDelay` | 500ms |
| Circuit Breaker Duration | `CircuitBreakerDuration` | `circuitBreakerDuration` | 30s |

## Building from Source

### .NET SDK

```bash
cd sdks/dotnet
dotnet build SerialMemory.Client
dotnet pack SerialMemory.Client -c Release
```

### Node.js SDK

```bash
cd sdks/node
npm install
npm run build
npm pack
```

## Contributing

1. Fork the repository
2. Create a feature branch
3. Add tests for new functionality
4. Submit a pull request

## License

MIT
