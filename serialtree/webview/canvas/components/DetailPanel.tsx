import React from 'react';
import type {
  CanvasNode,
  MemoryCanvasNode,
  EntityCanvasNode,
  AgentCanvasNode,
  FindingCanvasNode,
  ModuleCanvasNode,
  ActivityCanvasNode,
} from '../types';
import { NODE_TYPE_COLORS, SEVERITY_COLORS } from '../nodes/nodeStyles';

interface DetailPanelProps {
  readonly node: CanvasNode;
  readonly onClose: () => void;
  readonly onAction: (
    nodeId: string,
    action: string,
    payload?: Readonly<Record<string, unknown>>,
  ) => void;
}

export function DetailPanel({
  node,
  onClose,
  onAction,
}: DetailPanelProps): React.JSX.Element {
  const typeColor = NODE_TYPE_COLORS[node.nodeType] ?? '#6B7280';

  return (
    <div style={panelStyle}>
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          marginBottom: 12,
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span
            style={{
              width: 10,
              height: 10,
              borderRadius: '50%',
              background: typeColor,
              display: 'inline-block',
            }}
          />
          <span
            style={{
              fontSize: 13,
              fontWeight: 600,
              color: '#E2E8F0',
              textTransform: 'capitalize',
            }}
          >
            {node.nodeType}
          </span>
        </div>
        <button onClick={onClose} style={closeButtonStyle}>
          x
        </button>
      </div>

      <div
        style={{
          fontSize: 15,
          fontWeight: 700,
          color: '#F1F5F9',
          marginBottom: 10,
        }}
      >
        {node.label}
      </div>

      {renderDetails(node, onAction)}
    </div>
  );
}

function renderDetails(
  node: CanvasNode,
  onAction: DetailPanelProps['onAction'],
): React.JSX.Element {
  switch (node.nodeType) {
    case 'memory':
      return <MemoryDetails node={node} />;
    case 'entity':
      return <EntityDetails node={node} />;
    case 'agent':
      return <AgentDetails node={node} />;
    case 'finding':
      return <FindingDetails node={node} onAction={onAction} />;
    case 'module':
      return <ModuleDetails node={node} />;
    case 'activity':
      return <ActivityDetails node={node} />;
    default:
      return <div style={{ color: '#94A3B8' }}>No details available</div>;
  }
}

function MemoryDetails({
  node,
}: {
  readonly node: MemoryCanvasNode;
}): React.JSX.Element {
  return (
    <div>
      {node.memoryType && (
        <div style={fieldStyle}>
          <strong>Type:</strong> {node.memoryType}
        </div>
      )}
      {node.similarity != null && (
        <div style={fieldStyle}>
          <strong>Similarity:</strong> {Math.round(node.similarity * 100)}%
        </div>
      )}
      <div style={{ ...fieldStyle, whiteSpace: 'pre-wrap', lineHeight: 1.5 }}>
        {node.content}
      </div>
      {node.entities.length > 0 && (
        <div style={fieldStyle}>
          <strong>Entities:</strong>
          <div
            style={{
              display: 'flex',
              flexWrap: 'wrap',
              gap: 4,
              marginTop: 4,
            }}
          >
            {node.entities.map((e, i) => (
              <span
                key={i}
                style={{
                  background: 'rgba(59,130,246,0.15)',
                  color: '#93C5FD',
                  padding: '2px 6px',
                  borderRadius: 4,
                  fontSize: 11,
                }}
              >
                {e.name} ({e.type})
              </span>
            ))}
          </div>
        </div>
      )}
      {node.timestamp && (
        <div style={fieldStyle}>
          <strong>Created:</strong> {node.timestamp}
        </div>
      )}
    </div>
  );
}

function EntityDetails({
  node,
}: {
  readonly node: EntityCanvasNode;
}): React.JSX.Element {
  return (
    <div>
      <div style={fieldStyle}>
        <strong>Type:</strong> {node.entityType}
      </div>
      {node.confidence != null && (
        <div style={fieldStyle}>
          <strong>Confidence:</strong> {Math.round(node.confidence * 100)}%
        </div>
      )}
    </div>
  );
}

function AgentDetails({
  node,
}: {
  readonly node: AgentCanvasNode;
}): React.JSX.Element {
  return (
    <div>
      <div style={fieldStyle}>
        <strong>Role:</strong> {node.role}
      </div>
      <div style={fieldStyle}>
        <strong>Status:</strong> {node.status}
      </div>
      <div style={fieldStyle}>
        <strong>Findings:</strong> {node.findingCount}
      </div>
      {node.duration != null && (
        <div style={fieldStyle}>
          <strong>Duration:</strong> {Math.round(node.duration / 1000)}s
        </div>
      )}
    </div>
  );
}

