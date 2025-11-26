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
    limit: 30,
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
    <>
      <StatsPanel />

      <div className="border-t border-gray-700 pt-4">
        <SearchPanel
          onSearch={handleSidebarSearch}
          isLoading={graphLoading || memoriesLoading}
        />
      </div>

      <div className="border-t border-gray-700 pt-4">
        <MemoryList
          memories={(memories as SearchMemory[]) || []}
          isLoading={memoriesLoading}
          title={searchQuery ? 'Search Results' : 'Recent Memories'}
        />
      </div>

      {selectedNode && (
        <div className="border-t border-gray-700 pt-4">
          <h3 className="text-sm font-medium text-gray-400 uppercase tracking-wide mb-3">
            Selected Entity
          </h3>
          <div className="bg-bg-tertiary rounded-lg p-4">
            <div
              className="text-lg font-medium mb-1"
              style={{ color: selectedNode.color }}
            >
              {selectedNode.label}
            </div>
            <div className="text-sm text-gray-400">{selectedNode.group}</div>
          </div>
        </div>
      )}
    </>
  );

  return (
    <MainLayout sidebar={sidebar}>
      {/* Graph controls */}
      <GraphControls
        onSearch={handleGraphSearch}
        onFilterChange={setActiveFilters}
        onReset={handleReset}
      />

      {/* Loading state */}
      {graphLoading && (
        <div className="absolute inset-0 flex items-center justify-center bg-bg-primary bg-opacity-50 z-20">
          <div className="text-center">
            <div className="w-12 h-12 border-4 border-primary border-t-transparent rounded-full animate-spin mx-auto mb-4" />
            <p className="text-gray-400">Loading graph...</p>
          </div>
        </div>
      )}

      {/* 3D Graph */}
      {filteredData.nodes.length > 0 ? (
        <ForceGraph3D
          nodes={filteredData.nodes}
          links={filteredData.links}
          onNodeClick={handleNodeClick}
          clusterByType={true}
        />
      ) : !graphLoading && (
        <div className="absolute inset-0 flex items-center justify-center">
          <div className="text-center text-gray-500">
            <p className="text-lg mb-2">No data to display</p>
            <p className="text-sm">Try searching for something or ingesting memories</p>
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
