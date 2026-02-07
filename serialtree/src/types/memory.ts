import type { Entity } from './graph';

export type MemoryType = 'decision' | 'learning' | 'pattern' | 'bugfix' | 'context' | 'preference';

export type SearchMode = 'semantic' | 'text' | 'hybrid';

export interface SearchResult {
  id: string;
  content: string;
  createdAt: string;
  similarity?: number;
  rank?: number;
  entities: Entity[];
  memoryType?: MemoryType;
  source?: string;
}

export interface IngestResult {
  memoryId: string;
  entitiesExtracted?: number;
}

export interface ToolInfo {
  name: string;
  description: string;
  inputSchema?: Record<string, unknown>;
}

export interface CodeFinding {
  type: 'tech_debt' | 'bug' | 'performance' | 'quick_win';
  severity: 'critical' | 'high' | 'medium' | 'low';
  file: string;
  line: number;
  title: string;
  description: string;
  suggestion?: string;
}

export interface AnalysisReport {
  findings: CodeFinding[];
  timestamp: string;
  filesScanned: number;
  duration: number;
}

export interface SessionEntry {
  timestamp: string;
  type: 'file_edit' | 'command' | 'error' | 'finding' | 'memory' | 'unknown';
  content: string;
  file?: string;
  detail?: string;
}