function FindingDetails({
  node,
  onAction,
}: {
  readonly node: FindingCanvasNode;
  readonly onAction: DetailPanelProps['onAction'];
}): React.JSX.Element {
  const sevColor = SEVERITY_COLORS[node.severity] ?? '#6B7280';

  return (
    <div>
      <div style={fieldStyle}>
        <span
          style={{
            color: sevColor,
            fontWeight: 600,
            textTransform: 'uppercase',
          }}
        >
          {node.severity}
        </span>
        {' — '}
        <span style={{ color: '#94A3B8' }}>{node.findingType}</span>
      </div>
      <div style={{ ...fieldStyle, whiteSpace: 'pre-wrap' }}>
        {node.description}
      </div>
      {node.file && (
        <div style={fieldStyle}>
          <strong>File:</strong> {node.file}
          {node.line ? `:${node.line}` : ''}
        </div>
      )}
      {node.suggestion && (
        <div style={fieldStyle}>
          <strong>Suggestion:</strong> {node.suggestion}
        </div>
      )}
      <div style={{ display: 'flex', gap: 6, marginTop: 10 }}>
        <button
          onClick={() =>
            onAction(node.id, 'sendToClaude', {
              content: `${node.label}\n${node.description}`,
            })
          }
          style={actionBtnStyle}
        >
          Send to Claude
        </button>
        {node.file && (
          <button
            onClick={() =>
              onAction(node.id, 'openFile', {
                file: node.file,
                line: node.line,
              })
            }
            style={actionBtnStyle}
          >
            Open File
          </button>
        )}
        <button
          onClick={() =>
            onAction(node.id, 'dismiss', {
              findingId: `${node.file}:${node.line}:${node.label}`,
            })
          }
          style={{ ...actionBtnStyle, color: '#64748B' }}
        >
          Dismiss
        </button>
      </div>
    </div>
  );
}

function ModuleDetails({
  node,
}: {
  readonly node: ModuleCanvasNode;
}): React.JSX.Element {
  return (
    <div>
      <div style={{ ...fieldStyle, whiteSpace: 'pre-wrap' }}>
        {node.purpose}
      </div>
      {node.keyFiles.length > 0 && (
        <div style={fieldStyle}>
          <strong>Key Files:</strong>
          {node.keyFiles.map((f, i) => (
            <div
              key={i}
              style={{ color: '#94A3B8', fontSize: 11, paddingLeft: 8 }}
            >
              {f}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function ActivityDetails({
  node,
}: {
  readonly node: ActivityCanvasNode;
}): React.JSX.Element {
  return (
    <div>
      <div style={fieldStyle}>
        <strong>Type:</strong> {node.activityType}
      </div>
      <div style={{ ...fieldStyle, whiteSpace: 'pre-wrap' }}>
        {node.content}
      </div>
      {node.file && (
        <div style={fieldStyle}>
          <strong>File:</strong> {node.file}
        </div>
      )}
      {node.timestamp && (
        <div style={fieldStyle}>
          <strong>Time:</strong> {node.timestamp}
        </div>
      )}
    </div>
  );
}

const panelStyle: React.CSSProperties = {
  position: 'absolute',
  top: 0,
  right: 0,
  width: 360,
  height: '100%',
  background: '#0F172A',
  borderLeft: '1px solid #1E293B',
  padding: 16,
  overflowY: 'auto',
  zIndex: 20,
  color: '#E2E8F0',
  fontSize: 12,
  fontFamily: 'var(--vscode-font-family, "Segoe UI", sans-serif)',
};

const closeButtonStyle: React.CSSProperties = {
  background: 'transparent',
  border: 'none',
  color: '#94A3B8',
  fontSize: 20,
  cursor: 'pointer',
  padding: '0 4px',
};

const fieldStyle: React.CSSProperties = {
  marginBottom: 8,
  color: '#CBD5E1',
  lineHeight: 1.4,
};

const actionBtnStyle: React.CSSProperties = {
  background: '#1E293B',
  border: '1px solid #334155',
  borderRadius: 4,
  color: '#94A3B8',
  fontSize: 11,
  padding: '4px 10px',
  cursor: 'pointer',
};
