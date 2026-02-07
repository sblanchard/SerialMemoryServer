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
} as const;

// View IDs
export const VIEW = {
  ACTIVITY: `${EXTENSION_ID}.activity`,
  FINDINGS: `${EXTENSION_ID}.findings`,
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
