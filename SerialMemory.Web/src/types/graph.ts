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

// Color mapping for entity types
export const ENTITY_COLORS: Record<string, string> = {
  PERSON: '#3b82f6',
  ORG: '#8b5cf6',
  GPE: '#10b981',
  DATE: '#f59e0b',
  TIME: '#f59e0b',
  EMAIL: '#06b6d4',
  URL: '#ec4899',
  TITLE: '#6366f1',
  MONEY: '#06b6d4',
  PERCENT: '#06b6d4',
  default: '#e94560',
};

// Get color for entity type
export function getEntityColor(type: string): string {
  return ENTITY_COLORS[type.toUpperCase()] || ENTITY_COLORS.default;
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
