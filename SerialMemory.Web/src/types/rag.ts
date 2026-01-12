// RAG (Retrieval-Augmented Generation) Types

export interface RagAnswerRequest {
  query: string;
  maxMemories?: number;
  includeL1?: boolean;
  includeL3?: boolean;
  includeL4?: boolean;
  similarityThreshold?: number;
  temperature?: number;
  includeReasoningTrace?: boolean;
}

export interface RetrievedMemory {
  memoryId: string;
  l2Summary: string;
  l2OneLiner?: string;
  l1Context?: string;
  l3Facts: string[];
  l4Heuristics: string[];
  keywords: string[];
  similarity: number;
  importance: number;
  createdAt: string;
}

export interface RagAnswerResponse {
  answer: string;
  queryId: string;
  memories: RetrievedMemory[];
  reasoningTrace?: string;
  modelName: string;
  latencyMs: number;
}

export interface RagSearchRequest {
  query: string;
  limit?: number;
}

export interface RagSearchResponse {
  query: string;
  memories: RetrievedMemory[];
  count: number;
}

export interface RagQueryHistory {
  id: string;
  query: string;
  answer: string;
  memoryIds: string[];
  reasoningTrace?: string;
  latencyMs: number;
  modelName: string;
  createdAt: string;
}

export interface RagHistoryResponse {
  queries: RagQueryHistory[];
  count: number;
}

export interface RagStats {
  totalQueries: number;
  queriesLast24h: number;
  queriesLast7d: number;
  avgLatencyMs: number;
  avgMemoriesPerQuery: number;
  uniqueUsers: number;
  totalIndexedMemories: number;
  indexedLast24h: number;
}
