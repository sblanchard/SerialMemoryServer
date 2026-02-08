import type { Node, Edge } from '@xyflow/react';
import type { CanvasNode, CanvasEdge, CanvasNodeType } from './types';
import { EDGE_COLORS } from './nodes/nodeStyles';

const COLUMN_SPACING = 320;
const ROW_SPACING = 180;

// Column order for grid layout initial positioning
const TYPE_COLUMN_ORDER: readonly CanvasNodeType[] = [
  'agent', 'finding', 'memory', 'entity', 'module', 'activity',
];

/**
 * Transform CanvasNode[] into React Flow Node[] with grid-based initial positions.
 * Groups nodes by type and arranges them in columns.
 * Pure function - no mutations.
 */
export function transformToFlowNodes(canvasNodes: readonly CanvasNode[]): Node[] {
  // Group nodes by type for initial grid positioning
  const grouped = new Map<CanvasNodeType, readonly CanvasNode[]>();
  for (const node of canvasNodes) {
    const existing = grouped.get(node.nodeType) ?? [];
    grouped.set(node.nodeType, [...existing, node]);
  }

  const result: Node[] = [];
  let colIndex = 0;

  for (const nodeType of TYPE_COLUMN_ORDER) {
    const nodesOfType = grouped.get(nodeType) ?? [];
    if (nodesOfType.length === 0) {
      continue;
    }

    for (let rowIndex = 0; rowIndex < nodesOfType.length; rowIndex++) {
      const node = nodesOfType[rowIndex];
      if (!node) {
        continue;
      }
      result.push(createFlowNode(node, colIndex, rowIndex));
    }

    colIndex++;
  }

  return result;
}

/**
 * Create a single React Flow Node from a CanvasNode with grid position.
 * Pure function.
 */
function createFlowNode(node: CanvasNode, colIndex: number, rowIndex: number): Node {
  return {
    id: node.id,
    type: node.nodeType,
    position: {
      x: colIndex * COLUMN_SPACING,
      y: rowIndex * ROW_SPACING,
    },
    data: { ...node },
    draggable: true,
    selectable: true,
  };
}

/**
 * Transform CanvasEdge[] into React Flow Edge[] with styling.
 * Pure function - no mutations.
 */
export function transformToFlowEdges(canvasEdges: readonly CanvasEdge[]): Edge[] {
  return canvasEdges.map(edge => createFlowEdge(edge));
}

/**
 * Create a single React Flow Edge from a CanvasEdge with styling.
 * Pure function.
 */
function createFlowEdge(edge: CanvasEdge): Edge {
  return {
    id: edge.id,
    source: edge.source,
    target: edge.target,
    type: 'smoothstep',
    animated: edge.edgeType === 'agent-finding',
    label: edge.label,
    style: {
      stroke: EDGE_COLORS[edge.edgeType] ?? EDGE_COLORS.relationship,
      strokeWidth: edge.edgeType === 'relationship' ? 2 : 1,
    },
    labelStyle: {
      fill: '#94A3B8',
      fontSize: 10,
    },
  };
}

/**
 * Compute the visible edge set for a given set of visible node IDs.
 * Pure function.
 */
export function filterEdgesByVisibleNodes(
  edges: readonly Edge[],
  visibleNodeIds: ReadonlySet<string>,
): Edge[] {
  return edges.filter(edge =>
    visibleNodeIds.has(edge.source) && visibleNodeIds.has(edge.target),
  );
}
