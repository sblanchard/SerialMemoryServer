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
        <h3 className="text-sm font-medium text-gray-400 uppercase tracking-wide">{title}</h3>
        {[1, 2, 3].map(i => (
          <div key={i} className="bg-bg-tertiary rounded-lg p-3 animate-pulse">
            <div className="h-4 bg-gray-700 rounded mb-2 w-3/4" />
            <div className="h-3 bg-gray-700 rounded w-1/2" />
          </div>
        ))}
      </div>
    );
  }

  if (!memories.length) {
    return (
      <div className="space-y-3">
        <h3 className="text-sm font-medium text-gray-400 uppercase tracking-wide">{title}</h3>
        <div className="text-center text-gray-500 py-8">
          No memories found
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <h3 className="text-sm font-medium text-gray-400 uppercase tracking-wide">{title}</h3>
      <div className="space-y-2 max-h-96 overflow-y-auto pr-1">
        {memories.map(memory => (
          <div
            key={memory.id}
            className="bg-bg-tertiary rounded-lg p-3 hover:bg-opacity-80 transition-colors cursor-pointer"
          >
            <p className="text-sm text-gray-200 mb-2">
              {truncate(memory.content, 120)}
            </p>

            {/* Entity badges */}
            {memory.entities && memory.entities.length > 0 && (
              <div className="flex flex-wrap gap-1 mb-2">
                {memory.entities.slice(0, 5).map((entity, i) => {
                  const entityObj = typeof entity === 'string'
                    ? { name: entity, type: 'default' }
                    : entity;
                  return (
                    <span
                      key={i}
                      className="text-xs px-2 py-0.5 rounded-full"
                      style={{
                        backgroundColor: `${getEntityColor(entityObj.type || 'default')}20`,
                        color: getEntityColor(entityObj.type || 'default'),
                        border: `1px solid ${getEntityColor(entityObj.type || 'default')}40`,
                      }}
                    >
                      {typeof entity === 'string' ? entity : entity.name}
                    </span>
                  );
                })}
                {memory.entities.length > 5 && (
                  <span className="text-xs text-gray-500">
                    +{memory.entities.length - 5} more
                  </span>
                )}
              </div>
            )}

            {/* Timestamp and similarity */}
            <div className="flex items-center justify-between text-xs text-gray-500">
              <div className="flex items-center gap-1">
                <Clock className="w-3 h-3" />
                {formatDate(memory.createdAt)}
              </div>
              {memory.similarity !== undefined && memory.similarity > 0 && (
                <span className="text-primary">
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
