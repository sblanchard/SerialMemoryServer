/**
 * SerialMemory SDK for Node.js/TypeScript
 * Lightweight, zero-dependency client for SerialMemory API
 */

export interface SerialMemoryClientOptions {
  /** Base URL of the SerialMemory API (e.g., "http://localhost:5000") */
  baseUrl: string;
  /** API key (sm_*) or JWT token for authentication */
  apiKey: string;
  /** HTTP request timeout in ms (default: 30000) */
  timeout?: number;
  /** Max retry attempts for transient failures (default: 3) */
  maxRetries?: number;
}

export type SearchMode = 'semantic' | 'text' | 'hybrid';

export interface SearchOptions {
  mode?: SearchMode;
  limit?: number;
  threshold?: number;
  includeEntities?: boolean;
}

export interface IngestOptions {
  source?: string;
  metadata?: Record<string, unknown>;
  extractEntities?: boolean;
}

// Result types
export interface SearchResult {
  memories: MemoryMatch[];
  totalCount: number;
  query?: string;
}

export interface MemoryMatch {
  id: string;
  content: string;
  score: number;
  confidence: number;
  source?: string;
  layer?: string;
  createdAt: string;
  entities?: EntityInfo[];
  metadata?: Record<string, unknown>;
}

export interface EntityInfo {
  name: string;
  type: string;
}

export interface IngestResult {
  memoryId: string;
  message?: string;
  entitiesExtracted: number;
  relationshipsExtracted: number;
  entities?: string[];
}

export interface UpdateResult {
  memoryId: string;
  message?: string;
  success: boolean;
}

export interface DeleteResult {
  memoryId: string;
  message?: string;
  success: boolean;
}

export interface UserPersona {
  userId: string;
  preferences?: PersonaAttribute[];
  skills?: PersonaAttribute[];
  goals?: PersonaAttribute[];
  background?: PersonaAttribute[];
}

export interface PersonaAttribute {
  key: string;
  value: string;
  confidence: number;
  updatedAt?: string;
}

export interface SetPersonaResult {
  message?: string;
  success: boolean;
}

export interface TenantLimits {
  plan: string;
  displayName: string;
  monthlyCredits: number;
  creditsUsed: number;
  creditsRemaining: number;
  daysUntilReset: number;
  cycleResetAt: string;
  rateLimitRpm: number;
  rateLimitRpd: number;
  maxMemories?: number;
  maxEntities?: number;
  maxMemorySizeBytes?: number;
  currentMemories: number;
  currentEntities: number;
  warnings: LimitWarning[];
}

export interface LimitWarning {
  type: string;
  message: string;
  percentUsed: number;
  severity: 'info' | 'warning' | 'critical';
}

export interface UsageWarning {
  type: string;
  message: string;
  percentUsed: number;
  severity: 'info' | 'warning' | 'critical';
}

// Exceptions
export class SerialMemoryError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SerialMemoryError';
  }
}

export class RateLimitError extends SerialMemoryError {
  public readonly retryAfter: number;

  constructor(retryAfterMs: number, details?: string) {
    super(`Rate limit exceeded. Retry after ${retryAfterMs / 1000}s. ${details || ''}`);
    this.name = 'RateLimitError';
    this.retryAfter = retryAfterMs;
  }
}

export class UsageLimitError extends SerialMemoryError {
  constructor(details?: string) {
    super(`Usage limit exceeded. Upgrade your plan or wait for credit reset. ${details || ''}`);
    this.name = 'UsageLimitError';
  }
}

export class AuthenticationError extends SerialMemoryError {
  constructor(details?: string) {
    super(`Authentication failed. Check your API key or token. ${details || ''}`);
    this.name = 'AuthenticationError';
  }
}

/**
 * SerialMemory client for Node.js/TypeScript applications.
 * Zero dependencies - uses native fetch API.
 */
export class SerialMemoryClient {
  private readonly baseUrl: string;
  private readonly headers: Record<string, string>;
  private readonly timeout: number;
  private readonly maxRetries: number;

  /** Callback for usage warnings (75%, 90% thresholds) */
  public onUsageWarning?: (warning: UsageWarning) => void;

  constructor(options: SerialMemoryClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/$/, '');
    this.timeout = options.timeout ?? 30000;
    this.maxRetries = options.maxRetries ?? 3;

    this.headers = {
      'Content-Type': 'application/json',
    };

