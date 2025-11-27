/**
 * Configuration options for SerialMemoryClient
 */
export interface SerialMemoryOptions {
  /** Base URL of the SerialMemory API (e.g., "http://localhost:5000") */
  baseUrl: string;

  /** API key for authentication (preferred for server-to-server) */
  apiKey?: string;

  /** JWT token for authentication (alternative to API key) */
  jwtToken?: string;

  /** Tenant ID for multi-tenant deployments */
  tenantId?: string;

  /** Default source identifier for ingested memories */
  defaultSource?: string;

  /** Request timeout in milliseconds (default: 30000) */
  timeout?: number;

  /** Maximum retry attempts for transient failures (default: 3) */
  maxRetries?: number;

  /** Initial retry delay in milliseconds (default: 500) */
  retryDelay?: number;

  /** Circuit breaker open duration in milliseconds (default: 30000) */
  circuitBreakerDuration?: number;

  /** Circuit breaker failure threshold (default: 5) */
  circuitBreakerThreshold?: number;

  /** Callback invoked on retry attempts */
  onRetry?: (attempt: number, error: Error) => void;

  /** Callback invoked when circuit breaker opens */
  onCircuitOpen?: (duration: number) => void;
}

/**
 * Search mode for memory queries
 */
export type SearchMode = 'semantic' | 'text' | 'hybrid';

/**
 * User persona attribute types
 */
export type PersonaAttributeType = 'preference' | 'skill' | 'goal' | 'background';

// ============================================================================
// Request Types
// ============================================================================

export interface SearchRequest {
  query: string;
  mode?: SearchMode;
  limit?: number;
  threshold?: number;
  includeEntities?: boolean;
}

export interface IngestRequest {
  content: string;
  source?: string;
  metadata?: Record<string, unknown>;
  extractEntities?: boolean;
}

export interface UpdateRequest {
  memoryId: string;
  newContent: string;
  reason?: string;
}

export interface DeleteRequest {
  memoryId: string;
  reason: string;
}

export interface MultiHopSearchRequest {
  query: string;
  hops?: number;
  maxResultsPerHop?: number;
}

export interface SetPersonaRequest {
  attributeType: PersonaAttributeType;
  attributeKey: string;
  attributeValue: string;
  confidence?: number;
  userId?: string;
}

export interface InitializeSessionRequest {
  sessionName?: string;
  clientType?: string;
  metadata?: Record<string, unknown>;
}

// ============================================================================
// Response Types
// ============================================================================

/**
 * Entity information from search results
 */
export interface EntityInfo {
  id: string;
  name: string;
  entityType: string;
}

/**
 * Relationship between two entities
 */
export interface RelationshipInfo {
  from: string;
  to: string;
  relationType: string;
}

/**
 * A memory match from search results
 */
export interface MemoryMatch {
  id: string;
  content: string;
  similarity: number;
  rank: number;
  createdAt: string;
  source?: string;
  entities: EntityInfo[];
  metadata?: Record<string, unknown>;
}

/**
 * Result of a memory search operation
 */
export interface MemorySearchResult {
  memories: MemoryMatch[];
  totalCount: number;
  mode?: string;
}

/**
 * Results from a single hop in multi-hop search
 */
export interface HopResult {
  hopNumber: number;
  memories: MemoryMatch[];
  sourceEntity?: string;
}

/**
 * Result of a multi-hop search operation
 */
export interface MultiHopSearchResult {
  initialMemories: MemoryMatch[];
  hops: HopResult[];
  entities: EntityInfo[];
  relationships: RelationshipInfo[];
}

/**
 * Result of a memory ingest operation
 */
export interface MemoryIngestResult {
  memoryId: string;
  extractedEntities: EntityInfo[];
  extractedRelationships: RelationshipInfo[];
  message?: string;
}

/**
 * Result of a memory update operation
 */
export interface MemoryUpdateResult {
  memoryId: string;
  version: number;
  message?: string;
}

/**
 * Result of a memory delete operation
 */
export interface MemoryDeleteResult {
  memoryId: string;
  success: boolean;
  message?: string;
}

/**
 * A persona attribute with value and confidence
 */
export interface PersonaAttribute {
  value: string;
  confidence: number;
  updatedAt: string;
}

/**
 * Result of getting user persona
 */
export interface UserPersonaResult {
  userId: string;
  preferences: Record<string, PersonaAttribute>;
  skills: Record<string, PersonaAttribute>;
  goals: Record<string, PersonaAttribute>;
  background: Record<string, PersonaAttribute>;
}

/**
 * Result of setting a persona attribute
 */
export interface SetPersonaResult {
  success: boolean;
  message?: string;
}

/**
 * Result of session operations
 */
export interface SessionResult {
  sessionId: string;
  sessionName?: string;
  status?: string;
  message?: string;
}

/**
 * Knowledge graph statistics
 */
export interface GraphStatisticsResult {
  totalMemories: number;
  totalEntities: number;
  totalRelationships: number;
  entitiesByType: Record<string, number>;
  relationshipsByType: Record<string, number>;
}
