// Entity types from the knowledge graph
export type EntityType = 'PERSON' | 'ORG' | 'GPE' | 'DATE' | 'TIME' | 'EMAIL' | 'URL' | 'TITLE' | 'MONEY' | 'PERCENT';

export interface GraphNode {
  id: string;
  label: string;
  group: EntityType | string;
  title?: string;
  x?: number;
  y?: number;
  z?: number;
  color?: string;
}

export interface GraphEdge {
  from: string;
  to: string;
  label?: string;
  title?: string;
  dashes?: boolean;
}

export interface GraphData {
  nodes: GraphNode[];
  edges: GraphEdge[];
  memories: Memory[];
}

export interface Memory {
  id: string;
  content: string;
  createdAt: string;
  entities: string[];
}

export interface Stats {
  memories: number;
  entities: number;
  relationships: number;
}

export interface Entity {
  id: string;
  name: string;
  type: EntityType | string;
  confidence?: number;
}

export interface ForceGraphLink {
  source: string | GraphNode;
  target: string | GraphNode;
  label?: string;
  color?: string;
  value?: number;
}

// SerialMemory theme colors
export const NODE_TYPE_COLORS: Record<string, string> = {
  memory: '#22D3EE',
  entity: '#3B82F6',
  relation: '#9333EA',
  user: '#22C55E',
  default: '#6B7280',
};

export const LINK_TYPE_COLORS: Record<string, string> = {
  direct: 'rgba(59,130,246,0.35)',
  weak: 'rgba(107,114,128,0.25)',
  default: 'rgba(34,211,238,0.35)',
};

export const ENTITY_COLORS: Record<string, string> = {
  PERSON: '#3B82F6',
  ORG: '#9333EA',
  GPE: '#22C55E',
  DATE: '#f59e0b',
  TIME: '#f59e0b',
  EMAIL: '#22D3EE',
  URL: '#ec4899',
  TITLE: '#6366f1',
  MONEY: '#22D3EE',
  PERCENT: '#22D3EE',
  default: '#6B7280',
};

export function getEntityColor(type: string): string {
  return ENTITY_COLORS[type.toUpperCase()] ?? ENTITY_COLORS.default;
}

export function getNodeColor(nodeType: string): string {
  return NODE_TYPE_COLORS[nodeType.toLowerCase()] ?? NODE_TYPE_COLORS.default;
}

export function getLinkColor(linkType: string): string {
  return LINK_TYPE_COLORS[linkType.toLowerCase()] ?? LINK_TYPE_COLORS.default;
}
