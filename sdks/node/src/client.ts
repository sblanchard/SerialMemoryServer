import {
  SerialMemoryOptions,
  SearchMode,
  PersonaAttributeType,
  MemorySearchResult,
  MemoryIngestResult,
  MemoryUpdateResult,
  MemoryDeleteResult,
  MultiHopSearchResult,
  UserPersonaResult,
  SetPersonaResult,
  SessionResult,
  GraphStatisticsResult,
} from './types.js';

import {
  SerialMemoryError,
  RateLimitExceededError,
  UsageLimitExceededError,
  CircuitBreakerOpenError,
  AuthenticationError,
  TimeoutError,
} from './errors.js';

/**
 * Circuit breaker state
 */
enum CircuitState {
  Closed = 'closed',
  Open = 'open',
  HalfOpen = 'half-open',
}

/**
 * SerialMemory client SDK for Node.js/TypeScript applications.
 * Provides high-level methods for memory operations with built-in retry,
 * rate limit handling, and circuit breaker.
 *
 * @example
 * ```typescript
 * const client = new SerialMemoryClient({
 *   baseUrl: 'http://localhost:5000',
 *   apiKey: 'your-api-key'
 * });
 *
 * // Ingest a memory
 * const result = await client.ingest('John works at Acme Corp');
 * console.log('Created memory:', result.memoryId);
 *
 * // Search for memories
 * const search = await client.search('Who works at Acme?');
 * for (const memory of search.memories) {
 *   console.log(`[${(memory.similarity * 100).toFixed(0)}%] ${memory.content}`);
 * }
 * ```
 */
export class SerialMemoryClient {
  private readonly baseUrl: string;
  private readonly headers: Record<string, string>;
  private readonly timeout: number;
  private readonly maxRetries: number;
  private readonly retryDelay: number;
  private readonly defaultSource: string;
  private readonly circuitBreakerDuration: number;
  private readonly circuitBreakerThreshold: number;
  private readonly onRetry?: (attempt: number, error: Error) => void;
  private readonly onCircuitOpen?: (duration: number) => void;

  // Circuit breaker state
  private circuitState: CircuitState = CircuitState.Closed;
  private circuitFailures: number = 0;
  private circuitOpenedAt: number = 0;

  /**
   * Creates a new SerialMemory client with the specified options.
   */
  constructor(options: SerialMemoryOptions) {
    this.baseUrl = options.baseUrl.replace(/\/$/, '');
    this.timeout = options.timeout ?? 30000;
    this.maxRetries = options.maxRetries ?? 3;
    this.retryDelay = options.retryDelay ?? 500;
    this.defaultSource = options.defaultSource ?? 'serialmemory-sdk-node';
    this.circuitBreakerDuration = options.circuitBreakerDuration ?? 30000;
    this.circuitBreakerThreshold = options.circuitBreakerThreshold ?? 5;
    this.onRetry = options.onRetry;
    this.onCircuitOpen = options.onCircuitOpen;

    // Build headers
    this.headers = {
      'Content-Type': 'application/json',
    };

    if (options.apiKey) {
      this.headers['X-API-Key'] = options.apiKey;
    } else if (options.jwtToken) {
      this.headers['Authorization'] = `Bearer ${options.jwtToken}`;
    }

    if (options.tenantId) {
      this.headers['X-Tenant-Id'] = options.tenantId;
    }
  }

  // ==========================================================================
  // Memory Operations
  // ==========================================================================

  /**
   * Search memories using semantic, text, or hybrid search.
   *
   * @param query - Natural language search query
   * @param options - Search options
   * @returns Search results with memories and similarity scores
   *
   * @example
   * ```typescript
   * const results = await client.search('machine learning algorithms', {
   *   mode: 'hybrid',
   *   limit: 10,
   *   threshold: 0.7
   * });
   * ```
   */
  async search(
    query: string,
    options: {
      mode?: SearchMode;
      limit?: number;
      threshold?: number;
      includeEntities?: boolean;
    } = {}
  ): Promise<MemorySearchResult> {
    return this.post<MemorySearchResult>('/memory/search', {
      query,
      mode: options.mode ?? 'hybrid',
      limit: options.limit ?? 10,
      threshold: options.threshold ?? 0.7,
      include_entities: options.includeEntities ?? true,
    });
  }

  /**
   * Ingest a new memory into the knowledge graph.
   * Automatically extracts entities and generates embeddings.
   *
   * @param content - Memory content to store
   * @param options - Ingest options
   * @returns The created memory with ID and extracted entities
   *
   * @example
   * ```typescript
   * const result = await client.ingest(
   *   'John Smith works at Acme Corp as a senior engineer.',
   *   { source: 'meeting-notes', metadata: { project: 'acme' } }
   * );
   * ```
   */
  async ingest(
    content: string,
    options: {
      source?: string;
      metadata?: Record<string, unknown>;
      extractEntities?: boolean;
    } = {}
  ): Promise<MemoryIngestResult> {
    return this.post<MemoryIngestResult>('/memory/ingest', {
      content,
      source: options.source ?? this.defaultSource,
      metadata: options.metadata,
      extract_entities: options.extractEntities ?? true,
    });
  }

