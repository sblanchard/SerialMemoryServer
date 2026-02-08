import React, { memo } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import { NODE_TYPE_COLORS, cardStyle } from './nodeStyles';

interface ModuleNodeData {
  readonly label: string;
  readonly purpose: string;
  readonly keyFiles: readonly string[];
  [key: string]: unknown;
}

function ModuleNodeComponent({ data }: NodeProps) {
  const d = data as unknown as ModuleNodeData;
  const purposeText = d.purpose ?? '';
  const purposePreview = purposeText.slice(0, 150);
  const keyFiles = d.keyFiles ?? [];

  return (
    <div style={{
      ...cardStyle,
      borderLeft: `3px solid ${NODE_TYPE_COLORS.module}`,
      minWidth: 220,
      maxWidth: 320,
    }}>
      <Handle type="target" position={Position.Top} style={{ background: NODE_TYPE_COLORS.module }} />

      <div style={{ fontWeight: 700, fontSize: 14, marginBottom: 6, color: '#E2E8F0' }}>
        {d.label}
      </div>

      <div style={{ fontSize: 11, color: '#94A3B8', marginBottom: 8, lineHeight: 1.4 }}>
        {purposePreview}{purposeText.length > 150 ? '...' : ''}
      </div>

      {keyFiles.length > 0 && (
        <div>
          <div style={{ fontSize: 10, color: '#64748B', marginBottom: 2, fontWeight: 600 }}>Key Files</div>
          {keyFiles.slice(0, 5).map((file, i) => (
            <div key={i} style={{ fontSize: 10, color: '#94A3B8', padding: '1px 0' }}>
              {file}
            </div>
          ))}
          {keyFiles.length > 5 && (
            <div style={{ fontSize: 9, color: '#64748B' }}>+{keyFiles.length - 5} more</div>
          )}
        </div>
      )}

      <Handle type="source" position={Position.Bottom} style={{ background: NODE_TYPE_COLORS.module }} />
      <Handle type="source" position={Position.Right} id="dep-out" style={{ background: NODE_TYPE_COLORS.module, top: '50%' }} />
      <Handle type="target" position={Position.Left} id="dep-in" style={{ background: NODE_TYPE_COLORS.module, top: '50%' }} />
    </div>
  );
}

export const ModuleNode = memo(ModuleNodeComponent);
