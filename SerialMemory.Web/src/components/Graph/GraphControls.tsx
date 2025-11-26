import { useState } from 'react';
import { Search, Filter, RotateCcw, Box, Grid3X3 } from 'lucide-react';
import { ENTITY_COLORS } from '../../types/graph';

interface GraphControlsProps {
  onSearch: (query: string) => void;
  onFilterChange: (types: string[]) => void;
  onReset: () => void;
  on2DToggle?: () => void;
  is3D?: boolean;
}

const ENTITY_TYPES = Object.keys(ENTITY_COLORS).filter(k => k !== 'default');

export function GraphControls({
  onSearch,
  onFilterChange,
  onReset,
  on2DToggle,
  is3D = true,
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
    <div className="absolute top-4 left-4 z-10 flex flex-col gap-2">
      {/* Search */}
      <div className="flex gap-2">
        <div className="relative">
          <input
            type="text"
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Search graph..."
            className="bg-bg-secondary border border-gray-600 rounded-lg px-4 py-2 pl-10 text-sm w-64 focus:outline-none focus:border-primary"
          />
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" />
        </div>
        <button
          onClick={handleSearch}
          className="bg-primary hover:bg-primary-hover text-white px-4 py-2 rounded-lg text-sm transition-colors"
        >
          Search
        </button>
      </div>

      {/* Control buttons */}
      <div className="flex gap-2">
        <button
          onClick={() => setShowFilters(!showFilters)}
          className={`flex items-center gap-2 px-3 py-2 rounded-lg text-sm transition-colors ${
            showFilters ? 'bg-primary text-white' : 'bg-bg-secondary text-gray-300 hover:bg-bg-tertiary'
          }`}
        >
          <Filter className="w-4 h-4" />
          Filters
        </button>

        <button
          onClick={onReset}
          className="flex items-center gap-2 bg-bg-secondary text-gray-300 hover:bg-bg-tertiary px-3 py-2 rounded-lg text-sm transition-colors"
        >
          <RotateCcw className="w-4 h-4" />
          Reset
        </button>

        {on2DToggle && (
          <button
            onClick={on2DToggle}
            className="flex items-center gap-2 bg-bg-secondary text-gray-300 hover:bg-bg-tertiary px-3 py-2 rounded-lg text-sm transition-colors"
          >
            {is3D ? <Grid3X3 className="w-4 h-4" /> : <Box className="w-4 h-4" />}
            {is3D ? '2D' : '3D'}
          </button>
        )}
      </div>

      {/* Filter panel */}
      {showFilters && (
        <div className="bg-bg-secondary border border-gray-600 rounded-lg p-4 mt-2 w-72">
          <div className="flex justify-between items-center mb-3">
            <span className="text-sm font-medium">Entity Types</span>
            <div className="flex gap-2">
              <button onClick={selectAll} className="text-xs text-primary hover:underline">
                All
              </button>
              <button onClick={selectNone} className="text-xs text-gray-400 hover:underline">
                None
              </button>
            </div>
          </div>
          <div className="grid grid-cols-2 gap-2">
            {ENTITY_TYPES.map(type => (
              <label
                key={type}
                className="flex items-center gap-2 cursor-pointer text-sm"
              >
                <input
                  type="checkbox"
                  checked={activeFilters.has(type)}
                  onChange={() => toggleFilter(type)}
                  className="rounded border-gray-600"
                />
                <span
                  className="w-3 h-3 rounded-full"
                  style={{ backgroundColor: ENTITY_COLORS[type] }}
                />
                <span className="text-gray-300">{type}</span>
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
