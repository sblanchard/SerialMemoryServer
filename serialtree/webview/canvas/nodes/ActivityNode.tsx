import React, { memo } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { NODE_TYPE_COLORS, ACTIVITY_TYPE_ICONS, cardStyle, truncateStyle } from './nodeStyles';

interface ActivityNodeData {
  readonly label: string;
  readonly activityType: string;
  readonly content: string;
  readonly file?: string;
  readonly timestamp?: string;
  [key: string]: unknown;
}

function formatTime(ts: string): string {
  try {
    return new Date(ts).toLocaleTimeString(undefined, {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
  } catch {
    return ts;
  }
}

function ActivityNodeComponent({ data }: NodeProps) {
  const d = data as unknown as ActivityNodeData;
  const icon = ACTIVITY_TYPE_ICONS[d.activityType] ?? ACTIVITY_TYPE_ICONS.unknown;
  const contentText = (d.content ?? '').slice(0, 80);

  return (
    <div style={{
      ...cardStyle,
      borderLeft: `2px solid ${NODE_TYPE_COLORS.activity}`,
      opacity: 0.75,
      minWidth: 160,
      maxWidth: 220,
      padding: '6px 10px',
    }}>
      <Handle type="target" position={Position.Top} style={{ background: NODE_TYPE_COLORS.activity }} />

      <div style={{ display: 'flex', alignItems: 'center', gap: 4, marginBottom: 3 }}>
        <span style={{ fontSize: 12 }}>{icon}</span>
        <span style={{ fontSize: 10, color: '#64748B', textTransform: 'uppercase' }}>{d.activityType}</span>
      </div>

      <div style={{ ...truncateStyle, fontSize: 11, color: '#CBD5E1' }}>
        {contentText}
      </div>

      {d.timestamp && (
        <div style={{ fontSize: 9, color: '#475569', marginTop: 2 }}>
          {formatTime(d.timestamp)}
        </div>
      )}

      <Handle type="source" position={Position.Bottom} style={{ background: NODE_TYPE_COLORS.activity }} />
    </div>
  );
}

export const ActivityNode = memo(ActivityNodeComponent);
