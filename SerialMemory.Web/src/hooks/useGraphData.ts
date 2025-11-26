import { useQuery } from '@tanstack/react-query';
import { fetchGraphData } from '../lib/api';
import type { GraphData, ForceGraphNode, ForceGraphLink } from '../types/graph';
import { getEntityColor } from '../types/graph';

interface UseGraphDataOptions {
  limit?: number;
  query?: string;
  hops?: number;
}

// Transform API data to force-graph format
function transformGraphData(data: GraphData): { nodes: ForceGraphNode[]; links: ForceGraphLink[] } {
  const nodes: ForceGraphNode[] = data.nodes.map(node => ({
    ...node,
    color: getEntityColor(node.group),
  }));

  const links: ForceGraphLink[] = data.edges.map(edge => ({
    source: edge.from,
    target: edge.to,
    label: edge.label,
    color: edge.dashes ? '#4a4a6a' : '#6a6a8a',
  }));

  return { nodes, links };
}

export function useGraphData(options: UseGraphDataOptions = {}) {
  const { limit = 20, query, hops = 2 } = options;

  return useQuery({
    queryKey: ['graph', limit, query, hops],
    queryFn: () => fetchGraphData(limit, query, hops),
    select: transformGraphData,
    staleTime: 30000, // 30 seconds
  });
}
