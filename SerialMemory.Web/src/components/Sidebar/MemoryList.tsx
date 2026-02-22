import { useEffect, useRef, useState } from 'react';
import { Clock } from 'lucide-react';
import type { SearchMemory } from '../../types/graph';
import { getEntityColor } from '../../types/graph';

interface MemoryListProps {
  memories: SearchMemory[];
  isLoading?: boolean;
  title?: string;
  isLive?: boolean;
}

function formatDate(dateString: string): string {
  const normalized = dateString.endsWith('Z') ? dateString : dateString + 'Z';
  const date = new Date(normalized);
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

export function MemoryList({ memories, isLoading, title = 'Recent Memories', isLive }: MemoryListProps) {
  const listRef = useRef<HTMLDivElement>(null);
  const [knownIds, setKnownIds] = useState<Set<string>>(new Set());
  const [newIds, setNewIds] = useState<Set<string>>(new Set());
  const [userScrolled, setUserScrolled] = useState(false);
  const isInitialLoad = useRef(true);

  // Track which memories are new
  useEffect(() => {
    if (!memories.length) return;

    const currentIds = new Set(memories.map(m => m.id));

    if (isInitialLoad.current) {
      // First load — mark everything as known, nothing is "new"
      setKnownIds(currentIds);
      isInitialLoad.current = false;
      return;
    }

    const freshIds = new Set<string>();
    for (const id of currentIds) {
      if (!knownIds.has(id)) {
        freshIds.add(id);
      }
    }

    if (freshIds.size > 0) {
      setNewIds(freshIds);
      setKnownIds(currentIds);

      // Auto-scroll to top if user hasn't scrolled away
      if (!userScrolled && listRef.current) {
        listRef.current.scrollTo({ top: 0, behavior: 'smooth' });
      }

      // Clear "new" highlight after animation completes
      const timer = setTimeout(() => setNewIds(new Set()), 2000);
      return () => clearTimeout(timer);
    }
  }, [memories]);

  // Detect manual scrolling
  useEffect(() => {
    const el = listRef.current;
    if (!el) return;

    const handleScroll = () => {
      setUserScrolled(el.scrollTop > 20);
    };

    el.addEventListener('scroll', handleScroll, { passive: true });
    return () => el.removeEventListener('scroll', handleScroll);
  }, []);

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
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <h3 className="panel-header">{title}</h3>
          {isLive && (
            <span className="flex items-center gap-1 text-xs text-emerald-400">
              <span className="w-1.5 h-1.5 rounded-full bg-emerald-400 animate-pulse" />
              LIVE
            </span>
          )}
        </div>
        {newIds.size > 0 && (
          <span className="text-xs text-cyan animate-pulse">
            +{newIds.size} new
          </span>
        )}
      </div>
      <div
        ref={listRef}
        className="space-y-2 max-h-[400px] overflow-y-auto pr-1"
      >
        {memories.map(memory => (
          <div
            key={memory.id}
            className={`memory-item transition-all duration-500 ${
              newIds.has(memory.id)
                ? 'memory-item-new'
                : ''
            }`}
          >
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
