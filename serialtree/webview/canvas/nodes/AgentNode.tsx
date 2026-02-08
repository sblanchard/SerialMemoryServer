import React, { memo } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { NODE_TYPE_COLORS, AGENT_STATUS_COLORS, cardStyle, badgeStyle } from './nodeStyles';

interface AgentNodeData {
  readonly label: string;
  readonly role: string;
  readonly status: 'running' | 'completed' | 'failed';
  readonly findingCount: number;
  readonly startedAt: number;
  readonly duration?: number;
  [key: string]: unknown;
}

function computeStatusIcon(status: string): string {
  switch (status) {
    case 'running': return '\u27F3';
    case 'completed': return '\u2713';
    case 'failed': return '\u2717';
    default: return '\u2022';
  }
}

function computeDurationText(d: AgentNodeData): string {
  if (d.duration != null) {
    return `${Math.round(d.duration / 1000)}s`;
  }
  if (d.status === 'running' && d.startedAt) {
    return `${Math.round((Date.now() - d.startedAt) / 1000)}s...`;
  }
  return '';
}

function AgentNodeComponent({ data }: NodeProps) {
  const d = data as unknown as AgentNodeData;
  const statusColor = AGENT_STATUS_COLORS[d.status] ?? '#6B7280';
  const statusIcon = computeStatusIcon(d.status);
  const durationText = computeDurationText(d);

  return (
    <div style={{
      ...cardStyle,
      borderLeft: `3px solid ${NODE_TYPE_COLORS.agent}`,
      border: `1px solid ${statusColor}44`,
    }}>
      <Handle type="target" position={Position.Top} style={{ background: NODE_TYPE_COLORS.agent }} />

      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 6 }}>
        <span style={{
          width: 20,
          height: 20,
          borderRadius: '50%',
          background: `${statusColor}22`,
          border: `2px solid ${statusColor}`,
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          fontSize: 11,
          color: statusColor,
          animation: d.status === 'running' ? 'serialtree-spin 1.5s linear infinite' : undefined,
        }}>
          {statusIcon}
        </span>
        <span style={{ fontWeight: 600, fontSize: 13 }}>{d.role}</span>
      </div>

      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 10, color: '#94A3B8' }}>
        <span>
          <span style={{ ...badgeStyle, background: 'rgba(245,158,11,0.15)', color: '#FBBF24' }}>
            {d.findingCount} findings
          </span>
        </span>
        {durationText && <span>{durationText}</span>}
      </div>

      <Handle type="source" position={Position.Bottom} style={{ background: NODE_TYPE_COLORS.agent }} />

      <style>{`
        @keyframes serialtree-spin {
          from { transform: rotate(0deg); }
          to { transform: rotate(360deg); }
        }
      `}</style>
    </div>
  );
}

export const AgentNode = memo(AgentNodeComponent);
