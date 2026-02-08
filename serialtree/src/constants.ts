export const EXTENSION_ID = 'serialtree';

// Command IDs
export const CMD = {
  SHOW_GRAPH: `${EXTENSION_ID}.showGraph`,
  SEARCH_MEMORY: `${EXTENSION_ID}.searchMemory`,
  SHOW_ACTIVITY: `${EXTENSION_ID}.showActivity`,
  ANALYZE_WORKSPACE: `${EXTENSION_ID}.analyzeWorkspace`,
  INGEST_SELECTION: `${EXTENSION_ID}.ingestSelection`,
  SEND_TO_CLAUDE: `${EXTENSION_ID}.sendToClaudeCode`,
  SUMMARIZE_SESSION: `${EXTENSION_ID}.summarizeSession`,
  SHOW_FINDINGS: `${EXTENSION_ID}.showFindings`,
  REFRESH_ACTIVITY: `${EXTENSION_ID}.refreshActivity`,
  REFRESH_FINDINGS: `${EXTENSION_ID}.refreshFindings`,
  FINDING_SEND_TO_CLAUDE: `${EXTENSION_ID}.findingSendToClaude`,
  FINDING_OPEN_FILE: `${EXTENSION_ID}.findingOpenFile`,
  FINDING_DISMISS: `${EXTENSION_ID}.findingDismiss`,
  ORCHESTRATE_AGENTS: `${EXTENSION_ID}.orchestrateAgents`,
  EXPLORE_CODEBASE: `${EXTENSION_ID}.exploreCodebase`,
  SHOW_CANVAS: `${EXTENSION_ID}.showCanvas`,
} as const;

// View IDs
export const VIEW = {
  ACTIONS: `${EXTENSION_ID}.actions`,
  ACTIVITY: `${EXTENSION_ID}.activity`,
  FINDINGS: `${EXTENSION_ID}.findings`,
  CANVAS: `${EXTENSION_ID}.canvas`,
} as const;

// Config keys
export const CONFIG = {
  MCP_PROJECT_PATH: 'mcpProjectPath',
  API_URL: 'apiUrl',
  CONNECTION_MODE: 'connectionMode',
  CLAUDE_TERMINAL_PATTERN: 'claudeTerminalPattern',
  PROMPT_TEMPLATE: 'promptTemplate',
  AUTO_ANALYZE_ON_OPEN: 'autoAnalyzeOnOpen',
  ANALYSIS_SCOPE: 'analysisScope',
  MAX_FUNCTION_LENGTH: 'maxFunctionLength',
  MAX_FILE_LENGTH: 'maxFileLength',
  SESSION_LOG_DIR: 'sessionLogDir',
  SHOW_ACTIVITY_ON_START: 'showActivityOnStart',
  AGENT_POOL_SIZE: 'agentPoolSize',
  AGENT_TIMEOUT: 'agentTimeout',
  AGENT_SCOPE: 'agentScope',
  CLAUDE_COMMAND: 'claudeCommand',
  EXPLORER_MAX_MODULES: 'explorerMaxModules',
  EXPLORER_TIMEOUT: 'explorerTimeout',
  CANVAS_LAYOUT: 'canvasLayout',
  CANVAS_SHOW_MINIMAP: 'canvasShowMinimap',
  CANVAS_MAX_NODES: 'canvasMaxNodes',
} as const;

// Defaults
export const DEFAULTS = {
  API_URL: 'http://localhost:5000',
  CONNECTION_MODE: 'mcp' as const,
  CLAUDE_TERMINAL_PATTERN: 'Claude',
  MAX_FUNCTION_LENGTH: 50,
  MAX_FILE_LENGTH: 800,
  SESSION_LOG_DIR: '~/.cc-serialmemory/sessions',
  ANALYSIS_SCOPE: ['src/**/*.ts', '**/*.cs'],
  AGENT_POOL_SIZE: 3,
  AGENT_TIMEOUT: 300_000,
  AGENT_SCOPE: ['src/**/*.ts', '**/*.cs'],
  CLAUDE_COMMAND: 'claude',
  AGENT_POLL_INTERVAL: 10_000,
  EXPLORER_MAX_MODULES: 7,
  EXPLORER_TIMEOUT: 600_000,
  EXPLORER_SUB_TIMEOUT: 300_000,
  CANVAS_LAYOUT: 'grid' as const,
  CANVAS_SHOW_MINIMAP: true,
  CANVAS_MAX_NODES: 500,
} as const;

// Memory types for QuickPick
export const MEMORY_TYPES = [
  { label: 'Decision', value: 'decision', description: 'Architecture or design decision' },
  { label: 'Learning', value: 'learning', description: 'Knowledge learned or discovered' },
  { label: 'Pattern', value: 'pattern', description: 'Code pattern or best practice' },
  { label: 'Bug Fix', value: 'bugfix', description: 'Bug fix with root cause' },
  { label: 'Context', value: 'context', description: 'General context or background' },
  { label: 'Preference', value: 'preference', description: 'User or project preference' },
] as const;
