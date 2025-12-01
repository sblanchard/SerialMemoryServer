import type { GraphData, Stats, SearchMemory } from '../types/graph';
import type {
  RagAnswerRequest,
  RagAnswerResponse,
  RagSearchRequest,
  RagSearchResponse,
  RagHistoryResponse,
  RagStats,
} from '../types/rag';

const API_BASE = '/api';

// Fetch graph data
export async function fetchGraphData(limit: number = 20, query?: string, hops?: number): Promise<GraphData> {
  const params = new URLSearchParams();
  params.set('limit', limit.toString());
  if (query) {
    params.set('query', query);
    if (hops) params.set('hops', hops.toString());
  }

  const response = await fetch(`${API_BASE}/graph?${params}`);
  if (!response.ok) throw new Error('Failed to fetch graph data');
  return response.json();
}

// Fetch statistics
export async function fetchStats(): Promise<Stats> {
  const response = await fetch(`${API_BASE}/stats`);
  if (!response.ok) throw new Error('Failed to fetch stats');
  return response.json();
}

// Search memories
export async function searchMemories(
  query: string,
  mode: 'semantic' | 'text' | 'hybrid' = 'hybrid',
  limit: number = 20,
  threshold: number = 0.5
): Promise<SearchMemory[]> {
  const params = new URLSearchParams({
    query,
    mode,
    limit: limit.toString(),
    threshold: threshold.toString(),
  });

  const response = await fetch(`${API_BASE}/memories/search?${params}`);
  if (!response.ok) throw new Error('Failed to search memories');
  return response.json();
}

// Fetch recent memories
export async function fetchRecentMemories(limit: number = 20): Promise<SearchMemory[]> {
  const response = await fetch(`${API_BASE}/memories/recent?limit=${limit}`);
  if (!response.ok) throw new Error('Failed to fetch recent memories');
  return response.json();
}

// Ingest a new memory
export async function ingestMemory(content: string, source?: string): Promise<{ memoryId: string }> {
  const response = await fetch(`${API_BASE}/memories`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ content, source, extractEntities: true }),
  });
  if (!response.ok) throw new Error('Failed to ingest memory');
  return response.json();
}

// =============================================================================
// RAG (Ask My Memory) API Functions
// =============================================================================

const RAG_BASE = '/rag';

// Ask a question using RAG over user memories
export async function askMyMemory(request: RagAnswerRequest): Promise<RagAnswerResponse> {
  const response = await fetch(`${RAG_BASE}/answer`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({ message: 'Failed to get answer' }));
    throw new Error(error.message || 'Failed to get answer');
  }
  return response.json();
}

// Search memories using RAG without generating an answer
export async function searchRagMemories(request: RagSearchRequest): Promise<RagSearchResponse> {
  const response = await fetch(`${RAG_BASE}/search`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
  if (!response.ok) throw new Error('Failed to search memories');
  return response.json();
}

// Get RAG query history
export async function getRagHistory(limit: number = 20): Promise<RagHistoryResponse> {
  const response = await fetch(`${RAG_BASE}/history?limit=${limit}`);
  if (!response.ok) throw new Error('Failed to fetch RAG history');
  return response.json();
}

// Get RAG usage statistics
export async function getRagStats(): Promise<RagStats> {
  const response = await fetch(`${RAG_BASE}/stats`);
  if (!response.ok) throw new Error('Failed to fetch RAG stats');
  return response.json();
}
