import React, { memo } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { NODE_TYPE_COLORS, cardStyle, badgeStyle, truncateStyle } from './nodeStyles';

interface MemoryNodeData {
  readonly label: string;
  readonly content: string;
  readonly similarity?: number;
  readonly memoryType?: string;
  readonly entities: ReadonlyArray<{ readonly name: string; readonly type: string }>;
  readonly timestamp?: string;
  [key: string]: unknown;
}

function MemoryNodeComponent({ data }: NodeProps) {
  const d = data as unknown as MemoryNodeData;
  const contentText = d.content ?? '';
  const preview = contentText.slice(0, 120);
  const similarityPct = d.similarity != null ? `${Math.round(d.similarity * 100)}%` : null;
  const entities = d.entities ?? [];

  return (
    <div style={{ ...cardStyle, borderLeft: `3px solid ${NODE_TYPE_COLORS.memory}` }}>
      <Handle type="target" position={Position.Top} style={{ background: NODE_TYPE_COLORS.memory }} />

      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 4 }}>
        {d.memoryType && (
          <span style={{ ...badgeStyle, background: 'rgba(34,211,238,0.15)', color: NODE_TYPE_COLORS.memory }}>
            {d.memoryType}
          </span>
        )}
        {similarityPct && (
          <span style={{ fontSize: 10, color: '#94A3B8' }}>{similarityPct}</span>
        )}
      </div>

      <div style={{ ...truncateStyle, marginBottom: 6, lineHeight: 1.4, whiteSpace: 'normal' }}>
        {preview}{preview.length < contentText.length ? '...' : ''}
      </div>

      {entities.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 2 }}>
          {entities.slice(0, 4).map((ent, i) => (
            <span key={i} style={{ ...badgeStyle, background: 'rgba(59,130,246,0.15)', color: '#93C5FD', fontSize: 9 }}>
              {ent.name}
            </span>
          ))}
          {entities.length > 4 && (
            <span style={{ fontSize: 9, color: '#64748B' }}>+{entities.length - 4}</span>
          )}
        </div>
      )}

      {d.timestamp && (
        <div style={{ fontSize: 9, color: '#64748B', marginTop: 4 }}>
          {formatTimestamp(d.timestamp)}
        </div>
      )}

      <Handle type="source" position={Position.Bottom} style={{ background: NODE_TYPE_COLORS.memory }} />
    </div>
  );
}

function formatTimestamp(ts: string): string {
  try {
    const date = new Date(ts);
    return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  } catch {
    return ts;
  }
}

export const MemoryNode = memo(MemoryNodeComponent);
