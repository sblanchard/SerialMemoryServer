import React, { memo } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { NODE_TYPE_COLORS, ENTITY_TYPE_COLORS, cardStyle, badgeStyle } from './nodeStyles';

interface EntityNodeData {
  readonly label: string;
  readonly entityType: string;
  readonly confidence?: number;
  [key: string]: unknown;
}

function EntityNodeComponent({ data }: NodeProps) {
  const d = data as unknown as EntityNodeData;
  const entityTypeKey = d.entityType?.toUpperCase() ?? '';
  const typeColor = ENTITY_TYPE_COLORS[entityTypeKey] ?? ENTITY_TYPE_COLORS.default;
  const typeInitial = (d.entityType ?? 'E')[0];

  return (
    <div style={{
      ...cardStyle,
      borderLeft: `3px solid ${typeColor}`,
      minWidth: 140,
      maxWidth: 200,
      textAlign: 'center',
    }}>
      <Handle type="target" position={Position.Top} style={{ background: typeColor }} />

      <div style={{
        width: 28,
        height: 28,
        borderRadius: '50%',
        background: `${typeColor}22`,
        border: `2px solid ${typeColor}`,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        margin: '0 auto 6px',
        fontSize: 12,
        fontWeight: 700,
        color: typeColor,
      }}>
        {typeInitial}
      </div>

      <div style={{ fontWeight: 600, marginBottom: 4, fontSize: 13 }}>
        {d.label}
      </div>

      <span style={{ ...badgeStyle, background: `${typeColor}22`, color: typeColor }}>
        {d.entityType}
      </span>

      {d.confidence != null && (
        <div style={{ fontSize: 9, color: '#64748B', marginTop: 4 }}>
          Confidence: {Math.round(d.confidence * 100)}%
        </div>
      )}

      <Handle type="source" position={Position.Bottom} style={{ background: typeColor }} />
    </div>
  );
}

export const EntityNode = memo(EntityNodeComponent);
