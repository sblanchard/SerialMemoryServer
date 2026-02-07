export type AgentRole = 'security' | 'performance' | 'patterns' | 'reviewer' | 'custom' | 'explorer' | 'explorer-sub';

export interface AgentConfig {
  readonly role: AgentRole;
  readonly label: string;
  readonly description: string;
  readonly icon: string;
}

export interface OrchestrateOptions {
  readonly timeout?: number;
  readonly customPrompt?: string;
  readonly pollInterval?: number;
}

export interface SpawnedAgent {
  readonly id: string;
  readonly role: AgentRole;
  readonly terminalName: string;
  readonly startedAt: number;
}

export interface AgentFinding {
  readonly role: AgentRole;
  readonly severity: 'critical' | 'high' | 'medium' | 'low';
  readonly title: string;
  readonly file?: string;
  readonly line?: number;
  readonly description: string;
  readonly suggestion?: string;
}

export interface RunResult {
  readonly runId: string;
  readonly roles: readonly AgentRole[];
  readonly findings: readonly AgentFinding[];
  readonly completedAgents: readonly string[];
  readonly timedOutAgents: readonly string[];
  readonly duration: number;
}

export interface ModuleDescriptor {
  readonly name: string;
  readonly memoryContent: string;
}

export interface ExplorerOptions {
  readonly maxModules?: number;
  readonly decompose?: boolean;
  readonly timeout?: number;
  readonly subAgentTimeout?: number;
  readonly pollInterval?: number;
}

export interface ExplorerRunResult {
  readonly runId: string;
  readonly modules: readonly ModuleDescriptor[];
  readonly subAgentResults: readonly string[];
  readonly completedSubAgents: readonly string[];
  readonly timedOutSubAgents: readonly string[];
  readonly duration: number;
}

export interface ExplorerParams {
  readonly maxModules?: number;
  readonly parentModuleContext?: string;
  readonly parentModuleName?: string;
}

export const AGENT_CONFIGS: Record<AgentRole, AgentConfig> = {
  security: {
    role: 'security',
    label: 'Security Reviewer',
    description: 'Auth, injection, secrets, OWASP vulnerabilities',
    icon: 'shield',
  },
  performance: {
    role: 'performance',
    label: 'Performance Analyst',
    description: 'N+1 queries, allocations, caching, indexing',
    icon: 'dashboard',
  },
  patterns: {
    role: 'patterns',
    label: 'Pattern Scanner',
    description: 'Tech debt, dead code, duplication, architecture',
    icon: 'symbol-structure',
  },
  reviewer: {
    role: 'reviewer',
    label: 'Code Reviewer',
    description: 'Bug detection, edge cases, error handling',
    icon: 'checklist',
  },
  custom: {
    role: 'custom',
    label: 'Custom Agent',
    description: 'User-defined analysis instruction',
    icon: 'wand',
  },
  explorer: {
    role: 'explorer',
    label: 'Codebase Explorer',
    description: 'Map codebase structure into module-level knowledge graph',
    icon: 'telescope',
  },
  'explorer-sub': {
    role: 'explorer-sub',
    label: 'Module Deep-Dive',
    description: 'Decompose a module into sub-components',
    icon: 'symbol-class',
  },
} as const;
