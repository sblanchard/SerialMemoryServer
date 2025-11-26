import { useState } from 'react';
import { Search, Zap, FileText, Blend } from 'lucide-react';

interface SearchPanelProps {
  onSearch: (query: string, mode: 'semantic' | 'text' | 'hybrid') => void;
  isLoading?: boolean;
}

type SearchMode = 'semantic' | 'text' | 'hybrid';

const SEARCH_MODES: { value: SearchMode; label: string; icon: typeof Zap; description: string }[] = [
  { value: 'hybrid', label: 'Hybrid', icon: Blend, description: 'Best of both' },
  { value: 'semantic', label: 'Semantic', icon: Zap, description: 'AI-powered' },
  { value: 'text', label: 'Text', icon: FileText, description: 'Keyword match' },
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
      <div className="relative">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search memories..."
          className="w-full bg-bg-tertiary border border-gray-600 rounded-lg px-4 py-3 pl-10 text-sm focus:outline-none focus:border-primary transition-colors"
        />
        <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" />
      </div>

      <div className="flex gap-1">
        {SEARCH_MODES.map(m => (
          <button
            key={m.value}
            type="button"
            onClick={() => setMode(m.value)}
            className={`flex-1 flex items-center justify-center gap-1 px-2 py-2 rounded-lg text-xs transition-colors ${
              mode === m.value
                ? 'bg-primary text-white'
                : 'bg-bg-tertiary text-gray-400 hover:text-gray-200'
            }`}
            title={m.description}
          >
            <m.icon className="w-3 h-3" />
            {m.label}
          </button>
        ))}
      </div>

      <button
        type="submit"
        disabled={!query.trim() || isLoading}
        className="w-full bg-primary hover:bg-primary-hover disabled:opacity-50 disabled:cursor-not-allowed text-white py-2 rounded-lg text-sm font-medium transition-colors"
      >
        {isLoading ? 'Searching...' : 'Search'}
      </button>
    </form>
  );
}
