import type { SearchResult, CodeFinding, SessionEntry } from '../types/memory';
import type { GraphNode, GraphEdge, GraphData } from '../types/graph';
import type {
  SpawnedAgent,
  AgentFinding,
  ModuleDescriptor,
} from '../types/agent';

// ---------------------------------------------------------------------------
// Canvas node/edge types (mirrors webview/canvas/types.ts)
// Defined here to avoid cross-boundary imports between extension and webview.
// ---------------------------------------------------------------------------

export type CanvasNodeType =
  | 'memory'
  | 'entity'
  | 'agent'
  | 'finding'
  | 'module'
  | 'activity';

export type CanvasEdgeType =
  | 'relationship'
  | 'entity-link'
  | 'agent-finding'
  | 'finding-file'
  | 'module-dep'
  | 'activity-ref';

export interface CanvasNode {
  readonly id: string;
  readonly label: string;
  readonly nodeType: CanvasNodeType;
  readonly timestamp?: string;
  readonly [key: string]: unknown;
}

export interface CanvasEdge {
  readonly id: string;
  readonly source: string;
  readonly target: string;
  readonly edgeType: CanvasEdgeType;
  readonly label?: string;
}

export interface CanvasData {
  readonly nodes: readonly CanvasNode[];
  readonly edges: readonly CanvasEdge[];
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function sanitize(name: string): string {
  return name
    .replace(/[^a-zA-Z0-9]/g, '-')
    .toLowerCase()
    .slice(0, 30);
}

const FILE_PATH_REGEX = /(?:\.\/|src\/|lib\/|test\/|tests\/)[a-zA-Z0-9_\-./]+\.[a-zA-Z]{1,6}/g;

function extractFilePaths(content: string): readonly string[] {
  const matches = content.match(FILE_PATH_REGEX);
  if (!matches) {
    return [];
  }
  return [...new Set(matches)];
}

// ---------------------------------------------------------------------------
// Transform functions — all pure, returning new immutable objects
// ---------------------------------------------------------------------------

export function transformMemory(result: SearchResult): CanvasNode {
  return {
    id: `mem-${result.id}`,
    label: result.content.slice(0, 60),
    nodeType: 'memory',
    content: result.content,
    similarity: result.similarity,
    memoryType: result.memoryType,
    entities: result.entities.map((e) => ({ name: e.name, type: e.type })),
    source: result.source,
    timestamp: result.createdAt,
  };
}

export function transformEntity(node: GraphNode): CanvasNode {
  return {
    id: `ent-${node.id}`,
    label: node.label,
    nodeType: 'entity',
    entityType: node.group,
    confidence: undefined,
  };
}

export function transformAgent(
  agent: SpawnedAgent,
  status: 'running' | 'completed' | 'failed',
  findingCount: number,
): CanvasNode {
  return {
    id: `agt-${agent.id}`,
    label: agent.role,
    nodeType: 'agent',
    role: agent.role,
    status,
    findingCount,
    startedAt: agent.startedAt,
    timestamp: new Date(agent.startedAt).toISOString(),
  };
}

export function transformFinding(
  finding: AgentFinding | CodeFinding,
  index: number,
): CanvasNode {
  const file = (finding as CodeFinding).file ?? (finding as AgentFinding).file;
  const line = (finding as CodeFinding).line ?? (finding as AgentFinding).line;
  const findingType =
    (finding as CodeFinding).type ?? (finding as AgentFinding).role;

  return {
    id: `fnd-${index}-${file ?? 'unknown'}`,
    label: finding.title,
    nodeType: 'finding',
    severity: finding.severity,
    findingType,
    description: finding.description,
    file,
    line,
    suggestion: finding.suggestion,
  };
}

export function transformModule(
  mod: ModuleDescriptor,
  index: number,
): CanvasNode {
  return {
    id: `mod-${index}-${sanitize(mod.name)}`,
    label: mod.name,
    nodeType: 'module',
    purpose: mod.memoryContent.slice(0, 200),
    keyFiles: extractFilePaths(mod.memoryContent),
  };
}

export function transformActivity(
  entry: SessionEntry,
  index: number,
): CanvasNode {
  return {
    id: `act-${index}-${entry.timestamp}`,
    label: entry.content.slice(0, 60),
    nodeType: 'activity',
    activityType: entry.type,
    content: entry.content,
    file: entry.file,
    timestamp: entry.timestamp,
  };
}

// ---------------------------------------------------------------------------
// Edge builders — all pure, returning new arrays of immutable edges
// ---------------------------------------------------------------------------

export function buildEdgesFromGraph(
  graphEdges: readonly GraphEdge[],
): readonly CanvasEdge[] {
  return graphEdges.map((edge, i) => ({
    id: `rel-${i}-${edge.from}-${edge.to}`,
    source: `ent-${edge.from}`,
    target: `ent-${edge.to}`,
    edgeType: 'relationship' as const,
    label: edge.label,
  }));
}

export function buildEntityLinks(
  memories: readonly CanvasNode[],
  entities: readonly CanvasNode[],
  searchResults: readonly SearchResult[],
): readonly CanvasEdge[] {
  const entityIdSet = new Set(entities.map((e) => e.id));

  const edges: CanvasEdge[] = [];

  for (const result of searchResults) {
    const memoryNodeId = `mem-${result.id}`;
    const memoryExists = memories.some((m) => m.id === memoryNodeId);
    if (!memoryExists) {
      continue;
    }

    for (const entity of result.entities) {
      const entityNodeId = `ent-${entity.id}`;
      if (!entityIdSet.has(entityNodeId)) {
        continue;
      }

      edges.push({
        id: `elink-${result.id}-${entity.id}`,
        source: memoryNodeId,
        target: entityNodeId,
        edgeType: 'entity-link',
        label: entity.type,
      });
    }
  }

  return edges;
}

export function buildAgentFindingEdges(
  agents: readonly CanvasNode[],
  findings: readonly CanvasNode[],
  agentFindings: readonly AgentFinding[],
): readonly CanvasEdge[] {
  const agentIdByRole = new Map<string, string>();
  for (const agent of agents) {
    agentIdByRole.set(agent.role as string, agent.id);
  }

  const edges: CanvasEdge[] = [];

  for (let i = 0; i < agentFindings.length; i++) {
    const af = agentFindings[i];
    const agentNodeId = agentIdByRole.get(af.role);
    if (!agentNodeId) {
      continue;
    }

    const findingNodeId = `fnd-${i}-${af.file ?? 'unknown'}`;
    const findingExists = findings.some((f) => f.id === findingNodeId);
    if (!findingExists) {
      continue;
    }

    edges.push({
      id: `aflink-${agentNodeId}-${findingNodeId}`,
      source: agentNodeId,
      target: findingNodeId,
      edgeType: 'agent-finding',
      label: af.severity,
    });
  }

  return edges;
}

// ---------------------------------------------------------------------------
// Main aggregation
// ---------------------------------------------------------------------------

export interface AggregateParams {
  readonly graphData: GraphData;
  readonly searchResults: readonly SearchResult[];
  readonly agents: readonly SpawnedAgent[];
  readonly agentStatus: ReadonlyMap<string, 'running' | 'completed' | 'failed'>;
  readonly agentFindings: readonly AgentFinding[];
  readonly codeFindings: readonly CodeFinding[];
  readonly modules: readonly ModuleDescriptor[];
  readonly activities: readonly SessionEntry[];
  readonly maxNodes: number;
}

export function aggregateAll(params: AggregateParams): CanvasData {
  const {
    graphData,
    searchResults,
    agents,
    agentStatus,
    agentFindings,
    codeFindings,
    modules,
    activities,
    maxNodes,
  } = params;

  // --- Transform each data source into canvas nodes ---

  const agentNodes: readonly CanvasNode[] = agents.map((agent) => {
    const status = agentStatus.get(agent.id) ?? 'running';
    const findingCount = agentFindings.filter(
      (f) => f.role === agent.role,
    ).length;
    return transformAgent(agent, status, findingCount);
  });

  const allFindings: readonly (AgentFinding | CodeFinding)[] = [
    ...agentFindings,
    ...codeFindings,
  ];
  const findingNodes: readonly CanvasNode[] = allFindings.map((f, i) =>
    transformFinding(f, i),
  );

  const memoryNodes: readonly CanvasNode[] = searchResults.map(transformMemory);

  const entityNodes: readonly CanvasNode[] = graphData.nodes.map(transformEntity);

  const moduleNodes: readonly CanvasNode[] = modules.map((m, i) =>
    transformModule(m, i),
  );

  const activityNodes: readonly CanvasNode[] = activities.map((a, i) =>
    transformActivity(a, i),
  );

  // --- Truncate to maxNodes, prioritised: agents > findings > memories > entities > modules > activities ---

  const prioritised: readonly (readonly CanvasNode[])[] = [
    agentNodes,
    findingNodes,
    memoryNodes,
    entityNodes,
    moduleNodes,
    activityNodes,
  ];

  let remaining = maxNodes;
  const selectedGroups: (readonly CanvasNode[])[] = [];

  for (const group of prioritised) {
    if (remaining <= 0) {
      selectedGroups.push([]);
      continue;
    }
    const slice = group.slice(0, remaining);
    selectedGroups.push(slice);
    remaining -= slice.length;
  }

  const truncatedNodes: readonly CanvasNode[] = selectedGroups.flat();

  const selectedAgents = selectedGroups[0];
  const selectedFindings = selectedGroups[1];
  const selectedMemories = selectedGroups[2];
  const selectedEntities = selectedGroups[3];

  // --- Build edges (only for nodes that made the cut) ---

  const nodeIdSet = new Set(truncatedNodes.map((n) => n.id));

  const graphEdges = buildEdgesFromGraph(graphData.edges).filter(
    (e) => nodeIdSet.has(e.source) && nodeIdSet.has(e.target),
  );

  const entityLinkEdges = buildEntityLinks(
    selectedMemories,
    selectedEntities,
    searchResults,
  );

  const agentFindingEdges = buildAgentFindingEdges(
    selectedAgents,
    selectedFindings,
    agentFindings,
  );

  const allEdges: readonly CanvasEdge[] = [
    ...graphEdges,
    ...entityLinkEdges,
    ...agentFindingEdges,
  ];

  return {
    nodes: truncatedNodes,
    edges: allEdges,
  };
}
