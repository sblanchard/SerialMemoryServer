# Quick Start: Node.js SDK

The SerialMemory Node.js SDK provides a zero-dependency TypeScript client for integrating memory capabilities into your Node.js applications.

## Installation

```bash
# Via npm (when published)
npm install @serialmemory/sdk

# Or link locally
cd SerialMemory.Sdk.Node && npm link
cd your-project && npm link @serialmemory/sdk
```

## Requirements

- Node.js 18.0.0 or later (for native fetch support)

## Basic Usage

```typescript
import { SerialMemoryClient } from '@serialmemory/sdk';

// Create client
const client = new SerialMemoryClient({
  baseUrl: 'http://localhost:5000',
  apiKey: 'sm_live_your_api_key_here',
});

// Store a memory
const result = await client.ingest(
  'John works at Acme Corp as a software engineer.',
  { source: 'my-app' }
);
console.log(`Memory ID: ${result.memoryId}`);

// Search memories
const search = await client.search('Who works at Acme?');
for (const match of search.memories) {
  console.log(`[${(match.score * 100).toFixed(0)}%] ${match.content}`);
}

// Get user persona
const persona = await client.getUserPersona();

// Set user preference
await client.setUserPersona(
  'preference',
  'language',
  'TypeScript'
);

// Check usage limits
const limits = await client.getLimits();
console.log(`Credits: ${limits.creditsUsed}/${limits.monthlyCredits}`);
```

## Usage Warnings

Subscribe to usage threshold warnings:

```typescript
client.onUsageWarning = (warning) => {
  if (warning.severity === 'critical') {
    console.warn(`WARNING: ${warning.message}`);
    // Maybe pause non-essential operations
  }
};

// This will trigger warnings if usage is at 75% or 90%
const limits = await client.getLimits();
```

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
    // Retry...
  } else if (error instanceof UsageLimitError) {
    // Upgrade plan or wait for credit reset
    console.log('Usage limit exceeded');
  } else if (error instanceof AuthenticationError) {
    // Check API key
    console.log('Invalid API key');
  } else if (error instanceof SerialMemoryError) {
    // Other errors
    console.error(error.message);
  }
}
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `baseUrl` | required | SerialMemory API URL |
| `apiKey` | required | API key (sm_*) or JWT token |
| `timeout` | 30000 | HTTP timeout in milliseconds |
| `maxRetries` | 3 | Retry attempts for transient failures |

## Project Context Example

Isolate memories by project using metadata:

```typescript
// Ingest with project context
await client.ingest(
  'Architecture decision: Using PostgreSQL for orders.',
  {
    source: 'my-app',
    metadata: {
      project_id: 'proj-ecommerce',
      project_name: 'E-Commerce API',
      type: 'architecture'
    }
  }
);

// Search within a specific project (when metadata filtering is supported)
const results = await client.search('database decisions', {
  // Note: metadata filtering depends on API support
});
```

## Full Example

See the complete example project:

```bash
cd SerialMemory.Examples.Node.ProjectContextMemory
npm start
```

This demonstrates:
- Project-scoped memory namespaces
- Cross-project vs. project-specific search
- Usage limit checking

## API Reference

### Memory Operations

| Method | Description |
|--------|-------------|
| `search(query, options?)` | Search memories |
| `ingest(content, options?)` | Store new memory |
| `update(id, content, reason?)` | Update memory |
| `delete(id, reason)` | Soft-delete memory |

### User Persona

| Method | Description |
|--------|-------------|
| `getUserPersona(userId?)` | Get persona attributes |
| `setUserPersona(type, key, value, confidence?, userId?)` | Set attribute |

### Limits

| Method | Description |
|--------|-------------|
| `getLimits()` | Get plan limits and usage |

## TypeScript Support

The SDK is written in TypeScript and includes full type definitions:

```typescript
import type {
  SearchResult,
  MemoryMatch,
  IngestResult,
  UserPersona,
  TenantLimits
} from '@serialmemory/sdk';
```

## Next Steps

- [Plans and Limits](./05-plans-and-limits.md)
- [Data Lifecycle](./06-data-lifecycle.md)
- [Self-Hosting Guide](./07-self-hosting.md)
