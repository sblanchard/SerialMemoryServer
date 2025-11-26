import { Brain, Users, GitBranch } from 'lucide-react';
import { useStats } from '../../hooks/useStats';

export function StatsPanel() {
  const { data: stats, isLoading, error } = useStats();

  if (isLoading) {
    return (
      <div className="grid grid-cols-3 gap-3">
        {[1, 2, 3].map(i => (
          <div key={i} className="bg-bg-tertiary rounded-lg p-3 animate-pulse">
            <div className="h-6 bg-gray-700 rounded mb-2" />
            <div className="h-4 bg-gray-700 rounded w-16" />
          </div>
        ))}
      </div>
    );
  }

  if (error || !stats) {
    return (
      <div className="text-gray-400 text-sm">Failed to load statistics</div>
    );
  }

  const statItems = [
    { label: 'Memories', value: stats.memories, icon: Brain, color: 'text-entity-person' },
    { label: 'Entities', value: stats.entities, icon: Users, color: 'text-entity-org' },
    { label: 'Relations', value: stats.relationships, icon: GitBranch, color: 'text-entity-gpe' },
  ];

  return (
    <div className="grid grid-cols-3 gap-3">
      {statItems.map(item => (
        <div
          key={item.label}
          className="bg-bg-tertiary rounded-lg p-3 text-center"
        >
          <div className={`text-2xl font-bold ${item.color}`}>
            {item.value.toLocaleString()}
          </div>
          <div className="text-xs text-gray-400 flex items-center justify-center gap-1 mt-1">
            <item.icon className="w-3 h-3" />
            {item.label}
          </div>
        </div>
      ))}
    </div>
  );
}
