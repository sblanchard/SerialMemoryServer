import { Brain, Users, GitBranch } from 'lucide-react';
import { useStats } from '../../hooks/useStats';

export function StatsPanel() {
  const { data: stats, isLoading, error } = useStats();

  if (isLoading) {
    return (
      <div className="grid grid-cols-3 gap-2">
        {[1, 2, 3].map(i => (
          <div key={i} className="graph-stat animate-pulse">
            <div className="h-7 bg-slate/30 rounded mb-1" />
            <div className="h-3 bg-slate/20 rounded w-12 mx-auto" />
          </div>
        ))}
      </div>
    );
  }

  if (error || !stats) {
    return (
      <div className="text-center text-graphite text-sm py-4">
        Failed to load statistics
      </div>
    );
  }

  const statItems = [
    { label: 'Memories', value: stats.memories, icon: Brain, color: 'text-cyan' },
    { label: 'Entities', value: stats.entities, icon: Users, color: 'text-electric' },
    { label: 'Relations', value: stats.relationships, icon: GitBranch, color: 'text-success' },
  ];

  return (
    <div className="grid grid-cols-3 gap-2">
      {statItems.map(item => (
        <div key={item.label} className="graph-stat group">
          <div className={`graph-stat-value ${item.color}`}>
            {item.value.toLocaleString()}
          </div>
          <div className="graph-stat-label flex items-center justify-center gap-1">
            <item.icon className="w-3 h-3" />
            {item.label}
          </div>
        </div>
      ))}
    </div>
  );
}
