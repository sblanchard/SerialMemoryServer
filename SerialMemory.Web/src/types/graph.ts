// Entity types from the knowledge graph
export type EntityType = 'PERSON' | 'ORG' | 'GPE' | 'DATE' | 'TIME' | 'EMAIL' | 'URL' | 'TITLE' | 'MONEY' | 'PERCENT';

// Graph node from API
export interface GraphNode {
  id: string;
  label: string;
  group: EntityType | string;
  title?: string;
}

// Graph edge from API
export interface GraphEdge {
  from: string;
  to: string;
  label?: string;
  title?: string;
  dashes?: boolean;
}

// Memory from API
export interface Memory {
  id: string;
  content: string;
  createdAt: string;
  entities: string[];
}

// Full graph data response
export interface GraphData {
  nodes: GraphNode[];
  edges: GraphEdge[];
  memories: Memory[];
}

// Statistics response
export interface Stats {
  memories: number;
  entities: number;
  relationships: number;
}

// Entity with full details
export interface Entity {
  id: string;
  name: string;
  type: EntityType | string;
  confidence?: number;
}

// Search result memory
export interface SearchMemory {
  id: string;
  content: string;
  createdAt: string;
  similarity?: number;
  rank?: number;
  entities: Entity[];
}

// SerialMemory Theme - Node type colors
export const NODE_TYPE_COLORS: Record<string, string> = {
  memory: '#22D3EE',   // cyan
  entity: '#3B82F6',   // blue
  relation: '#9333EA', // violet
  user: '#22C55E',     // green
  default: '#6B7280',  // gray
};

// SerialMemory Theme - Link type colors
export const LINK_TYPE_COLORS: Record<string, string> = {
  direct: 'rgba(59,130,246,0.35)',  // blue
  weak: 'rgba(107,114,128,0.25)',   // gray
  default: 'rgba(34,211,238,0.35)', // cyan
};

// Color mapping for entity types
export const ENTITY_COLORS: Record<string, string> = {
  PERSON: '#3B82F6',   // blue (matches entity)
  ORG: '#9333EA',      // violet (matches relation)
  GPE: '#22C55E',      // green (matches user)
  DATE: '#f59e0b',
  TIME: '#f59e0b',
  EMAIL: '#22D3EE',    // cyan (matches memory)
  URL: '#ec4899',
  TITLE: '#6366f1',
  MONEY: '#22D3EE',
  PERCENT: '#22D3EE',
  default: '#6B7280',  // gray
};

// Get color for entity type
export function getEntityColor(type: string): string {
  return ENTITY_COLORS[type.toUpperCase()] || ENTITY_COLORS.default;
}

// Get color for node type (SerialMemory theme)
export function getNodeColor(nodeType: string): string {
  return NODE_TYPE_COLORS[nodeType.toLowerCase()] || NODE_TYPE_COLORS.default;
}

// Get color for link type (SerialMemory theme)
export function getLinkColor(linkType: string): string {
  return LINK_TYPE_COLORS[linkType.toLowerCase()] || LINK_TYPE_COLORS.default;
}

// Force graph node (extended for 3D rendering)
export interface ForceGraphNode extends GraphNode {
  x?: number;
  y?: number;
  z?: number;
  color?: string;
  __threeObj?: THREE.Object3D;
}

// Force graph link
export interface ForceGraphLink {
  source: string | ForceGraphNode;
  target: string | ForceGraphNode;
  label?: string;
  color?: string;
  value?: number;
}

// THREE.js type for node objects
declare global {
  namespace THREE {
    interface Object3D {}
  }
}
