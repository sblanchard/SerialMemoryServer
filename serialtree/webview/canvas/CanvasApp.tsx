import React, { useCallback, useState, useMemo, useEffect } from 'react';
import {
  ReactFlow,
  Controls,
  MiniMap,
  Background,
  BackgroundVariant,
  useNodesState,
  useEdgesState,
  type Node,
  type Edge,
  type OnNodesChange,
  type OnEdgesChange,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';

import { nodeTypes } from './nodes/nodeTypes';
import { useCanvasMessages } from './hooks/useCanvasMessages';
import { useLayoutEngine } from './hooks/useLayoutEngine';
import { useNodeFilter } from './hooks/useNodeFilter';
import { CanvasToolbar } from './components/CanvasToolbar';
import { DetailPanel } from './components/DetailPanel';
import { transformToFlowNodes, transformToFlowEdges } from './transforms';
import type { CanvasNode, CanvasNodeType, LayoutAlgorithm } from './types';

interface VsCodeApi {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
}

interface CanvasAppProps {
  readonly vscode: VsCodeApi;
}

export function CanvasApp({ vscode }: CanvasAppProps): React.JSX.Element {
  const [nodes, setNodes, onNodesChange] = useNodesState([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState([]);
  const [selectedNode, setSelectedNode] = useState<CanvasNode | null>(null);
  const [layout, setLayout] = useState<LayoutAlgorithm>('grid');
  const [showMinimap, setShowMinimap] = useState(true);
  const [visibleTypes, setVisibleTypes] = useState<readonly CanvasNodeType[]>([
    'memory', 'entity', 'agent', 'finding', 'module', 'activity',
  ]);
  const [searchQuery, setSearchQuery] = useState('');
  const [canvasNodes, setCanvasNodes] = useState<readonly CanvasNode[]>([]);

  // Request data from extension on mount
  useEffect(() => {
    vscode.postMessage({ type: 'requestData' });
  }, [vscode]);

  // Handle messages from extension
  useCanvasMessages({
    onCanvasData: (newNodes, newEdges) => {
      setCanvasNodes(newNodes);
      setNodes(transformToFlowNodes(newNodes));
      setEdges(transformToFlowEdges(newEdges));
    },
    onNodeAdded: (node, newEdges) => {
      setCanvasNodes(prev => [...prev, node]);
      setNodes(prev => [...prev, ...transformToFlowNodes([node])]);
      setEdges(prev => [...prev, ...transformToFlowEdges(newEdges)]);
    },
    onNodeUpdated: (node) => {
      setCanvasNodes(prev => prev.map(n => n.id === node.id ? node : n));
      setNodes(prev => {
        const updated = transformToFlowNodes([node]);
        return prev.map(n => n.id === node.id ? (updated[0] ?? n) : n);
      });
    },
    onAgentStatusChanged: (agentId, status) => {
      setCanvasNodes(prev => prev.map(n =>
        n.id === agentId && n.nodeType === 'agent'
          ? { ...n, status } as CanvasNode
          : n,
      ));
    },
    onSettings: (settingsLayout, settingsShowMinimap) => {
      setLayout(settingsLayout as LayoutAlgorithm);
      setShowMinimap(settingsShowMinimap);
    },
  });

  // Apply layout when requested
  useLayoutEngine(nodes, edges, layout, setNodes);

  // Filter nodes by visible types
  const filteredNodes = useNodeFilter(nodes, visibleTypes);

  const handleNodeClick = useCallback((_event: React.MouseEvent, node: Node) => {
    const canvasNode = canvasNodes.find(n => n.id === node.id) ?? null;
    setSelectedNode(canvasNode);
  }, [canvasNodes]);

  const handleSearch = useCallback((query: string) => {
    setSearchQuery(query);
    vscode.postMessage({ type: 'search', query });
  }, [vscode]);

  const handleLayoutChange = useCallback((newLayout: LayoutAlgorithm) => {
    setLayout(newLayout);
    vscode.postMessage({ type: 'layoutRequested', layout: newLayout });
  }, [vscode]);

  const handleRefresh = useCallback(() => {
    vscode.postMessage({ type: 'requestData' });
  }, [vscode]);

  const handleFilterChange = useCallback((types: readonly CanvasNodeType[]) => {
    setVisibleTypes(types);
    vscode.postMessage({ type: 'filterChanged', visibleTypes: types });
  }, [vscode]);

  const handleNodeAction = useCallback((nodeId: string, action: string, payload?: Record<string, unknown>) => {
    switch (action) {
      case 'openFile':
        vscode.postMessage({ type: 'openFile', file: payload?.file, line: payload?.line });
        break;
      case 'sendToClaude':
        vscode.postMessage({ type: 'sendToClaude', content: payload?.content ?? '' });
        break;
      case 'dismiss':
        vscode.postMessage({ type: 'dismissFinding', findingId: payload?.findingId ?? nodeId });
        break;
      default:
        vscode.postMessage({ type: 'nodeAction', nodeId, action });
    }
  }, [vscode]);

  const handleCloseDetail = useCallback(() => {
    setSelectedNode(null);
  }, []);

  // Count nodes by type for toolbar stats
  const nodeCounts = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const n of canvasNodes) {
      counts[n.nodeType] = (counts[n.nodeType] ?? 0) + 1;
    }
    return counts;
  }, [canvasNodes]);

  return (
    <div style={{ width: '100%', height: '100%', position: 'relative' }}>
      <CanvasToolbar
        searchQuery={searchQuery}
        onSearch={handleSearch}
        onRefresh={handleRefresh}
        visibleTypes={visibleTypes}
        onFilterChange={handleFilterChange}
        layout={layout}
        onLayoutChange={handleLayoutChange}
        nodeCounts={nodeCounts}
        totalNodes={canvasNodes.length}
      />
      <ReactFlow
        nodes={filteredNodes}
        edges={edges}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onNodeClick={handleNodeClick}
        nodeTypes={nodeTypes}
        fitView
        minZoom={0.1}
        maxZoom={4}
        panOnDrag
        zoomOnScroll
        zoomOnPinch
        panOnScroll={false}
        preventScrolling
        defaultEdgeOptions={{ type: 'smoothstep', animated: false }}
        proOptions={{ hideAttribution: true }}
        style={{ background: '#0B0F1A' }}
      >
        <Controls position="bottom-left" style={{ background: '#1E293B', borderColor: '#334155' }} />
        {showMinimap && (
          <MiniMap
            position="bottom-right"
            maskColor="rgba(11,15,26,0.8)"
            style={{ background: '#1E293B' }}
          />
        )}
        <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="#1E293B" />
      </ReactFlow>
      {selectedNode && (
        <DetailPanel
          node={selectedNode}
          onClose={handleCloseDetail}
          onAction={handleNodeAction}
        />
      )}
    </div>
  );
}
