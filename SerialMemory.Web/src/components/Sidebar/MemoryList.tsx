import { Clock } from 'lucide-react';
import type { SearchMemory } from '../../types/graph';
import { getEntityColor } from '../../types/graph';

interface MemoryListProps {
  memories: SearchMemory[];
  isLoading?: boolean;
  title?: string;
}

function formatDate(dateString: string): string {
  const date = new Date(dateString);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMins / 60);
  const diffDays = Math.floor(diffHours / 24);

  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;
  if (diffDays < 7) return `${diffDays}d ago`;
  return date.toLocaleDateString();
}

function truncate(text: string, maxLength: number): string {
  if (text.length <= maxLength) return text;
  return text.slice(0, maxLength) + '...';
}

export function MemoryList({ memories, isLoading, title = 'Recent Memories' }: MemoryListProps) {
  if (isLoading) {
    return (
      <div className="space-y-3">
        <h3 className="panel-header">{title}</h3>
        {[1, 2, 3].map(i => (
          <div key={i} className="memory-item animate-pulse">
            <div className="h-4 bg-slate/20 rounded mb-2 w-3/4" />
            <div className="h-3 bg-slate/15 rounded w-1/2" />
          </div>
        ))}
      </div>
    );
  }

  if (!memories.length) {
    return (
      <div className="space-y-3">
        <h3 className="panel-header">{title}</h3>
        <div className="text-center text-graphite py-8 text-sm">
          No memories found
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <h3 className="panel-header">{title}</h3>
      <div className="space-y-2 max-h-[400px] overflow-y-auto pr-1">
        {memories.map(memory => (
          <div key={memory.id} className="memory-item">
            <p className="text-sm text-soft-white/90 mb-2 leading-relaxed">
              {truncate(memory.content, 100)}
            </p>

            {/* Entity badges */}
            {memory.entities && memory.entities.length > 0 && (
              <div className="flex flex-wrap gap-1.5 mb-2">
                {memory.entities.slice(0, 4).map((entity, i) => {
                  const entityObj = typeof entity === 'string'
                    ? { name: entity, type: 'default' }
                    : entity;
                  const color = getEntityColor(entityObj.type || 'default');
                  return (
                    <span
                      key={i}
                      className="entity-badge"
                      style={{
                        backgroundColor: `${color}15`,
                        color: color,
                        border: `1px solid ${color}30`,
                      }}
                    >
                      {typeof entity === 'string' ? entity : entity.name}
                    </span>
                  );
                })}
                {memory.entities.length > 4 && (
                  <span className="text-xs text-graphite">
                    +{memory.entities.length - 4} more
                  </span>
                )}
              </div>
            )}

            {/* Timestamp and similarity */}
            <div className="flex items-center justify-between text-xs text-graphite">
              <div className="flex items-center gap-1">
                <Clock className="w-3 h-3" />
                {formatDate(memory.createdAt)}
              </div>
              {memory.similarity !== undefined && memory.similarity > 0 && (
                <span className="text-cyan font-medium">
                  {Math.round(memory.similarity * 100)}% match
                </span>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