  /**
   * Update an existing memory's content.
   * Creates a new version (event-sourced, immutable).
   *
   * @param memoryId - UUID of the memory to update
   * @param newContent - New content to replace existing
   * @param reason - Reason for update (audit trail)
   * @returns Updated memory details
   */
  async update(
    memoryId: string,
    newContent: string,
    reason?: string
  ): Promise<MemoryUpdateResult> {
    return this.post<MemoryUpdateResult>('/memory/update', {
      memory_id: memoryId,
      new_content: newContent,
      reason,
    });
  }

  /**
   * Soft delete (invalidate) a memory.
   * Memory remains for audit trail but is marked inactive.
   *
   * @param memoryId - UUID of the memory to delete
   * @param reason - Reason for deletion (required for audit)
   * @returns Deletion confirmation
   */
  async delete(memoryId: string, reason: string): Promise<MemoryDeleteResult> {
    return this.post<MemoryDeleteResult>('/memory/delete', {
      memory_id: memoryId,
      reason,
    });
  }

  /**
   * Perform multi-hop search by traversing the knowledge graph.
   * Finds related memories through entity relationships.
   *
   * @param query - Initial search query
   * @param options - Multi-hop options
   * @returns Multi-hop search results with traversal paths
   *
   * @example
   * ```typescript
   * const results = await client.multiHopSearch('John Smith', { hops: 2 });
   *
   * // Initial memories about John Smith
   * for (const memory of results.initialMemories) {
   *   console.log(memory.content);
   * }
   *
   * // Related memories through entity connections
   * for (const hop of results.hops) {
   *   console.log(`Hop ${hop.hopNumber} via ${hop.sourceEntity}:`);
   *   for (const memory of hop.memories) {
   *     console.log(`  - ${memory.content}`);
   *   }
   * }
   * ```
   */
  async multiHopSearch(
    query: string,
    options: {
      hops?: number;
      maxResultsPerHop?: number;
    } = {}
  ): Promise<MultiHopSearchResult> {
    return this.post<MultiHopSearchResult>('/memory/multi-hop-search', {
      query,
      hops: options.hops ?? 2,
      max_results_per_hop: options.maxResultsPerHop ?? 5,
    });
  }

  // ==========================================================================
  // User Persona Operations
  // ==========================================================================

  /**
   * Get structured information about the user's persona.
   * Includes preferences, skills, goals, and background.
   *
   * @param userId - User identifier (default: "default_user")
   * @returns User persona with all attributes
   */
  async getUserPersona(userId: string = 'default_user'): Promise<UserPersonaResult> {
    return this.post<UserPersonaResult>('/memory/about-user', { user_id: userId });
  }

  /**
   * Set or update a user persona attribute.
   *
   * @param attributeType - Type: "preference", "skill", "goal", "background"
   * @param attributeKey - Attribute name (e.g., "programming_language")
   * @param attributeValue - Attribute value
   * @param options - Additional options
   * @returns Updated attribute confirmation
   *
   * @example
   * ```typescript
   * await client.setUserPersona('preference', 'preferred_language', 'TypeScript', {
   *   confidence: 0.9,
   *   userId: 'user123'
   * });
   * ```
   */
  async setUserPersona(
    attributeType: PersonaAttributeType,
    attributeKey: string,
    attributeValue: string,
    options: {
      confidence?: number;
      userId?: string;
    } = {}
  ): Promise<SetPersonaResult> {
    return this.post<SetPersonaResult>('/memory/set-persona', {
      attribute_type: attributeType,
      attribute_key: attributeKey,
      attribute_value: attributeValue,
      confidence: options.confidence ?? 1.0,
      user_id: options.userId ?? 'default_user',
    });
  }

  // ==========================================================================
  // Session Management
  // ==========================================================================

  /**
   * Initialize a new conversation session for context tracking.
   *
   * @param options - Session options
   * @returns Session details with ID
   */
  async initializeSession(
    options: {
      sessionName?: string;
      clientType?: string;
      metadata?: Record<string, unknown>;
    } = {}
  ): Promise<SessionResult> {
    return this.post<SessionResult>('/session/initialize', {
      session_name: options.sessionName,
      client_type: options.clientType ?? this.defaultSource,
      metadata: options.metadata,
    });
  }

  /**
   * End the current conversation session.
   *
   * @returns Session end confirmation
   */
  async endSession(): Promise<SessionResult> {
    return this.post<SessionResult>('/session/end', {});
  }

  // ==========================================================================
  // Graph Statistics
  // ==========================================================================

  /**
   * Get statistics about the knowledge graph.
   * Includes entity and relationship counts by type.
   *
   * @returns Graph statistics
   */
  async getGraphStatistics(): Promise<GraphStatisticsResult> {
    return this.post<GraphStatisticsResult>('/graph/statistics', {});
  }

  // ==========================================================================
  // Private Helpers
  // ==========================================================================

