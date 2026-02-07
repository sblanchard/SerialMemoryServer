import { useState, useCallback, useEffect } from 'react';
import { createRoot } from 'react-dom/client';

// VS Code webview API
declare function acquireVsCodeApi(): {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
};

const vscode = acquireVsCodeApi();

type SearchMode = 'semantic' | 'text' | 'hybrid';

interface Entity {
  name: string;
  type: string;
}

interface SearchResult {
  id: string;
  content: string;
  createdAt: string;
  similarity?: number;
  entities: (Entity | string)[];
  memoryType?: string;
}

const ENTITY_COLORS: Record<string, string> = {
  PERSON: '#3B82F6',
  ORG: '#9333EA',
  GPE: '#22C55E',
  DATE: '#f59e0b',
  TIME: '#f59e0b',
  EMAIL: '#22D3EE',
  URL: '#ec4899',
  TITLE: '#6366f1',
  default: '#6B7280',
};

function getEntityColor(type: string): string {
  return ENTITY_COLORS[type.toUpperCase()] ?? ENTITY_COLORS.default;
}

function formatDate(dateString: string): string {
  const date = new Date(dateString);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMins / 60);
  const diffDays = Math.floor(diffHours / 24);

  if (diffMins < 60) { return `${diffMins}m ago`; }
  if (diffHours < 24) { return `${diffHours}h ago`; }
  if (diffDays < 7) { return `${diffDays}d ago`; }
  return date.toLocaleDateString();
}

function truncate(text: string, maxLength: number): string {
  return text.length <= maxLength ? text : text.slice(0, maxLength) + '...';
}

// --- Components ---

function SearchPanel({ onSearch, isLoading }: {
  onSearch: (query: string, mode: SearchMode) => void;
  isLoading: boolean;
}) {
  const [query, setQuery] = useState('');
  const [mode, setMode] = useState<SearchMode>('hybrid');

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (query.trim()) {
      onSearch(query.trim(), mode);
    }
  };

  const modes: { value: SearchMode; label: string }[] = [
    { value: 'hybrid', label: 'Hybrid' },
    { value: 'semantic', label: 'Semantic' },
    { value: 'text', label: 'Text' },
  ];

  return (
    <form onSubmit={handleSubmit} style={{ marginBottom: 12 }}>
      <input
        type="text"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Search memories..."
        style={{
          width: '100%',
          padding: '6px 8px',
          marginBottom: 8,
          background: 'var(--vscode-input-background)',
          color: 'var(--vscode-input-foreground)',
          border: '1px solid var(--vscode-input-border)',
          borderRadius: 3,
          fontSize: 13,
          outline: 'none',
        }}
      />
      <div style={{ display: 'flex', gap: 4, marginBottom: 8 }}>
        {modes.map(m => (
          <button
            key={m.value}
            type="button"
            onClick={() => setMode(m.value)}
            style={{
              flex: 1,
              padding: '4px 8px',
              fontSize: 11,
              cursor: 'pointer',
              border: '1px solid var(--vscode-button-border, transparent)',
              borderRadius: 3,
              background: mode === m.value
                ? 'var(--vscode-button-background)'
                : 'var(--vscode-button-secondaryBackground)',
              color: mode === m.value
                ? 'var(--vscode-button-foreground)'
                : 'var(--vscode-button-secondaryForeground)',
            }}
          >
            {m.label}
          </button>
        ))}
      </div>
      <button
        type="submit"
        disabled={!query.trim() || isLoading}
        style={{
          width: '100%',
          padding: '6px 12px',
          cursor: query.trim() && !isLoading ? 'pointer' : 'not-allowed',
          background: 'var(--vscode-button-background)',
          color: 'var(--vscode-button-foreground)',
          border: 'none',
          borderRadius: 3,
          fontSize: 13,
          opacity: !query.trim() || isLoading ? 0.5 : 1,
        }}
      >
        {isLoading ? 'Searching...' : 'Search'}
      </button>
    </form>
  );
}

