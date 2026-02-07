import type { AgentRole } from '../types/agent';

interface PromptParams {
  readonly role: AgentRole;
  readonly runId: string;
  readonly workspacePath: string;
  readonly scope: readonly string[];
  readonly customPrompt?: string;
  readonly maxModules?: number;
  readonly parentModuleContext?: string;
  readonly parentModuleName?: string;
}

const ROLE_INSTRUCTIONS: Record<Exclude<AgentRole, 'custom' | 'explorer' | 'explorer-sub'>, string> = {
  security: `## Your Task
You are a **security reviewer**. Analyze the codebase for vulnerabilities including:
- Authentication and authorization flaws
- Injection vulnerabilities (SQL, XSS, command injection)
- Hardcoded secrets, API keys, or credentials
- OWASP Top 10 vulnerabilities
- Insecure cryptographic practices
- Missing input validation at system boundaries
- CSRF, SSRF, and path traversal risks
- Unsafe deserialization

Prioritize CRITICAL and HIGH severity findings. Include the exact file path and line number for each finding.`,

  performance: `## Your Task
You are a **performance analyst**. Analyze the codebase for bottlenecks including:
- N+1 query patterns (await/async calls inside loops)
- Excessive memory allocations or large object copies
- Missing caching opportunities
- Inefficient string concatenation in loops
- Unindexed database queries
- Blocking I/O on hot paths
- Unnecessary re-renders or recomputation
- Large bundle or payload sizes

Estimate the impact of each finding (latency, memory, throughput). Include file path and line number.`,

  patterns: `## Your Task
You are a **pattern scanner**. Analyze the codebase for code quality issues including:
- Dead code and unused exports
- Code duplication (DRY violations)
- Overly complex functions (high cyclomatic complexity)
- Deep nesting (>4 levels)
- Missing error handling
- Inconsistent naming conventions
- God classes or files exceeding 800 lines
- Tight coupling between modules
- Missing abstractions or premature abstractions

Suggest specific refactoring approaches for each finding. Include file path and line number.`,

  reviewer: `## Your Task
You are a **code reviewer**. Analyze the codebase for bugs and logic errors including:
- Off-by-one errors and boundary conditions
- Null/undefined reference risks
- Unhandled promise rejections
- Race conditions in async code
- Incorrect type assertions or unsafe casts
- Missing edge case handling
- Logic errors in conditionals
- Resource leaks (unclosed connections, streams, file handles)

Provide reproduction hints or scenarios where each bug manifests. Include file path and line number.`,
};

function buildExplorerInstructions(maxModules: number): string {
  return `## Your Task
YOU ARE AN ORCHESTRATOR — MAP the codebase, do NOT fix or modify anything.

Your goal is to identify the top ~${maxModules} cohesive modules (bounded contexts) in this codebase and record structured knowledge for each.

### Step 1: Explore
- Read the top-level directory layout (ls, tree, or glob)
- Examine package files (package.json, *.csproj, Cargo.toml, go.mod, etc.)
- Identify entry points (main, index, Program.cs, app, etc.)
- Scan for READMEs, CLAUDE.md, or architecture docs
- Note the tech stack, frameworks, and build system

### Step 2: Identify Modules
Identify up to ${maxModules} cohesive modules. A module is:
- A directory or set of files that form a bounded context
- Has a clear purpose and responsibility
- Examples: "Authentication", "API Layer", "Database Access", "UI Components", "Background Workers"

### Step 3: Record Each Module
For EACH module, ingest ONE structured memory with this format:

# Module: <MODULE_NAME>
Purpose: <1-2 sentence description of what this module does>
Key Files: <comma-separated list of most important files>
Entry Points: <main entry files or public API surface>
Dependencies: <other modules or external packages this depends on>
Dependents: <modules that depend on this one>
Boundaries: <how this module interfaces with the rest of the system>
Gotchas: <non-obvious design decisions, quirks, or complexity>

## Constraints
- Maximum ${maxModules} modules — group small related files into logical units
- ONE memory per module — be comprehensive but concise
- Do NOT create, modify, or delete any files
- Do NOT run tests, builds, or any destructive commands
- ONLY read, explore, and record via SerialMemory`;
}