  private async post<T>(endpoint: string, body: Record<string, unknown>): Promise<T> {
    // Check circuit breaker
    this.checkCircuitBreaker();

    let lastError: Error | null = null;

    for (let attempt = 0; attempt <= this.maxRetries; attempt++) {
      try {
        const response = await this.fetchWithTimeout(endpoint, body);
        this.onSuccess();
        return response as T;
      } catch (error) {
        lastError = error as Error;

        // Don't retry for non-transient errors
        if (
          error instanceof AuthenticationError ||
          error instanceof UsageLimitExceededError
        ) {
          throw error;
        }

        // Handle rate limiting with backoff
        if (error instanceof RateLimitExceededError) {
          if (attempt < this.maxRetries) {
            this.onRetry?.(attempt + 1, error);
            await this.sleep(error.retryAfter);
            continue;
          }
          throw error;
        }

        // Record failure for circuit breaker
        this.onFailure();

        // Retry with exponential backoff for transient errors
        if (attempt < this.maxRetries) {
          const delay = this.retryDelay * Math.pow(2, attempt) * (0.5 + Math.random());
          this.onRetry?.(attempt + 1, error as Error);
          await this.sleep(delay);
        }
      }
    }

    throw lastError ?? new SerialMemoryError('Request failed after retries');
  }

  private async fetchWithTimeout(
    endpoint: string,
    body: Record<string, unknown>
  ): Promise<unknown> {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), this.timeout);

    try {
      const response = await fetch(`${this.baseUrl}${endpoint}`, {
        method: 'POST',
        headers: this.headers,
        body: JSON.stringify(this.toSnakeCase(body)),
        signal: controller.signal,
      });

      const responseText = await response.text();

      if (!response.ok) {
        this.handleErrorResponse(response.status, responseText);
      }

      return responseText ? this.toCamelCase(JSON.parse(responseText)) : {};
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        throw new TimeoutError(this.timeout);
      }
      if (error instanceof SerialMemoryError) {
        throw error;
      }
      throw new SerialMemoryError((error as Error).message);
    } finally {
      clearTimeout(timeoutId);
    }
  }

  private handleErrorResponse(status: number, responseText: string): never {
    switch (status) {
      case 401:
      case 403:
        throw new AuthenticationError(
          `Authentication failed: ${responseText}`,
          status
        );
      case 402:
        try {
          const data = JSON.parse(responseText);
          throw new UsageLimitExceededError(
            responseText,
            data.plan,
            data.next_reset ? new Date(data.next_reset) : undefined
          );
        } catch {
          throw new UsageLimitExceededError(responseText);
        }
      case 429:
        // Try to parse retry-after from response
        let retryAfter = 60000; // Default 60 seconds
        try {
          const data = JSON.parse(responseText);
          if (data.retry_after) {
            retryAfter = data.retry_after * 1000;
          }
        } catch {
          // Use default
        }
        throw new RateLimitExceededError(retryAfter, responseText);
      default:
        throw new SerialMemoryError(
          `Request failed with status ${status}: ${responseText}`,
          status,
          responseText
        );
    }
  }

  // Circuit breaker helpers
  private checkCircuitBreaker(): void {
    if (this.circuitState === CircuitState.Open) {
      const now = Date.now();
      if (now - this.circuitOpenedAt >= this.circuitBreakerDuration) {
        this.circuitState = CircuitState.HalfOpen;
      } else {
        throw new CircuitBreakerOpenError(
          this.circuitBreakerDuration - (now - this.circuitOpenedAt)
        );
      }
    }
  }

  private onSuccess(): void {
    if (this.circuitState === CircuitState.HalfOpen) {
      this.circuitState = CircuitState.Closed;
      this.circuitFailures = 0;
    }
  }

  private onFailure(): void {
    this.circuitFailures++;
    if (
      this.circuitFailures >= this.circuitBreakerThreshold &&
      this.circuitState !== CircuitState.Open
    ) {
      this.circuitState = CircuitState.Open;
      this.circuitOpenedAt = Date.now();
      this.onCircuitOpen?.(this.circuitBreakerDuration);
    }
  }

  // Utility helpers
  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // Convert object keys to snake_case for API
  private toSnakeCase(obj: Record<string, unknown>): Record<string, unknown> {
    const result: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(obj)) {
      const snakeKey = key.replace(/([A-Z])/g, '_$1').toLowerCase();
      if (value !== undefined && value !== null) {
        if (typeof value === 'object' && !Array.isArray(value)) {
          result[snakeKey] = this.toSnakeCase(value as Record<string, unknown>);
        } else {
          result[snakeKey] = value;
        }
      }
    }
    return result;
  }

  // Convert object keys to camelCase from API
  private toCamelCase(obj: unknown): unknown {
    if (Array.isArray(obj)) {
      return obj.map((item) => this.toCamelCase(item));
    }
    if (obj !== null && typeof obj === 'object') {
      const result: Record<string, unknown> = {};
      for (const [key, value] of Object.entries(obj as Record<string, unknown>)) {
        const camelKey = key.replace(/_([a-z])/g, (_, letter) => letter.toUpperCase());
        result[camelKey] = this.toCamelCase(value);
      }
      return result;
    }
    return obj;
  }
}
