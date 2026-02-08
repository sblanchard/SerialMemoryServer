import { useMemo } from 'react';
import type { Node } from '@xyflow/react';
import type { CanvasNodeType } from '../types';

export function useNodeFilter(
  nodes: readonly Node[],
  visibleTypes: readonly CanvasNodeType[],
): Node[] {
  return useMemo(() => {
    const visibleSet = new Set(visibleTypes);
    return nodes.map(node => {
      const nodeType = (node.type ?? 'unknown') as CanvasNodeType;
      const isVisible = visibleSet.has(nodeType);
      return isVisible === !node.hidden
        ? node
        : { ...node, hidden: !isVisible };
    });
  }, [nodes, visibleTypes]);
}
