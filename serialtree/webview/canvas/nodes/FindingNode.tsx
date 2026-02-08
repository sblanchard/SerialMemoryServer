import React, { memo, useCallback } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { NODE_TYPE_COLORS, SEVERITY_COLORS, cardStyle, badgeStyle, truncateStyle } from './nodeStyles';
import { useVsCodeApi } from '../hooks/VsCodeContext';

interface FindingNodeData {
  readonly id: string;
  readonly label: string;
  readonly severity: 'critical' | 'high' | 'medium' | 'low';
  readonly findingType: string;
  readonly description: string;
  readonly file?: string;
  readonly line?: number;
  readonly suggestion?: string;
  [key: string]: unknown;
}

const actionButtonStyle: React.CSSProperties = {
  background: 'rgba(255,255,255,0.05)',
  border: '1px solid #334155',
  borderRadius: 4,
  color: '#94A3B8',
  fontSize: 10,
  padding: '2px 8px',
  cursor: 'pointer',
};

const dismissButtonStyle: React.CSSProperties = {
  ...actionButtonStyle,
  color: '#64748B',
};

function buildFindingContent(d: FindingNodeData): string {
  return [
    `[${d.findingType}] ${d.label}`,
    d.file ? `File: ${d.file}${d.line ? `:${d.line}` : ''}` : '',
    `Severity: ${d.severity}`,
    d.description,
    d.suggestion ? `Suggestion: ${d.suggestion}` : '',
  ].filter(Boolean).join('\n');
}

function buildFindingId(d: FindingNodeData): string {
  return d.file ? `${d.file}:${d.line ?? 0}:${d.label}` : d.id;
}

function FindingNodeComponent({ data }: NodeProps) {
  const d = data as unknown as FindingNodeData;
  const vscode = useVsCodeApi();
  const sevColor = SEVERITY_COLORS[d.severity] ?? SEVERITY_COLORS.medium;

  const handleSendToClaude = useCallback((e: React.MouseEvent) => {
    e.stopPropagation();
    const content = buildFindingContent(d);
    vscode.postMessage({ type: 'sendToClaude', content });
  }, [d, vscode]);

  const handleOpenFile = useCallback((e: React.MouseEvent) => {
    e.stopPropagation();
    if (d.file) {
      vscode.postMessage({ type: 'openFile', file: d.file, line: d.line });
    }
  }, [d, vscode]);

  const handleDismiss = useCallback((e: React.MouseEvent) => {
    e.stopPropagation();
    const findingId = buildFindingId(d);
    vscode.postMessage({ type: 'dismissFinding', findingId });
  }, [d, vscode]);

  return (
    <div style={{
      ...cardStyle,
      borderLeft: `4px solid ${sevColor}`,
      minWidth: 200,
      maxWidth: 300,
    }}>
      <Handle type="target" position={Position.Top} style={{ background: NODE_TYPE_COLORS.finding }} />

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 6 }}>
        <span style={{ ...badgeStyle, background: `${sevColor}22`, color: sevColor, textTransform: 'uppercase' }}>
          {d.severity}
        </span>
        <span style={{ ...badgeStyle, background: 'rgba(107,114,128,0.2)', color: '#94A3B8' }}>
          {d.findingType}
        </span>
      </div>

      <div style={{ fontWeight: 600, marginBottom: 4, fontSize: 12, lineHeight: 1.3 }}>
        {d.label}
      </div>

      <div style={{
        ...truncateStyle,
        fontSize: 11,
        color: '#94A3B8',
        marginBottom: 6,
        whiteSpace: 'normal',
        WebkitLineClamp: 3,
        display: '-webkit-box',
        WebkitBoxOrient: 'vertical' as const,
      }}>
        {d.description}
      </div>

      {d.file && (
        <div style={{ fontSize: 10, color: '#64748B', marginBottom: 6 }}>
          {d.file}{d.line ? `:${d.line}` : ''}
        </div>
      )}

      <div style={{ display: 'flex', gap: 4, marginTop: 4 }}>
        <button onClick={handleSendToClaude} style={actionButtonStyle}>Send to Claude</button>
        {d.file && <button onClick={handleOpenFile} style={actionButtonStyle}>Open File</button>}
        <button onClick={handleDismiss} style={dismissButtonStyle}>Dismiss</button>
      </div>

      <Handle type="source" position={Position.Bottom} style={{ background: NODE_TYPE_COLORS.finding }} />
    </div>
  );
}

export const FindingNode = memo(FindingNodeComponent);
