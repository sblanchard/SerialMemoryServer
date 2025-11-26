import { useState } from 'react';
import { Search, Filter, RotateCcw, X } from 'lucide-react';
import { ENTITY_COLORS } from '../../types/graph';

interface GraphControlsProps {
  onSearch: (query: string) => void;
  onFilterChange: (types: string[]) => void;
  onReset: () => void;
}

const ENTITY_TYPES = Object.keys(ENTITY_COLORS).filter(k => k !== 'default');

export function GraphControls({
  onSearch,
  onFilterChange,
  onReset,
}: GraphControlsProps) {
  const [searchQuery, setSearchQuery] = useState('');
  const [activeFilters, setActiveFilters] = useState<Set<string>>(new Set(ENTITY_TYPES));
  const [showFilters, setShowFilters] = useState(false);

  const handleSearch = () => {
    onSearch(searchQuery);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') handleSearch();
  };

  const toggleFilter = (type: string) => {
    const newFilters = new Set(activeFilters);
    if (newFilters.has(type)) {
      newFilters.delete(type);
    } else {
      newFilters.add(type);
    }
    setActiveFilters(newFilters);
    onFilterChange(Array.from(newFilters));
  };

  const selectAll = () => {
    setActiveFilters(new Set(ENTITY_TYPES));
    onFilterChange(ENTITY_TYPES);
  };

  const selectNone = () => {
    setActiveFilters(new Set());
    onFilterChange([]);
  };

  return (
    <div className="absolute top-4 left-4 z-10 flex flex-col gap-2 items-start">
      {/* Search */}
      <div className="graph-controls flex gap-2">
        <div className="relative">
          <input
            type="text"
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Search graph..."
            className="graph-search w-48 pl-9 pr-3 py-2 text-sm"
          />
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-graphite" />
        </div>
        <button
          onClick={handleSearch}
          className="btn-primary px-4 py-2"
        >
          Search
        </button>
      </div>

      {/* Control buttons */}
      <div className="flex gap-2">
        <button
          onClick={() => setShowFilters(!showFilters)}
          className={`graph-control-btn ${showFilters ? 'active' : ''}`}
        >
          <Filter className="w-4 h-4" />
          Filters
        </button>

        <button
          onClick={onReset}
          className="graph-control-btn"
        >
          <RotateCcw className="w-4 h-4" />
          Reset
        </button>
      </div>

      {/* Filter panel */}
      {showFilters && (
        <div className="graph-controls w-72 animate-in slide-in-from-top-2 duration-200">
          <div className="flex justify-between items-center mb-3">
            <span className="text-sm font-medium text-soft-white">Entity Types</span>
            <div className="flex items-center gap-3">
              <button onClick={selectAll} className="text-xs text-electric hover:text-cyan transition-colors">
                All
              </button>
              <button onClick={selectNone} className="text-xs text-graphite hover:text-soft-white transition-colors">
                None
              </button>
              <button
                onClick={() => setShowFilters(false)}
                className="text-graphite hover:text-soft-white transition-colors"
              >
                <X className="w-4 h-4" />
              </button>
            </div>
          </div>
          <div className="grid grid-cols-2 gap-2">
            {ENTITY_TYPES.map(type => (
              <label
                key={type}
                className={`flex items-center gap-2 cursor-pointer text-sm p-2 rounded-lg transition-all ${
                  activeFilters.has(type)
                    ? 'bg-slate/20 text-soft-white'
                    : 'text-graphite hover:bg-slate/10'
                }`}
              >
                <input
                  type="checkbox"
                  checked={activeFilters.has(type)}
                  onChange={() => toggleFilter(type)}
                  className="hidden"
                />
                <div
                  className={`w-3 h-3 rounded-full ring-2 ring-offset-1 ring-offset-navy transition-all ${
                    activeFilters.has(type) ? 'ring-opacity-100' : 'ring-opacity-30'
                  }`}
                  style={{
                    backgroundColor: activeFilters.has(type) ? ENTITY_COLORS[type] : 'transparent',
                    borderColor: ENTITY_COLORS[type],
                    boxShadow: activeFilters.has(type) ? `0 0 8px ${ENTITY_COLORS[type]}60` : 'none'
                  }}
                />
                <span className="capitalize">{type}</span>
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