    // Set auth header based on key format
    if (options.apiKey.startsWith('sm_')) {
      this.headers['X-API-Key'] = options.apiKey;
    } else {
      this.headers['Authorization'] = `Bearer ${options.apiKey}`;
    }
  }

  // ===== Core Memory Operations =====

  /**
   * Search memories using semantic, text, or hybrid search.
   */
  async search(query: string, options: SearchOptions = {}): Promise<SearchResult> {
    const request = {
      query,
      mode: options.mode ?? 'hybrid',
      limit: options.limit ?? 10,
      threshold: options.threshold ?? 0.7,
      include_entities: options.includeEntities ?? true,
    };

    return this.post<SearchResult>('/tools/memory_search', request);
  }

  /**
   * Ingest a new memory. Automatically extracts entities and generates embeddings.
   */
  async ingest(content: string, options: IngestOptions = {}): Promise<IngestResult> {
    const request = {
      content,
      source: options.source ?? 'sdk-node',
      metadata: options.metadata,
      extract_entities: options.extractEntities ?? true,
    };

    return this.post<IngestResult>('/tools/memory_ingest', request);
  }

  /**
   * Update an existing memory's content (creates new version).
   */
  async update(memoryId: string, newContent: string, reason?: string): Promise<UpdateResult> {
    const request = {
      memory_id: memoryId,
      new_content: newContent,
      reason,
    };

    return this.post<UpdateResult>('/tools/memory_update', request);
  }

  /**
   * Soft delete (invalidate) a memory.
   */
  async delete(memoryId: string, reason: string): Promise<DeleteResult> {
    const request = {
      memory_id: memoryId,
      reason,
    };

    return this.post<DeleteResult>('/tools/memory_delete', request);
  }

  // ===== User Persona =====

  /**
   * Get user persona information (preferences, skills, goals, background).
   */
  async getUserPersona(userId = 'default_user'): Promise<UserPersona> {
    return this.post<UserPersona>('/tools/memory_about_user', { user_id: userId });
  }

  /**
   * Set or update a user persona attribute.
   */
  async setUserPersona(
    attributeType: 'preference' | 'skill' | 'goal' | 'background',
    attributeKey: string,
    attributeValue: string,
    confidence = 1.0,
    userId = 'default_user'
  ): Promise<SetPersonaResult> {
    const request = {
      attribute_type: attributeType,
      attribute_key: attributeKey,
      attribute_value: attributeValue,
      confidence,
      user_id: userId,
    };

    return this.post<SetPersonaResult>('/tools/set_user_persona', request);
  }

  // ===== Limits & Usage =====

  /**
   * Get current tenant limits and usage information.
   */
  async getLimits(): Promise<TenantLimits> {
    const limits = await this.get<TenantLimits>('/tenant/limits');

    // Check for warnings and fire callback
    if (this.onUsageWarning && limits.warnings?.length > 0) {
      for (const warning of limits.warnings) {
        this.onUsageWarning({
          type: warning.type,
          message: warning.message,
          percentUsed: warning.percentUsed,
          severity: warning.severity,
        });
      }
    }

    return limits;
  }

  // ===== HTTP Helpers =====

  private async get<T>(endpoint: string): Promise<T> {
    return this.executeWithRetry(() => this.doGet<T>(endpoint));
  }

  private async post<T>(endpoint: string, body: unknown): Promise<T> {
    return this.executeWithRetry(() => this.doPost<T>(endpoint, body));
  }

  private async doGet<T>(endpoint: string): Promise<T> {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), this.timeout);

    try {
      const response = await fetch(`${this.baseUrl}${endpoint}`, {
        method: 'GET',
        headers: this.headers,
        signal: controller.signal,
      });

      return this.handleResponse<T>(response);
    } finally {
      clearTimeout(timeoutId);
    }
  }

  private async doPost<T>(endpoint: string, body: unknown): Promise<T> {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), this.timeout);

    try {
      const response = await fetch(`${this.baseUrl}${endpoint}`, {
        method: 'POST',
        headers: this.headers,
        body: JSON.stringify(body),
        signal: controller.signal,
      });

      return this.handleResponse<T>(response);
    } finally {
      clearTimeout(timeoutId);
    }
  }

  private async handleResponse<T>(response: Response): Promise<T> {
    if (response.ok) {
      const data = await response.json();
      return this.convertKeys(data) as T;
    }

    const errorText = await response.text().catch(() => '');

    switch (response.status) {
      case 429: {
        const retryAfter = response.headers.get('Retry-After');
        const retryMs = retryAfter ? parseInt(retryAfter, 10) * 1000 : 60000;
        throw new RateLimitError(retryMs, errorText);
      }
      case 402:
        throw new UsageLimitError(errorText);
      case 401:
      case 403:
        throw new AuthenticationError(errorText);
      default:
        throw new SerialMemoryError(`Request failed (${response.status}): ${errorText}`);
    }
  }

  private async executeWithRetry<T>(operation: () => Promise<T>): Promise<T> {
    let lastError: Error | undefined;
    let delay = 500;

    for (let attempt = 0; attempt <= this.maxRetries; attempt++) {
      try {
        return await operation();
      } catch (error) {
        lastError = error as Error;

        if (attempt >= this.maxRetries) break;

        // Retry on rate limit with specified delay
        if (error instanceof RateLimitError) {
          await this.sleep(error.retryAfter);
          continue;
        }

        // Retry on network errors with exponential backoff
        if (error instanceof TypeError || (error as { name?: string }).name === 'AbortError') {
          const jitter = Math.random() * 100;
          await this.sleep(delay + jitter);
          delay *= 2;
          continue;
        }

        // Don't retry auth or usage errors
        throw error;
      }
    }

    throw lastError ?? new SerialMemoryError('Max retries exceeded');
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // Convert snake_case to camelCase for JS conventions
  private convertKeys(obj: unknown): unknown {
    if (obj === null || obj === undefined) return obj;
    if (Array.isArray(obj)) return obj.map((item) => this.convertKeys(item));
    if (typeof obj !== 'object') return obj;

    const converted: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(obj as Record<string, unknown>)) {
      const camelKey = key.replace(/_([a-z])/g, (_, c) => c.toUpperCase());
      converted[camelKey] = this.convertKeys(value);
    }
    return converted;
  }
}

// Default export
export default SerialMemoryClient;
