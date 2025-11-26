import { useState } from 'react';
import { Search, Zap, FileText, Blend } from 'lucide-react';

interface SearchPanelProps {
  onSearch: (query: string, mode: 'semantic' | 'text' | 'hybrid') => void;
  isLoading?: boolean;
}

type SearchMode = 'semantic' | 'text' | 'hybrid';

const SEARCH_MODES: { value: SearchMode; label: string; icon: typeof Zap }[] = [
  { value: 'hybrid', label: 'Hybrid', icon: Blend },
  { value: 'semantic', label: 'Semantic', icon: Zap },
  { value: 'text', label: 'Text', icon: FileText },
];

export function SearchPanel({ onSearch, isLoading }: SearchPanelProps) {
  const [query, setQuery] = useState('');
  const [mode, setMode] = useState<SearchMode>('hybrid');

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (query.trim()) {
      onSearch(query.trim(), mode);
    }
  };

  return (
    <form onSubmit={handleSubmit} className="space-y-3">
      <div className="memory-search relative">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search memories..."
          className="pl-10"
        />
        <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-graphite" />
      </div>

      <div className="search-tabs">
        {SEARCH_MODES.map(m => (
          <button
            key={m.value}
            type="button"
            onClick={() => setMode(m.value)}
            className={`search-tab ${mode === m.value ? 'active' : ''}`}
          >
            <m.icon className="w-3 h-3" />
            {m.label}
          </button>
        ))}
      </div>

      <button
        type="submit"
        disabled={!query.trim() || isLoading}
        className="btn-primary w-full disabled:opacity-50 disabled:cursor-not-allowed disabled:transform-none"
      >
        {isLoading ? (
          <span className="flex items-center justify-center gap-2">
            <div className="w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />
            Searching...
          </span>
        ) : 'Search'}
      </button>
    </form>
  );
}
