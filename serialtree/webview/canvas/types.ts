import type { SearchResult, CodeFinding, SessionEntry } from '../../src/types/memory';
import type { GraphNode } from '../../src/types/graph';
import type { AgentRole, SpawnedAgent, AgentFinding, ModuleDescriptor } from '../../src/types/agent';

// --- Canvas node types (discriminated union) ---

export type CanvasNodeType = 'memory' | 'entity' | 'agent' | 'finding' | 'module' | 'activity';

interface CanvasNodeBase {
  readonly id: string;
  readonly label: string;
  readonly nodeType: CanvasNodeType;
  readonly timestamp?: string;
}

export interface MemoryCanvasNode extends CanvasNodeBase {
  readonly nodeType: 'memory';
  readonly content: string;
  readonly similarity?: number;
  readonly memoryType?: string;
  readonly entities: ReadonlyArray<{ readonly name: string; readonly type: string }>;
  readonly source?: string;
}

export interface EntityCanvasNode extends CanvasNodeBase {
  readonly nodeType: 'entity';
  readonly entityType: string;
  readonly confidence?: number;
}

export interface AgentCanvasNode extends CanvasNodeBase {
  readonly nodeType: 'agent';
  readonly role: AgentRole;
  readonly status: 'running' | 'completed' | 'failed';
  readonly findingCount: number;
  readonly startedAt: number;
  readonly duration?: number;
}

export interface FindingCanvasNode extends CanvasNodeBase {
  readonly nodeType: 'finding';
  readonly severity: 'critical' | 'high' | 'medium' | 'low';
  readonly findingType: string;
  readonly description: string;
  readonly file?: string;
  readonly line?: number;
  readonly suggestion?: string;
}

export interface ModuleCanvasNode extends CanvasNodeBase {
  readonly nodeType: 'module';
  readonly purpose: string;
  readonly keyFiles: readonly string[];
}

export interface ActivityCanvasNode extends CanvasNodeBase {
  readonly nodeType: 'activity';
  readonly activityType: SessionEntry['type'];
  readonly content: string;
  readonly file?: string;
}

export type CanvasNode =
  | MemoryCanvasNode
  | EntityCanvasNode
  | AgentCanvasNode
  | FindingCanvasNode
  | ModuleCanvasNode
  | ActivityCanvasNode;

// --- Canvas edge types ---

export type CanvasEdgeType =
  | 'relationship'
  | 'entity-link'
  | 'agent-finding'
  | 'finding-file'
  | 'module-dep'
  | 'activity-ref';

export interface CanvasEdge {
  readonly id: string;
  readonly source: string;
  readonly target: string;
  readonly edgeType: CanvasEdgeType;
  readonly label?: string;
}

// --- Message protocols ---

export type CanvasMessageToWebview =
  | { readonly type: 'canvasData'; readonly nodes: readonly CanvasNode[]; readonly edges: readonly CanvasEdge[] }
  | { readonly type: 'nodeAdded'; readonly node: CanvasNode; readonly edges: readonly CanvasEdge[] }
  | { readonly type: 'nodeUpdated'; readonly node: CanvasNode }
  | { readonly type: 'agentStatusChanged'; readonly agentId: string; readonly status: AgentCanvasNode['status'] }
  | { readonly type: 'settings'; readonly layout: string; readonly showMinimap: boolean };

export type CanvasMessageToExtension =
  | { readonly type: 'requestData' }
  | { readonly type: 'search'; readonly query: string }
  | { readonly type: 'nodeAction'; readonly nodeId: string; readonly action: string }
  | { readonly type: 'filterChanged'; readonly visibleTypes: readonly CanvasNodeType[] }
  | { readonly type: 'layoutRequested'; readonly layout: string }
  | { readonly type: 'openFile'; readonly file: string; readonly line?: number }
  | { readonly type: 'sendToClaude'; readonly content: string }
  | { readonly type: 'dismissFinding'; readonly findingId: string };

// --- Layout types ---

export type LayoutAlgorithm = 'dagre' | 'force' | 'grid' | 'radial';
