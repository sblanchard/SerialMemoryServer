import { useEffect, useRef } from 'react';
import type { Node, Edge } from '@xyflow/react';
import type { LayoutAlgorithm } from '../types';

type SetNodes = React.Dispatch<React.SetStateAction<Node[]>>;

const COLUMN_SPACING = 320;
const ROW_SPACING = 180;

export function useLayoutEngine(
  nodes: readonly Node[],
  edges: readonly Edge[],
  layout: LayoutAlgorithm,
  setNodes: SetNodes,
): void {
  const layoutRef = useRef(layout);
  const initialRef = useRef(true);

  useEffect(() => {
    if (layoutRef.current === layout && !initialRef.current) {
      return;
    }
    layoutRef.current = layout;
    initialRef.current = false;

    if (nodes.length === 0) {
      return;
    }

    const positioned = applyLayout(nodes, edges, layout);
    setNodes(positioned);
  }, [layout, nodes.length, edges, setNodes]);
}

function applyLayout(
  nodes: readonly Node[],
  edges: readonly Edge[],
  layout: LayoutAlgorithm,
): Node[] {
  switch (layout) {
    case 'grid':
      return gridLayout(nodes);
    case 'force':
      return forceLayout(nodes, edges);
    case 'radial':
      return radialLayout(nodes, edges);
    case 'dagre':
      return dagreLayout(nodes, edges);
    default:
      return gridLayout(nodes);
  }
}

function gridLayout(nodes: readonly Node[]): Node[] {
  const grouped = new Map<string, readonly Node[]>();
  for (const node of nodes) {
    const type = node.type ?? 'unknown';
    const existing = grouped.get(type) ?? [];
    grouped.set(type, [...existing, node]);
  }

  const result: Node[] = [];
  let colIndex = 0;

  for (const [, groupNodes] of grouped) {
    for (let rowIndex = 0; rowIndex < groupNodes.length; rowIndex++) {
      const node = groupNodes[rowIndex];
      result.push({
        ...node,
        position: { x: colIndex * COLUMN_SPACING, y: rowIndex * ROW_SPACING },
      });
    }
    colIndex++;
  }

  return result;
}

function forceLayout(nodes: readonly Node[], edges: readonly Edge[]): Node[] {
  const positions = new Map<string, { readonly x: number; readonly y: number }>();
  const width = Math.max(800, Math.sqrt(nodes.length) * 300);

  for (let i = 0; i < nodes.length; i++) {
    const angle = (2 * Math.PI * i) / nodes.length;
    const radius = width / 3;
    positions.set(nodes[i].id, {
      x: width / 2 + radius * Math.cos(angle),
      y: width / 2 + radius * Math.sin(angle),
    });
  }

  const iterations = 50;
  const repulsionForce = 5000;
  const attractionForce = 0.01;

  for (let iter = 0; iter < iterations; iter++) {
    const nodeList = [...positions.entries()];

    for (let i = 0; i < nodeList.length; i++) {
      for (let j = i + 1; j < nodeList.length; j++) {
        const [idA, posA] = nodeList[i];
        const [idB, posB] = nodeList[j];
        const dx = posA.x - posB.x;
        const dy = posA.y - posB.y;
        const dist = Math.max(Math.sqrt(dx * dx + dy * dy), 1);
        const force = repulsionForce / (dist * dist);
        const fx = (dx / dist) * force;
        const fy = (dy / dist) * force;
        positions.set(idA, { x: posA.x + fx, y: posA.y + fy });
        positions.set(idB, { x: posB.x - fx, y: posB.y - fy });
      }
    }

    for (const edge of edges) {
      const posA = positions.get(edge.source);
      const posB = positions.get(edge.target);
      if (!posA || !posB) {
        continue;
      }
      const dx = posB.x - posA.x;
      const dy = posB.y - posA.y;
      const fx = dx * attractionForce;
      const fy = dy * attractionForce;
      positions.set(edge.source, { x: posA.x + fx, y: posA.y + fy });
      positions.set(edge.target, { x: posB.x - fx, y: posB.y - fy });
    }
  }

  return nodes.map(node => {
    const pos = positions.get(node.id) ?? node.position;
    return { ...node, position: { x: Math.round(pos.x), y: Math.round(pos.y) } };
  });
}

function radialLayout(nodes: readonly Node[], edges: readonly Edge[]): Node[] {
  if (nodes.length === 0) {
    return [];
  }

  const connections = new Map<string, number>();
  for (const node of nodes) {
    connections.set(node.id, 0);
  }
  for (const edge of edges) {
    connections.set(edge.source, (connections.get(edge.source) ?? 0) + 1);
    connections.set(edge.target, (connections.get(edge.target) ?? 0) + 1);
  }

  const sorted = [...nodes].sort((a, b) =>
    (connections.get(b.id) ?? 0) - (connections.get(a.id) ?? 0),
  );

  const centerX = 400;
  const centerY = 400;
  const layerSpacing = 200;

  return sorted.map((node, i) => {
    if (i === 0) {
      return { ...node, position: { x: centerX, y: centerY } };
    }

    const layer = Math.ceil(Math.sqrt(i));
    const nodesInLayer = 2 * layer * Math.PI > i ? i : Math.floor(2 * layer * Math.PI);
    const angleStep = (2 * Math.PI) / Math.max(nodesInLayer, 6);
    const angle = angleStep * (i % Math.max(nodesInLayer, 6));
    const radius = layer * layerSpacing;

    return {
      ...node,
      position: {
        x: Math.round(centerX + radius * Math.cos(angle)),
        y: Math.round(centerY + radius * Math.sin(angle)),
      },
    };
  });
}

function dagreLayout(nodes: readonly Node[], edges: readonly Edge[]): Node[] {
  if (nodes.length === 0) {
    return [];
  }

  const nodeMap = new Map(nodes.map(n => [n.id, n]));
  const inDegree = new Map<string, number>();
  const children = new Map<string, readonly string[]>();

  for (const node of nodes) {
    inDegree.set(node.id, 0);
    children.set(node.id, []);
  }

  for (const edge of edges) {
    if (nodeMap.has(edge.source) && nodeMap.has(edge.target)) {
      inDegree.set(edge.target, (inDegree.get(edge.target) ?? 0) + 1);
      const existing = children.get(edge.source) ?? [];
      children.set(edge.source, [...existing, edge.target]);
    }
  }

  const layers = new Map<string, number>();
  const queue: string[] = [];

  for (const [id, degree] of inDegree) {
    if (degree === 0) {
      queue.push(id);
      layers.set(id, 0);
    }
  }

  let head = 0;
  while (head < queue.length) {
    const current = queue[head];
    head += 1;
    const currentLayer = layers.get(current) ?? 0;
    for (const child of (children.get(current) ?? [])) {
      if (!layers.has(child)) {
        layers.set(child, currentLayer + 1);
        queue.push(child);
      }
    }
  }

  for (const node of nodes) {
    if (!layers.has(node.id)) {
      layers.set(node.id, 0);
    }
  }

  const layerNodes = new Map<number, readonly Node[]>();
  for (const node of nodes) {
    const layer = layers.get(node.id) ?? 0;
    const existing = layerNodes.get(layer) ?? [];
    layerNodes.set(layer, [...existing, node]);
  }

  const result: Node[] = [];
  for (const [layer, layerGroup] of layerNodes) {
    for (let i = 0; i < layerGroup.length; i++) {
      result.push({
        ...layerGroup[i],
        position: {
          x: i * COLUMN_SPACING,
          y: layer * ROW_SPACING,
        },
      });
    }
  }

  return result;
}
