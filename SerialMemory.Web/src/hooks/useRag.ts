import { useState, useCallback } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { askMyMemory, getRagHistory, getRagStats, searchRagMemories } from '../lib/api';
import type { RagAnswerRequest, RagAnswerResponse, RagHistoryResponse, RagStats } from '../types/rag';

export interface UseRagOptions {
  includeL1?: boolean;
  includeL3?: boolean;
  includeL4?: boolean;
  maxMemories?: number;
  similarityThreshold?: number;
  temperature?: number;
  includeReasoningTrace?: boolean;
}

export function useRag(options: UseRagOptions = {}) {
  const queryClient = useQueryClient();
  const [currentResponse, setCurrentResponse] = useState<RagAnswerResponse | null>(null);

  // Mutation for asking questions
  const askMutation = useMutation({
    mutationFn: async (query: string) => {
      const request: RagAnswerRequest = {
        query,
        maxMemories: options.maxMemories ?? 5,
        includeL1: options.includeL1 ?? true,
        includeL3: options.includeL3 ?? true,
        includeL4: options.includeL4 ?? true,
        similarityThreshold: options.similarityThreshold ?? 0.5,
        temperature: options.temperature ?? 0.3,
        includeReasoningTrace: options.includeReasoningTrace ?? false,
      };
      return askMyMemory(request);
    },
    onSuccess: (data) => {
      setCurrentResponse(data);
      // Invalidate history to show the new query
      queryClient.invalidateQueries({ queryKey: ['rag-history'] });
    },
  });

  // Mutation for semantic search without answer generation
  const searchMutation = useMutation({
    mutationFn: async ({ query, limit }: { query: string; limit?: number }) => {
      return searchRagMemories({ query, limit });
    },
  });

  // Query for history
  const historyQuery = useQuery<RagHistoryResponse>({
    queryKey: ['rag-history'],
    queryFn: () => getRagHistory(10),
    staleTime: 30000, // 30 seconds
  });

  // Query for stats
  const statsQuery = useQuery<RagStats>({
    queryKey: ['rag-stats'],
    queryFn: getRagStats,
    staleTime: 60000, // 1 minute
  });

  // Ask a question
  const ask = useCallback((query: string) => {
    if (query.trim()) {
      askMutation.mutate(query.trim());
    }
  }, [askMutation]);

  // Search without generating an answer
  const search = useCallback((query: string, limit?: number) => {
    if (query.trim()) {
      searchMutation.mutate({ query: query.trim(), limit });
    }
  }, [searchMutation]);

  // Clear current response
  const clearResponse = useCallback(() => {
    setCurrentResponse(null);
  }, []);

  return {
    // State
    currentResponse,
    isAsking: askMutation.isPending,
    isSearching: searchMutation.isPending,
    error: askMutation.error?.message || searchMutation.error?.message,

    // Actions
    ask,
    search,
    clearResponse,

    // History
    history: historyQuery.data?.queries ?? [],
    isLoadingHistory: historyQuery.isLoading,

    // Stats
    stats: statsQuery.data,
    isLoadingStats: statsQuery.isLoading,

    // Search results (without answer)
    searchResults: searchMutation.data,
  };
}
