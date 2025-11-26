/**
 * SerialMemory Node.js/TypeScript Client SDK
 *
 * Official client for the SerialMemory temporal knowledge graph memory system.
 * Provides high-level methods for memory operations with built-in retry,
 * rate limit handling, and circuit breaker.
 *
 * @example
 * ```typescript
 * import { SerialMemoryClient } from '@serialmemory/client';
 *
 * const client = new SerialMemoryClient({
 *   baseUrl: 'http://localhost:5000',
 *   apiKey: 'your-api-key'
 * });
 *
 * // Ingest a memory
 * const result = await client.ingest('John works at Acme Corp');
 *
 * // Search for memories
 * const memories = await client.search('Who works at Acme?');
 * ```
 */

export * from './client.js';
export * from './types.js';
export * from './errors.js';