function MemoryList({ memories, title }: {
  memories: SearchResult[];
  title: string;
}) {
  if (memories.length === 0) {
    return (
      <div style={{ textAlign: 'center', color: 'var(--vscode-descriptionForeground)', padding: 24, fontSize: 12 }}>
        No memories found
      </div>
    );
  }

  const handleClick = (memory: SearchResult) => {
    vscode.postMessage({ type: 'openMemory', memory });
  };

  return (
    <div>
      <div style={{ fontSize: 11, fontWeight: 600, marginBottom: 8, color: 'var(--vscode-descriptionForeground)', textTransform: 'uppercase' }}>
        {title} ({memories.length})
      </div>
      {memories.map(memory => (
        <div
          key={memory.id}
          onClick={() => handleClick(memory)}
          role="button"
          tabIndex={0}
          onKeyDown={(e) => { if (e.key === 'Enter') { handleClick(memory); } }}
          style={{
            padding: '8px 10px',
            marginBottom: 6,
            background: 'var(--vscode-list-hoverBackground)',
            borderRadius: 4,
            borderLeft: '3px solid var(--vscode-focusBorder)',
            cursor: 'pointer',
          }}
        >
          <div style={{ fontSize: 12, marginBottom: 4, lineHeight: 1.4 }}>
            {truncate(memory.content, 120)}
          </div>

          {memory.entities && memory.entities.length > 0 && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginBottom: 4 }}>
              {memory.entities.slice(0, 4).map((entity, i) => {
                const entityObj = typeof entity === 'string'
                  ? { name: entity, type: 'default' }
                  : entity;
                const color = getEntityColor(entityObj.type ?? 'default');
                return (
                  <span key={i} style={{
                    fontSize: 10,
                    padding: '1px 6px',
                    borderRadius: 10,
                    background: `${color}20`,
                    color,
                    border: `1px solid ${color}40`,
                  }}>
                    {typeof entity === 'string' ? entity : entity.name}
                  </span>
                );
              })}
              {memory.entities.length > 4 && (
                <span style={{ fontSize: 10, color: 'var(--vscode-descriptionForeground)' }}>
                  +{memory.entities.length - 4} more
                </span>
              )}
            </div>
          )}

          <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 10, color: 'var(--vscode-descriptionForeground)' }}>
            <span>{formatDate(memory.createdAt)}</span>
            {memory.similarity !== undefined && memory.similarity > 0 && (
              <span style={{ color: '#22D3EE' }}>
                {Math.round(memory.similarity * 100)}% match
              </span>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}

function App() {
  const [results, setResults] = useState<SearchResult[]>([]);
  const [title, setTitle] = useState('Recent Memories');
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleSearch = useCallback((query: string, mode: SearchMode) => {
    vscode.postMessage({ type: 'search', query, mode });
  }, []);

  useEffect(() => {
    const handler = (event: MessageEvent) => {
      const message = event.data;
      switch (message.type) {
        case 'results':
          setResults(message.results ?? []);
          setTitle(message.title ?? 'Results');
          setError(null);
          break;
        case 'loading':
          setIsLoading(message.value);
          break;
        case 'error':
          setError(message.message);
          setIsLoading(false);
          break;
      }
    };

    window.addEventListener('message', handler);
    vscode.postMessage({ type: 'loadRecent' });
    return () => window.removeEventListener('message', handler);
  }, []);

  return (
    <div style={{ padding: 4 }}>
      <SearchPanel onSearch={handleSearch} isLoading={isLoading} />
      {error && (
        <div style={{ padding: 8, marginBottom: 8, background: 'var(--vscode-inputValidation-errorBackground)', color: 'var(--vscode-inputValidation-errorForeground)', borderRadius: 3, fontSize: 12 }}>
          {error}
        </div>
      )}
      <MemoryList memories={results} title={title} />
    </div>
  );
}

// Mount
const root = document.getElementById('root');
if (root) {
  createRoot(root).render(<App />);
}
