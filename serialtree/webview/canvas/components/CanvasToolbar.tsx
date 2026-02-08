import React, { useCallback, useState } from 'react';
import type { CanvasNodeType, LayoutAlgorithm } from '../types';
import { NODE_TYPE_COLORS } from '../nodes/nodeStyles';

interface CanvasToolbarProps {
  readonly searchQuery: string;
  readonly onSearch: (query: string) => void;
  readonly onRefresh: () => void;
  readonly visibleTypes: readonly CanvasNodeType[];
  readonly onFilterChange: (types: readonly CanvasNodeType[]) => void;
  readonly layout: LayoutAlgorithm;
  readonly onLayoutChange: (layout: LayoutAlgorithm) => void;
  readonly nodeCounts: Readonly<Record<string, number>>;
  readonly totalNodes: number;
}

const ALL_TYPES: readonly CanvasNodeType[] = [
  'memory',
  'entity',
  'agent',
  'finding',
  'module',
  'activity',
];

const TYPE_LABELS: Readonly<Record<string, string>> = {
  memory: 'Memories',
  entity: 'Entities',
  agent: 'Agents',
  finding: 'Findings',
  module: 'Modules',
  activity: 'Activity',
};

const LAYOUT_OPTIONS: readonly {
  readonly value: LayoutAlgorithm;
  readonly label: string;
}[] = [
  { value: 'grid', label: 'Grid' },
  { value: 'force', label: 'Force' },
  { value: 'radial', label: 'Radial' },
  { value: 'dagre', label: 'Dagre' },
];

export function CanvasToolbar({
  searchQuery,
  onSearch,
  onRefresh,
  visibleTypes,
  onFilterChange,
  layout,
  onLayoutChange,
  nodeCounts,
  totalNodes,
}: CanvasToolbarProps): React.JSX.Element {
  const [inputValue, setInputValue] = useState(searchQuery);

  const handleSearchSubmit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      onSearch(inputValue);
    },
    [inputValue, onSearch],
  );

  const handleToggleType = useCallback(
    (nodeType: CanvasNodeType) => {
      const isVisible = visibleTypes.includes(nodeType);
      const updated = isVisible
        ? visibleTypes.filter(t => t !== nodeType)
        : [...visibleTypes, nodeType];
      onFilterChange(updated);
    },
    [visibleTypes, onFilterChange],
  );

  const handleLayoutSelect = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      onLayoutChange(e.target.value as LayoutAlgorithm);
    },
    [onLayoutChange],
  );

  return (
    <div style={toolbarStyle}>
      {/* Search */}
      <form onSubmit={handleSearchSubmit} style={{ display: 'flex', gap: 4 }}>
        <input
          type="text"
          value={inputValue}
          onChange={e => setInputValue(e.target.value)}
          placeholder="Search memories..."
          style={inputStyle}
        />
        <button type="submit" style={buttonStyle}>
          Search
        </button>
      </form>

      {/* Refresh */}
      <button onClick={onRefresh} style={buttonStyle} title="Refresh canvas data">
        Refresh
      </button>

      {/* Divider */}
      <span style={dividerStyle} />

      {/* Filter toggles */}
      <div style={{ display: 'flex', gap: 4, alignItems: 'center' }}>
        {ALL_TYPES.map(nodeType => {
          const isActive = visibleTypes.includes(nodeType);
          const count = nodeCounts[nodeType] ?? 0;
          return (
            <button
              key={nodeType}
              onClick={() => handleToggleType(nodeType)}
              style={{
                ...filterButtonStyle,
                opacity: isActive ? 1 : 0.35,
                borderColor: isActive
                  ? NODE_TYPE_COLORS[nodeType]
                  : '#475569',
              }}
              title={`${TYPE_LABELS[nodeType] ?? nodeType} (${count}) — click to ${isActive ? 'hide' : 'show'}`}
            >
              <span
                style={{
                  width: 8,
                  height: 8,
                  borderRadius: '50%',
                  background: NODE_TYPE_COLORS[nodeType],
                  display: 'inline-block',
                  marginRight: 4,
                }}
              />
              <span style={{ fontSize: 10 }}>{count}</span>
            </button>
          );
        })}
      </div>

      {/* Divider */}
      <span style={dividerStyle} />

      {/* Layout selector */}
      <select value={layout} onChange={handleLayoutSelect} style={selectStyle}>
        {LAYOUT_OPTIONS.map(opt => (
          <option key={opt.value} value={opt.value}>
            {opt.label}
          </option>
        ))}
      </select>

      {/* Node count */}
      <span style={{ fontSize: 10, color: '#64748B', marginLeft: 'auto' }}>
        {totalNodes} nodes
      </span>
    </div>
  );
}

const toolbarStyle: React.CSSProperties = {
  position: 'absolute',
  top: 8,
  left: 8,
  right: 8,
  zIndex: 10,
  display: 'flex',
  gap: 12,
  alignItems: 'center',
  background: 'rgba(15,23,42,0.92)',
  backdropFilter: 'blur(8px)',
  borderRadius: 8,
  padding: '6px 12px',
  border: '1px solid #1E293B',
};

const inputStyle: React.CSSProperties = {
  background: '#0F172A',
  border: '1px solid #334155',
  borderRadius: 4,
  color: '#E2E8F0',
  padding: '4px 10px',
  fontSize: 12,
  outline: 'none',
  width: 180,
};

const buttonStyle: React.CSSProperties = {
  background: '#1E293B',
  border: '1px solid #334155',
  borderRadius: 4,
  color: '#94A3B8',
  fontSize: 11,
  padding: '4px 10px',
  cursor: 'pointer',
};

const filterButtonStyle: React.CSSProperties = {
  background: 'transparent',
  border: '1px solid',
  borderRadius: 4,
  color: '#CBD5E1',
  fontSize: 10,
  padding: '2px 6px',
  cursor: 'pointer',
  display: 'flex',
  alignItems: 'center',
};

const selectStyle: React.CSSProperties = {
  background: '#0F172A',
  border: '1px solid #334155',
  borderRadius: 4,
  color: '#E2E8F0',
  fontSize: 11,
  padding: '4px 6px',
  outline: 'none',
};

const dividerStyle: React.CSSProperties = {
  width: 1,
  height: 20,
  background: '#334155',
  flexShrink: 0,
};
