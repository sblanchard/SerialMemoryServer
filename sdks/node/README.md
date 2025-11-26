# SerialMemory Node.js/TypeScript Client SDK

Official Node.js/TypeScript client SDK for [SerialMemory](https://github.com/serialmemory/serialmemory-server) - a temporal knowledge graph memory system for AI applications.

## Installation

```bash
npm install @serialmemory/client
```

Or with yarn:
```bash
yarn add @serialmemory/client
```

Or with pnpm:
```bash
pnpm add @serialmemory/client
```

## Quick Start

```typescript
import { SerialMemoryClient } from '@serialmemory/client';

// Create client with API key authentication
const client = new SerialMemoryClient({
  baseUrl: 'http://localhost:5000',
  apiKey: 'your-api-key'
});

// Ingest a memory
const result = await client.ingest(
  'John Smith works at Acme Corp as a senior engineer. He specializes in TypeScript and distributed systems.',
  { source: 'my-app' }
);
console.log(`Created memory: ${result.memoryId}`);
console.log(`Extracted entities: ${result.extractedEntities.map(e => e.name).join(', ')}`);

// Search for related memories
const searchResults = await client.search('Who works at Acme Corp?', {
  mode: 'hybrid',
  limit: 5
});

for (const memory of searchResults.memories) {
  console.log(`[${(memory.similarity * 100).toFixed(0)}%] ${memory.content}`);
}
```

## Features

- **Full TypeScript Support**: Complete type definitions for all APIs
- **Authentication**: API key or JWT token support
- **Resilience**: Automatic retry with exponential backoff and jitter
- **Rate Limiting**: Built-in handling with retry-after support
- **Circuit Breaker**: Protects against cascading failures
- **Multi-tenant**: Tenant ID header support for SaaS deployments
- **Zero Dependencies**: Uses native `fetch` (Node.js 18+)

## Configuration

```typescript
const client = new SerialMemoryClient({
  // Required
  baseUrl: 'http://localhost:5000',

  // Authentication (choose one)
  apiKey: 'sk-...',                // API key (preferred for server-to-server)
  jwtToken: 'eyJ...',              // JWT token (alternative)

  // Multi-tenant (optional)
  tenantId: '...',                 // Tenant ID for SaaS deployments

  // Defaults
  defaultSource: 'my-app',         // Source tag for ingested memories
  timeout: 30000,                  // Request timeout in ms
  maxRetries: 3,                   // Max retry attempts
  retryDelay: 500,                 // Initial retry delay in ms
  circuitBreakerDuration: 30000,   // Circuit breaker open duration in ms
  circuitBreakerThreshold: 5,      // Failures before circuit opens

  // Callbacks (optional)
  onRetry: (attempt, error) => console.log(`Retry ${attempt}: ${error.message}`),
  onCircuitOpen: (duration) => console.log(`Circuit opened for ${duration}ms`)
});
```

## API Reference

### Memory Operations

#### Search Memories

```typescript
const results = await client.search('machine learning algorithms', {
  mode: 'hybrid',      // 'semantic', 'text', or 'hybrid'
  limit: 10,
  threshold: 0.7,      // Minimum similarity (0.0-1.0)
  includeEntities: true
});

for (const memory of results.memories) {
  console.log(`[${(memory.similarity * 100).toFixed(0)}%] ${memory.content}`);
  if (memory.entities.length > 0) {
    console.log(`  Entities: ${memory.entities.map(e => e.name).join(', ')}`);
  }
}
```

#### Ingest Memory

```typescript
const result = await client.ingest(
  'Important decision: We chose PostgreSQL for the database.',
  {
    source: 'meeting-notes',
    metadata: {
      project: 'acme-app',
      meetingDate: '2024-01-15'
    },
    extractEntities: true
  }
);

console.log(`Memory ID: ${result.memoryId}`);
console.log(`Extracted: ${result.extractedEntities.length} entities`);
```

#### Update Memory

```typescript
const result = await client.update(
  'memory-uuid',
  'Updated content here',
  'Fixed typo'  // reason for audit trail
);
```

#### Delete Memory (Soft Delete)

```typescript
const result = await client.delete(
  'memory-uuid',
  'No longer relevant'  // reason required for audit
);
```

#### Multi-Hop Search

Traverse the knowledge graph to find related information through entity relationships:

```typescript
const results = await client.multiHopSearch('John Smith', {
  hops: 2,
  maxResultsPerHop: 5
});

// Initial memories about John Smith
console.log('Direct matches:');
for (const memory of results.initialMemories) {
  console.log(`  - ${memory.content}`);
}

// Related memories through entity connections
for (const hop of results.hops) {
  console.log(`\nHop ${hop.hopNumber} via ${hop.sourceEntity}:`);
  for (const memory of hop.memories) {
    console.log(`  - ${memory.content}`);
  }
}

// Discovered relationships
console.log('\nRelationships:');
for (const rel of results.relationships) {
  console.log(`  ${rel.from} --[${rel.relationType}]--> ${rel.to}`);
}
```

### User Persona

#### Get User Persona

```typescript
const persona = await client.getUserPersona('user123');

console.log('Preferences:');
for (const [key, attr] of Object.entries(persona.preferences)) {
  console.log(`  ${key}: ${attr.value} (confidence: ${(attr.confidence * 100).toFixed(0)}%)`);
}

console.log('Skills:');
for (const [key, attr] of Object.entries(persona.skills)) {
  console.log(`  ${key}: ${attr.value}`);
}
```

#### Set Persona Attribute

```typescript
await client.setUserPersona('preference', 'preferred_language', 'TypeScript', {
  confidence: 0.9,
  userId: 'user123'
});
```

### Session Management

```typescript
// Start a session
const session = await client.initializeSession({
  sessionName: 'Code Review Session',
  clientType: 'my-app',
  metadata: { projectId: 'proj-123' }
});
console.log(`Session started: ${session.sessionId}`);

// ... do work ...

// End the session
await client.endSession();
```

### Graph Statistics

```typescript
const stats = await client.getGraphStatistics();

console.log(`Total memories: ${stats.totalMemories}`);
console.log(`Total entities: ${stats.totalEntities}`);
console.log('Entities by type:');
for (const [type, count] of Object.entries(stats.entitiesByType)) {
  console.log(`  ${type}: ${count}`);
}
```

## Error Handling

The SDK provides typed exceptions for different error scenarios:

```typescript
import {
  SerialMemoryClient,
  RateLimitExceededError,
  UsageLimitExceededError,
  CircuitBreakerOpenError,
  AuthenticationError,
  TimeoutError
} from '@serialmemory/client';

try {
  const result = await client.search('query');
} catch (error) {
  if (error instanceof RateLimitExceededError) {
    // HTTP 429 - Wait before retrying
    console.log(`Rate limited. Retry after: ${error.retryAfter}ms`);
    await new Promise(r => setTimeout(r, error.retryAfter));
  } else if (error instanceof UsageLimitExceededError) {
    // HTTP 402 - Usage quota exceeded
    console.log(`Usage limit exceeded. Plan: ${error.plan}`);
    if (error.nextReset) {
      console.log(`Resets at: ${error.nextReset.toISOString()}`);
    }
  } else if (error instanceof CircuitBreakerOpenError) {
    // Service unavailable - circuit breaker is open
    console.log(`Service unavailable for ${error.breakDuration}ms`);
  } else if (error instanceof AuthenticationError) {
    // HTTP 401/403 - Auth failure
    console.log(`Authentication failed: ${error.message}`);
  } else if (error instanceof TimeoutError) {
    // Request timed out
    console.log('Request timed out');
  } else {
    // Other API errors
    console.log(`Error: ${error.message}`);
  }
}
```

## Express.js Integration

```typescript
import express from 'express';
import { SerialMemoryClient } from '@serialmemory/client';

const app = express();
app.use(express.json());

// Create a shared client instance
const memory = new SerialMemoryClient({
  baseUrl: process.env.SERIALMEMORY_URL!,
  apiKey: process.env.SERIALMEMORY_API_KEY!
});

// Middleware to add context retrieval
app.use('/api/chat', async (req, res, next) => {
  try {
    // Search for relevant context before processing
    const context = await memory.search(req.body.message, { limit: 3 });
    req.memoryContext = context.memories.map(m => m.content).join('\n');
    next();
  } catch (error) {
    // Continue without context if memory service is unavailable
    req.memoryContext = '';
    next();
  }
});

// Store important interactions
app.post('/api/decision', async (req, res) => {
  const { decision, rationale } = req.body;

  await memory.ingest(
    `Decision: ${decision}\nRationale: ${rationale}`,
    { source: 'api', metadata: { type: 'decision' } }
  );

  res.json({ success: true });
});

app.listen(3000);
```

## Requirements

- Node.js 18+ (uses native `fetch`)
- TypeScript 5.0+ (for full type support)

## License

MIT License - see [LICENSE](LICENSE) for details.
