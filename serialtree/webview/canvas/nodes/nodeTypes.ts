import { MemoryNode } from './MemoryNode';
import { EntityNode } from './EntityNode';
import { AgentNode } from './AgentNode';
import { FindingNode } from './FindingNode';
import { ModuleNode } from './ModuleNode';
import { ActivityNode } from './ActivityNode';
import type { NodeTypes } from '@xyflow/react';

export const nodeTypes: NodeTypes = {
  memory: MemoryNode,
  entity: EntityNode,
  agent: AgentNode,
  finding: FindingNode,
  module: ModuleNode,
  activity: ActivityNode,
};
