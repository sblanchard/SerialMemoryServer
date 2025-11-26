# SerialMemory Node.js/TypeScript SDK

Lightweight, zero-dependency TypeScript SDK for [SerialMemory](https://github.com/serialmemory/serialmemory) - a temporal knowledge graph memory system.

## Installation

```bash
npm install @serialmemory/sdk
```

Or link locally:
```bash
cd SerialMemory.Sdk.Node && npm link
cd your-project && npm link @serialmemory/sdk
```

## Quick Start

```typescript
import { SerialMemoryClient } from '@serialmemory/sdk';

// Create client
const client = new SerialMemoryClient({
  baseUrl: 'http://localhost:5000',
  apiKey: 'sm_live_your_api_key_here',
});

// Subscribe to usage warnings (75%/90% thresholds)
client.onUsageWarning = (warning) => {
  console.log(`[${warning.severity}] ${warning.message} (${warning.percentUsed}% used)`);
};

// Ingest a memory
const result = await client.ingest(
  'John works at Acme Corp as a software engineer.',
  { source: 'my-app' }
);
console.log(`Memory created: ${result.memoryId}`);

// Search memories
const search = await client.search('Who works at Acme?');
for (const match of search.memories) {
  console.log(`[${(match.score * 100).toFixed(0)}%] ${match.content}`);
}

// Check usage limits
const limits = await client.getLimits();
console.log(`Credits: ${limits.creditsUsed}/${limits.monthlyCredits}`);
```

## API Reference

### Core Operations

| Method | Description |
|--------|-------------|
| `search(query, options?)` | Search memories using semantic/text/hybrid search |
| `ingest(content, options?)` | Store a new memory with automatic entity extraction |
| `update(memoryId, newContent, reason?)` | Update memory content (creates new version) |
| `delete(memoryId, reason)` | Soft delete a memory |

### User Persona

| Method | Description |
|--------|-------------|
| `getUserPersona(userId?)` | Get user preferences, skills, goals, background |
| `setUserPersona(type, key, value, confidence?, userId?)` | Set or update a persona attribute |

### Limits & Usage

| Method | Description |
|--------|-------------|
| `getLimits()` | Get current plan limits and usage |

### Events

| Property | Description |
|----------|-------------|
| `onUsageWarning` | Callback fired when usage crosses 75% or 90% threshold |

## Error Handling

```typescript
import {
  SerialMemoryClient,
  RateLimitError,
  UsageLimitError,
  AuthenticationError,
  SerialMemoryError
} from '@serialmemory/sdk';

try {
  const result = await client.search('query');
} catch (error) {
  if (error instanceof RateLimitError) {
    // Wait and retry
    await new Promise(r => setTimeout(r, error.retryAfter));
  } else if (error instanceof UsageLimitError) {
    // Upgrade plan or wait for credit reset
  } else if (error instanceof AuthenticationError) {
    // Check API key validity
  } else if (error instanceof SerialMemoryError) {
    console.error(error.message);
  }
}
```

## Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `baseUrl` | required | SerialMemory API URL |
| `apiKey` | required | API key (sm_*) or JWT token |
| `timeout` | 30000 | HTTP request timeout (ms) |
| `maxRetries` | 3 | Max retry attempts for transient failures |

## Features

- **Zero dependencies** - Uses native fetch API (Node.js 18+)
- **Full TypeScript** - Complete type definitions included
- **Automatic retry** - Exponential backoff for transient failures
- **Rate limit handling** - Respects Retry-After headers
- **Usage tracking** - Callback for 75%/90% usage thresholds
- **camelCase conversion** - Automatic snake_case to camelCase for JS conventions

## Requirements

- Node.js 18.0.0 or later (for native fetch support)

## License

MIT
