import { useState, useCallback, useMemo } from 'react';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { MainLayout } from './components/Layout';
import { ForceGraph3D, GraphControls } from './components/Graph';
import { StatsPanel, SearchPanel, MemoryList } from './components/Sidebar';
import { useGraphData } from './hooks/useGraphData';
import { fetchRecentMemories, searchMemories } from './lib/api';
import type { SearchMemory, ForceGraphNode } from './types/graph';
import './index.css';

const queryClient = new QueryClient();

function AppContent() {
  const [searchQuery, setSearchQuery] = useState<string | undefined>();
  const [searchMode, setSearchMode] = useState<'semantic' | 'text' | 'hybrid'>('hybrid');
  const [selectedNode, setSelectedNode] = useState<ForceGraphNode | null>(null);
  const [activeFilters, setActiveFilters] = useState<string[]>([]);

  // Fetch graph data
  const { data: graphData, isLoading: graphLoading, refetch: refetchGraph } = useGraphData({
    limit: 100,
    query: searchQuery,
    hops: 2,
  });

  // Fetch memories for sidebar
  const { data: memories, isLoading: memoriesLoading } = useQuery({
    queryKey: ['memories', searchQuery, searchMode],
    queryFn: async () => {
      if (searchQuery) {
        return searchMemories(searchQuery, searchMode, 20);
      }
      return fetchRecentMemories(20);
    },
  });

  // Filter nodes based on active filters
  const filteredData = useMemo(() => {
    if (!graphData) return { nodes: [], links: [] };
    if (activeFilters.length === 0) return graphData;

    const filteredNodes = graphData.nodes.filter(
      node => activeFilters.includes(node.group.toUpperCase())
    );
    const nodeIds = new Set(filteredNodes.map(n => n.id));

    const filteredLinks = graphData.links.filter(link => {
      const sourceId = typeof link.source === 'string' ? link.source : link.source.id;
      const targetId = typeof link.target === 'string' ? link.target : link.target.id;
      return nodeIds.has(sourceId) && nodeIds.has(targetId);
    });

    return { nodes: filteredNodes, links: filteredLinks };
  }, [graphData, activeFilters]);

  // Handle search from sidebar
  const handleSidebarSearch = useCallback((query: string, mode: 'semantic' | 'text' | 'hybrid') => {
    setSearchQuery(query);
    setSearchMode(mode);
  }, []);

  // Handle search from graph controls
  const handleGraphSearch = useCallback((query: string) => {
    setSearchQuery(query || undefined);
  }, []);

  // Handle reset
  const handleReset = useCallback(() => {
    setSearchQuery(undefined);
    setSelectedNode(null);
    setActiveFilters([]);
    refetchGraph();
  }, [refetchGraph]);

  // Handle node click
  const handleNodeClick = useCallback((node: ForceGraphNode) => {
    setSelectedNode(node);
  }, []);

  // Sidebar content
  const sidebar = (
    <div className="memory-panel h-full">
      <StatsPanel />

      <div className="panel-divider" />

      <SearchPanel
        onSearch={handleSidebarSearch}
        isLoading={graphLoading || memoriesLoading}
      />

      <div className="panel-divider" />

      <MemoryList
        memories={(memories as SearchMemory[]) || []}
        isLoading={memoriesLoading}
        title={searchQuery ? 'Search Results' : 'Recent Memories'}
      />

      {selectedNode && (
        <>
          <div className="panel-divider" />
          <div>
            <h3 className="panel-header">Selected Entity</h3>
            <div className="selected-entity-card">
              <div
                className="text-lg font-semibold mb-1"
                style={{ color: selectedNode.color }}
              >
                {selectedNode.label}
              </div>
              <div className="text-sm text-graphite capitalize">{selectedNode.group}</div>
            </div>
          </div>
        </>
      )}
    </div>
  );

  return (
    <MainLayout sidebar={sidebar}>
      {/* Graph controls */}
      <GraphControls
        onSearch={handleGraphSearch}
        onFilterChange={setActiveFilters}
        onReset={handleReset}
      />

      {/* Graph canvas background */}
      <div
        className="absolute inset-0 z-0"
        style={{
          background: `
            radial-gradient(circle at 50% 0%, rgba(34,211,238,0.06), transparent 40%),
            radial-gradient(circle at 90% 90%, rgba(59,130,246,0.04), transparent 40%),
            #0B0F1A
          `
        }}
      />

      {/* Loading state */}
      {graphLoading && (
        <div className="absolute inset-0 flex items-center justify-center z-20">
          <div className="text-center">
            <div className="spinner mx-auto mb-4" />
            <p className="text-graphite text-sm">Loading graph...</p>
          </div>
        </div>
      )}

      {/* 3D Graph */}
      {filteredData.nodes.length > 0 ? (
        <div className="absolute inset-0">
          <ForceGraph3D
            nodes={filteredData.nodes}
            links={filteredData.links}
            onNodeClick={handleNodeClick}
            clusterByType={true}
          />
        </div>
      ) : !graphLoading && (
        <div className="absolute inset-0 flex items-center justify-center">
          <div className="text-center">
            <div className="w-16 h-16 rounded-2xl bg-navy/50 border border-slate/30 flex items-center justify-center mx-auto mb-4">
              <svg className="w-8 h-8 text-graphite" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M19.428 15.428a2 2 0 00-1.022-.547l-2.387-.477a6 6 0 00-3.86.517l-.318.158a6 6 0 01-3.86.517L6.05 15.21a2 2 0 00-1.806.547M8 4h8l-1 1v5.172a2 2 0 00.586 1.414l5 5c1.26 1.26.367 3.414-1.415 3.414H4.828c-1.782 0-2.674-2.154-1.414-3.414l5-5A2 2 0 009 10.172V5L8 4z" />
              </svg>
            </div>
            <p className="text-soft-white text-lg font-medium mb-1">No data to display</p>
            <p className="text-graphite text-sm">Try searching for something or ingesting memories</p>
          </div>
        </div>
      )}
    </MainLayout>
  );
}

function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <AppContent />
    </QueryClientProvider>
  );
}

export default App;
