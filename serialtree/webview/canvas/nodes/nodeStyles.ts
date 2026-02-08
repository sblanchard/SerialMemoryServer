import type { CanvasNodeType, CanvasEdgeType } from '../types';

export const NODE_TYPE_COLORS: Record<CanvasNodeType, string> = {
  memory: '#22D3EE',
  entity: '#3B82F6',
  agent: '#22C55E',
  finding: '#F59E0B',
  module: '#9333EA',
  activity: '#6B7280',
};

export const SEVERITY_COLORS: Record<string, string> = {
  critical: '#EF4444',
  high: '#F59E0B',
  medium: '#3B82F6',
  low: '#6B7280',
};

export const AGENT_STATUS_COLORS: Record<string, string> = {
  running: '#22C55E',
  completed: '#3B82F6',
  failed: '#EF4444',
};

export const EDGE_COLORS: Record<CanvasEdgeType | string, string> = {
  relationship: 'rgba(59,130,246,0.5)',
  'entity-link': 'rgba(34,211,238,0.4)',
  'agent-finding': 'rgba(34,197,94,0.5)',
  'finding-file': 'rgba(245,158,11,0.4)',
  'module-dep': 'rgba(147,51,234,0.4)',
  'activity-ref': 'rgba(107,114,128,0.3)',
};

export const ENTITY_TYPE_COLORS: Record<string, string> = {
  PERSON: '#3B82F6',
  ORG: '#9333EA',
  GPE: '#22C55E',
  DATE: '#F59E0B',
  TIME: '#F59E0B',
  EMAIL: '#22D3EE',
  URL: '#EC4899',
  TITLE: '#6366F1',
  default: '#6B7280',
};

export const ACTIVITY_TYPE_ICONS: Record<string, string> = {
  file_edit: '\u{1F4DD}',
  command: '\u{1F4BB}',
  error: '\u{274C}',
  finding: '\u{1F50D}',
  memory: '\u{1F9E0}',
  unknown: '\u{2753}',
};

// Shared card styles
export const cardStyle: React.CSSProperties = {
  background: '#1E293B',
  borderRadius: 8,
  padding: '10px 14px',
  color: '#E2E8F0',
  fontSize: 12,
  fontFamily: 'var(--vscode-font-family, "Segoe UI", sans-serif)',
  minWidth: 180,
  maxWidth: 280,
  boxShadow: '0 2px 8px rgba(0,0,0,0.3)',
};

export const badgeStyle: React.CSSProperties = {
  display: 'inline-block',
  padding: '1px 6px',
  borderRadius: 4,
  fontSize: 10,
  fontWeight: 600,
  marginRight: 4,
  marginBottom: 2,
};

export const truncateStyle: React.CSSProperties = {
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};
