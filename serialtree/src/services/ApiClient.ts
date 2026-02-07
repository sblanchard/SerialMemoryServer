import * as vscode from 'vscode';
import type { SearchResult, IngestResult } from '../types/memory';
import type { GraphData, Stats } from '../types/graph';
import { EXTENSION_ID, CONFIG, DEFAULTS } from '../constants';

export class ApiClient {
  private getBaseUrl(): string {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    return config.get<string>(CONFIG.API_URL) ?? DEFAULTS.API_URL;
  }

  private async request<T>(path: string, options?: RequestInit): Promise<T> {
    const url = `${this.getBaseUrl()}/api${path}`;
    const response = await fetch(url, options);

    if (!response.ok) {
      const body = await response.text().catch(() => '');
      throw new Error(`API ${response.status}: ${body || response.statusText}`);
    }

    return response.json() as Promise<T>;
  }

  async fetchGraphData(limit = 20, query?: string, hops?: number): Promise<GraphData> {
    const params = new URLSearchParams({ limit: limit.toString() });
    if (query) {
      params.set('query', query);
      if (hops) {
        params.set('hops', hops.toString());
      }
    }
    return this.request<GraphData>(`/graph?${params}`);
  }

  async fetchStats(): Promise<Stats> {
    return this.request<Stats>('/stats');
  }

  async searchMemories(
    query: string,
    mode: 'semantic' | 'text' | 'hybrid' = 'hybrid',
    limit = 20,
    threshold = 0.5,
  ): Promise<SearchResult[]> {
    const params = new URLSearchParams({
      query,
      mode,
      limit: limit.toString(),
      threshold: threshold.toString(),
    });
    return this.request<SearchResult[]>(`/memories/search?${params}`);
  }

  async fetchRecentMemories(limit = 20): Promise<SearchResult[]> {
    return this.request<SearchResult[]>(`/memories/recent?limit=${limit}`);
  }

  async ingestMemory(content: string, source?: string): Promise<IngestResult> {
    return this.request<IngestResult>('/memories', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content, source, extractEntities: true }),
    });
  }
}