function buildExplorerSubInstructions(parentModuleName: string, parentModuleContext: string): string {
  return `## Your Task
You are decomposing the **${parentModuleName}** module into its sub-components.

### Parent Module Context
${parentModuleContext}

### Instructions
1. Read and explore the files described in the parent module context above
2. Identify the key sub-components (classes, services, workflows, data models)
3. For each sub-component, ingest ONE structured memory with this format:

# SubComponent: <COMPONENT_NAME> (parent: ${parentModuleName})
Purpose: <what this component does within the module>
Type: <class | service | workflow | data-model | utility | config>
Key Files: <files that implement this component>
Public API: <exported functions, methods, or interfaces>
Internal Dependencies: <other components within this module it uses>
External Dependencies: <packages or other modules it depends on>
Data Flow: <how data enters and exits this component>

### Constraints
- Break down into meaningful sub-components (3-8 typically)
- ONE memory per sub-component
- depth_level: 1 — Do NOT spawn further agents or decompose deeper
- Do NOT create, modify, or delete any files
- Do NOT run tests, builds, or any destructive commands
- ONLY read, explore, and record via SerialMemory`;
}

const COMMUNICATION_PROTOCOL = `## Communication Protocol
You MUST use SerialMemory MCP tools for ALL communication:

1. FIRST: Search for existing context from other agents in this run:
   Use mcp__serialmemory-memory__memory_search with query="run:{runId}" mode="text"

2. Perform your analysis on the codebase.

3. Store EACH finding using mcp__serialmemory-memory__memory_ingest with:
   - content: Your structured finding (see Output Format below)
   - source: "serialtree-agent-{role}"
   - metadata: { "run_id": "{runId}", "agent_role": "{role}", "severity": "<severity>", "file": "<filepath>" }

4. When done, ingest a completion marker using mcp__serialmemory-memory__memory_ingest with:
   - content: "AGENT_COMPLETE: {role} - <count> findings. Summary: <brief summary>"
   - source: "serialtree-agent-{role}"
   - metadata: { "run_id": "{runId}", "agent_role": "{role}", "status": "complete" }

## Output Format Per Finding
[<SEVERITY>] <title>
File: <file>:<line>
Description: <description>
Suggestion: <suggestion>

## Rules
- Do NOT create or modify any files
- Do NOT use the Edit, Write, or Bash tools for file changes
- ONLY analyze and report via SerialMemory
- Be thorough but concise - max 200 words per finding
- Focus on HIGH and CRITICAL severity items first
- Prefix severity with brackets: [CRITICAL], [HIGH], [MEDIUM], [LOW]`;

export function buildAgentPrompt(params: PromptParams): string {
  const { role, runId, workspacePath, scope, customPrompt, maxModules, parentModuleContext, parentModuleName } = params;
  const scopeGlobs = scope.join(', ');

  const roleSection = buildRoleSection(role, customPrompt, maxModules, parentModuleName, parentModuleContext);

  const agentSource = role === 'explorer' || role === 'explorer-sub'
    ? 'explorer'
    : role;

  const protocol = COMMUNICATION_PROTOCOL
    .replace(/\{runId\}/g, runId)
    .replace(/\{role\}/g, agentSource);

  return `You are a ${role} agent analyzing the codebase at ${workspacePath}.
Run ID: ${runId}

${roleSection}

## Scope
Analyze files matching: ${scopeGlobs}

${protocol}`;
}

function buildRoleSection(
  role: AgentRole,
  customPrompt?: string,
  maxModules?: number,
  parentModuleName?: string,
  parentModuleContext?: string,
): string {
  if (role === 'custom') {
    return `## Your Task\n${customPrompt ?? 'Analyze the codebase and report findings.'}`;
  }

  if (role === 'explorer') {
    return buildExplorerInstructions(maxModules ?? 7);
  }

  if (role === 'explorer-sub') {
    return buildExplorerSubInstructions(
      parentModuleName ?? 'Unknown Module',
      parentModuleContext ?? 'No parent context available.',
    );
  }

  return ROLE_INSTRUCTIONS[role];
}

export function escapePromptForShell(prompt: string): string {
  return prompt
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"')
    .replace(/\$/g, '\\$')
    .replace(/`/g, '\\`')
    .replace(/\n/g, '\\n')
    .replace(/\r/g, '\\r');
}
