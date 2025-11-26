import { useQuery } from '@tanstack/react-query';
import { fetchStats } from '../lib/api';

export function useStats() {
  return useQuery({
    queryKey: ['stats'],
    queryFn: fetchStats,
    staleTime: 60000, // 1 minute
    refetchInterval: 60000, // Auto-refresh every minute
  });
}
