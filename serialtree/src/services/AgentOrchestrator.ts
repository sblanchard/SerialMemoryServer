import * as vscode from 'vscode';
import * as crypto from 'crypto';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import type { McpClient } from './McpClient';
import type { ApiClient } from './ApiClient';
import type {
  AgentRole, AgentFinding, OrchestrateOptions, RunResult, SpawnedAgent,
  ExplorerOptions, ExplorerRunResult, ExplorerParams, ModuleDescriptor,
} from '../types/agent';
import { AGENT_CONFIGS } from '../types/agent';
import { buildAgentPrompt } from '../prompts/agentPrompts';
import { EXTENSION_ID, CONFIG, DEFAULTS } from '../constants';

const MEMORY_SEARCH_THRESHOLD = 0.3;
const MAX_TITLE_PREVIEW = 80;
const MAX_TITLE_LENGTH = 120;
const MAX_DESCRIPTION_LENGTH = 300;

interface ActiveRun {
  readonly runId: string;
  readonly agents: readonly SpawnedAgent[];
  readonly completedRoles: ReadonlySet<string>;
  readonly terminals: ReadonlyMap<string, vscode.Terminal>;
  readonly promptFiles: readonly string[];
  readonly cancelled: boolean;
}

export class AgentOrchestrator {
  private activeRun: ActiveRun | null = null;
  private readonly terminalCloseListener: vscode.Disposable;

  constructor(
    private readonly mcpClient: McpClient,
    private readonly apiClient: ApiClient,
    private readonly outputChannel: vscode.OutputChannel,
  ) {
    this.terminalCloseListener = vscode.window.onDidCloseTerminal((terminal) => {
      this.handleTerminalClose(terminal);
    });
  }

  async orchestrateAnalysis(
    roles: readonly AgentRole[],
    scope: readonly string[],
    options?: OrchestrateOptions,
  ): Promise<RunResult> {
    this.validateInputs(roles, scope, options);

    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const poolSize = config.get<number>(CONFIG.AGENT_POOL_SIZE) ?? DEFAULTS.AGENT_POOL_SIZE;
    const timeout = options?.timeout ?? config.get<number>(CONFIG.AGENT_TIMEOUT) ?? DEFAULTS.AGENT_TIMEOUT;
    const pollInterval = options?.pollInterval ?? DEFAULTS.AGENT_POLL_INTERVAL;
    const workspacePath = this.getWorkspacePath();

    const runId = generateRunId();
    const startedAt = Date.now();

    this.outputChannel.appendLine(`[Orchestrator] Starting run ${runId} with roles: ${roles.join(', ')}`);

    const rolesToSpawn = roles.slice(0, poolSize);
    const spawnResults = await Promise.all(
      rolesToSpawn.map((role) =>
        this.spawnAgent(role, runId, workspacePath, scope, options?.customPrompt),
      ),
    );

    const terminals = new Map<string, vscode.Terminal>();
    const promptFiles: string[] = [];
    for (const result of spawnResults) {
      const terminal = this.findTerminalByName(result.agent.terminalName);
      if (terminal) {
        terminals.set(result.agent.role, terminal);
      }
      promptFiles.push(result.promptFile);
    }

    this.activeRun = {
      runId,
      agents: spawnResults.map((r) => r.agent),
      completedRoles: new Set(),
      terminals,
      promptFiles,
      cancelled: false,
    };

    try {
      await this.waitForAgents(runId, spawnResults.length, timeout, pollInterval);
    } catch (err) {
      this.outputChannel.appendLine(
        `[Orchestrator] Wait interrupted: ${err instanceof Error ? err.message : String(err)}`,
      );
    }

    const completedCount = this.activeRun?.completedRoles.size ?? 0;
    const agents = this.activeRun?.agents ?? [];

    // Determine which agents completed by checking if their ID or role appears in completedRoles
    const completedAgentIds: string[] = [];
    const timedOutAgentIds: string[] = [];
    for (const agent of agents) {
      const isCompleted = this.activeRun?.completedRoles.has(agent.id) ?? false;
      if (isCompleted || completedCount >= rolesToSpawn.length) {
        completedAgentIds.push(agent.role);
      } else {
        timedOutAgentIds.push(agent.role);
      }
    }

    if (timedOutAgentIds.length > 0) {
      this.outputChannel.appendLine(`[Orchestrator] Timed out agents: ${timedOutAgentIds.join(', ')}`);
    }

    const findings = await this.synthesizeResults(runId);
    const duration = Date.now() - startedAt;

    this.outputChannel.appendLine(
      `[Orchestrator] Run ${runId} complete: ${findings.length} findings in ${duration}ms`,
    );

    // Launch interactive Claude session with findings
    if (findings.length > 0) {
      await this.launchFindingsSession(runId, findings, rolesToSpawn);
    }

    await this.cleanupRun();

    return {
      runId,
      roles: rolesToSpawn,
      findings,
      completedAgents: completedAgentIds,
      timedOutAgents: timedOutAgentIds,
      duration,
    };
  }

  cancelRun(): void {
    if (!this.activeRun) {
      return;
    }

    this.activeRun = { ...this.activeRun, cancelled: true };
    this.outputChannel.appendLine(`[Orchestrator] Run ${this.activeRun.runId} cancelled by user`);

    for (const terminal of this.activeRun.terminals.values()) {
      terminal.dispose();
    }

    this.cleanupRun();
  }

  async orchestrateExploration(
    scope: readonly string[],
    options?: ExplorerOptions,
  ): Promise<ExplorerRunResult> {
    if (scope.length === 0) {
      throw new Error('Analysis scope cannot be empty');
    }

    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const poolSize = config.get<number>(CONFIG.AGENT_POOL_SIZE) ?? DEFAULTS.AGENT_POOL_SIZE;
    const maxModules = options?.maxModules
      ?? config.get<number>(CONFIG.EXPLORER_MAX_MODULES)
      ?? DEFAULTS.EXPLORER_MAX_MODULES;
    const explorerTimeout = options?.timeout
      ?? config.get<number>(CONFIG.EXPLORER_TIMEOUT)
      ?? DEFAULTS.EXPLORER_TIMEOUT;
    const subAgentTimeout = options?.subAgentTimeout ?? DEFAULTS.EXPLORER_SUB_TIMEOUT;
    const pollInterval = options?.pollInterval ?? DEFAULTS.AGENT_POLL_INTERVAL;
    const workspacePath = this.getWorkspacePath();
    const decompose = options?.decompose ?? false;

    const runId = generateRunId();
    const startedAt = Date.now();

    this.outputChannel.appendLine(
      `[Orchestrator] Starting exploration ${runId} (maxModules: ${maxModules}, decompose: ${decompose})`,
    );

    // Phase 1: Spawn single explorer agent
    const explorerResult = await this.spawnAgent(
      'explorer', runId, workspacePath, scope, undefined,
      { maxModules },
    );

    const terminals = new Map<string, vscode.Terminal>();
    const terminal = this.findTerminalByName(explorerResult.agent.terminalName);
    if (terminal) {
      terminals.set('explorer', terminal);
    }

    this.activeRun = {
      runId,
      agents: [explorerResult.agent],
      completedRoles: new Set(),
      terminals,
      promptFiles: [explorerResult.promptFile],
      cancelled: false,
    };

    await this.waitForAgents(runId, 1, explorerTimeout, pollInterval);

    // Phase 1.5: Read module memories
    const modules = await this.readModuleMemories(runId);
    this.outputChannel.appendLine(
      `[Orchestrator] Explorer identified ${modules.length} modules`,
    );

    // Phase 2: Spawn sub-agents if decompose is enabled
    const completedSubAgents: string[] = [];
    const timedOutSubAgents: string[] = [];

    if (decompose && modules.length > 0 && !this.activeRun?.cancelled) {
      this.outputChannel.appendLine(
        `[Orchestrator] Phase 2: Spawning sub-agents for ${modules.length} modules (pool: ${poolSize})`,
      );

      // Batch modules by pool size
      for (let i = 0; i < modules.length; i += poolSize) {
        if (this.activeRun?.cancelled) {
          break;
        }

        const batchIndex = Math.floor(i / poolSize);
        const batch = modules.slice(i, i + poolSize);
        const subRunId = `${runId}-sub-${batchIndex}`;

        const subSpawnResults = await Promise.all(
          batch.map((mod) =>
            this.spawnAgent('explorer-sub', subRunId, workspacePath, scope, undefined, {
              parentModuleName: mod.name,
              parentModuleContext: mod.memoryContent,
            }),
          ),
        );

        const currentRun = this.activeRun!;
        const subTerminals: Map<string, vscode.Terminal> = new Map(currentRun.terminals);
        const subPromptFiles: string[] = [...currentRun.promptFiles];
        const subAgents: SpawnedAgent[] = [...currentRun.agents];

        for (const result of subSpawnResults) {
          const subTerminal = this.findTerminalByName(result.agent.terminalName);
          if (subTerminal) {
            subTerminals.set(result.agent.id, subTerminal);
          }
          subPromptFiles.push(result.promptFile);
          subAgents.push(result.agent);
        }

        this.activeRun = {
          ...this.activeRun!,
          agents: subAgents,
          terminals: subTerminals,
          promptFiles: subPromptFiles,
          completedRoles: new Set(),
        };

        try {
          await this.waitForAgents(subRunId, batch.length, subAgentTimeout, pollInterval);
        } catch {
          // Some agents may have timed out
        }

        // Track completion by terminal close — each closed terminal
        // adds its agent role to completedRoles via handleTerminalClose
        const completedInBatch = this.activeRun?.completedRoles.size ?? 0;
        for (let j = 0; j < batch.length; j++) {
          if (j < completedInBatch) {
            completedSubAgents.push(batch[j].name);
          } else {
            timedOutSubAgents.push(batch[j].name);
          }
        }
      }
    }

    const duration = Date.now() - startedAt;

    this.outputChannel.appendLine(
      `[Orchestrator] Exploration ${runId} complete: ${modules.length} modules in ${duration}ms`,
    );

    await this.cleanupRun();

    return {
      runId,
      modules,
      subAgentResults: completedSubAgents,
      completedSubAgents,
      timedOutSubAgents,
      duration,
    };
  }

  private async readModuleMemories(runId: string): Promise<readonly ModuleDescriptor[]> {
    try {
      const results = await this.searchRunMemories(runId);
      const modules: ModuleDescriptor[] = [];

      for (const result of results) {
        if (result.content.startsWith('AGENT_COMPLETE')) {
          continue;
        }

        const moduleMatch = result.content.match(/^#\s*Module:\s*(.+?)$/m);
        if (moduleMatch) {
          modules.push({
            name: moduleMatch[1].trim(),
            memoryContent: result.content,
          });
        }
      }

      return modules;
    } catch (err) {
      this.outputChannel.appendLine(
        `[Orchestrator] Failed to read module memories: ${err instanceof Error ? err.message : String(err)}`,
      );
      return [];
    }
  }

  private validateInputs(
    roles: readonly AgentRole[],
    scope: readonly string[],
    options?: OrchestrateOptions,
  ): void {
    if (roles.length === 0) {
      throw new Error('At least one agent role is required');
    }

    if (scope.length === 0) {
      throw new Error('Analysis scope cannot be empty');
    }

    if (options?.timeout !== undefined && options.timeout < 1000) {
      throw new Error('Timeout must be at least 1000ms');
    }

    if (options?.pollInterval !== undefined && options.pollInterval < 1000) {
      throw new Error('Poll interval must be at least 1000ms');
    }
  }

  private getWorkspacePath(): string {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
      throw new Error('No workspace folder is open. Open a folder to analyze.');
    }
    return folder.uri.fsPath;
  }

  private async spawnAgent(
    role: AgentRole,
    runId: string,
    workspacePath: string,
    scope: readonly string[],
    customPrompt?: string,
    explorerParams?: ExplorerParams,
  ): Promise<{ readonly agent: SpawnedAgent; readonly promptFile: string }> {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const claudeCommand = config.get<string>(CONFIG.CLAUDE_COMMAND) ?? DEFAULTS.CLAUDE_COMMAND;
    const agentConfig = AGENT_CONFIGS[role];

    const prompt = buildAgentPrompt({
      role,
      runId,
      workspacePath,
      scope,
      customPrompt,
      maxModules: explorerParams?.maxModules,
      parentModuleContext: explorerParams?.parentModuleContext,
      parentModuleName: explorerParams?.parentModuleName,
    });

    const fileLabel = explorerParams?.parentModuleName
      ? `${role}-${sanitizeFileName(explorerParams.parentModuleName)}`
      : role;
    const promptFile = await writePromptFile(runId, fileLabel, prompt);

    const terminalSuffix = explorerParams?.parentModuleName
      ? ` (${explorerParams.parentModuleName})`
      : '';
    const terminalName = `SerialTree Agent: ${agentConfig.label}${terminalSuffix}`;
    const terminal = vscode.window.createTerminal({
      name: terminalName,
      cwd: workspacePath,
      iconPath: new vscode.ThemeIcon(agentConfig.icon),
      isTransient: true,
    });

    terminal.show(true);
    // Interactive mode — full TUI with real-time tool usage visible.
    // Pass prompt as positional argument via $(cat) for interactive session.
    terminal.sendText(`${claudeCommand} --dangerously-skip-permissions "$(cat '${promptFile}')"`);

    this.outputChannel.appendLine(`[Orchestrator] Spawned agent: ${role} (terminal: ${terminalName})`);

    return {
      agent: { id: `${runId}-${role}`, role, terminalName, startedAt: Date.now() },
      promptFile,
    };
  }

  private async waitForAgents(
    runId: string,
    expectedCount: number,
    timeout: number,
    pollInterval: number,
  ): Promise<void> {
    const deadline = Date.now() + timeout;

    while (Date.now() < deadline) {
      if (this.activeRun?.cancelled) {
        return;
      }

      const completedCount = await this.pollCompletion(runId);

      if (completedCount >= expectedCount) {
        this.outputChannel.appendLine(`[Orchestrator] All ${expectedCount} agents completed`);
        return;
      }

      this.outputChannel.appendLine(
        `[Orchestrator] ${completedCount}/${expectedCount} agents complete, polling...`,
      );

      await sleep(pollInterval);
    }

    const remaining = expectedCount - (this.activeRun?.completedRoles.size ?? 0);
    throw new Error(`Timeout: ${remaining} agents did not finish within ${timeout}ms`);
  }

  private async pollCompletion(runId: string): Promise<number> {
    try {
      const results = await this.searchRunMemories(runId, 'AGENT_COMPLETE');
      const newCompleted = new Set(this.activeRun?.completedRoles ?? []);

      for (const result of results) {
        const completeMatch = result.content.match(/AGENT_COMPLETE:\s*(.+)/);
        if (completeMatch) {
          // Use full content hash as unique key to handle multiple agents with same role
          newCompleted.add(completeMatch[1].trim());
        }
      }

      if (this.activeRun) {
        this.activeRun = { ...this.activeRun, completedRoles: newCompleted };
      }

      return newCompleted.size;
    } catch (err) {
      this.outputChannel.appendLine(
        `[Orchestrator] Poll error: ${err instanceof Error ? err.message : String(err)}`,
      );
      return this.activeRun?.completedRoles.size ?? 0;
    }
  }

  private async searchRunMemories(
    runId: string,
    keyword?: string,
  ): Promise<ReadonlyArray<{ content: string; source?: string }>> {
    const query = keyword ? `run:${runId} ${keyword}` : `run:${runId}`;

    if (this.mcpClient.isConnected) {
      return this.mcpClient.search(query, 'text', undefined, 50, MEMORY_SEARCH_THRESHOLD);
    }

    return this.apiClient.searchMemories(query, 'text', 50, MEMORY_SEARCH_THRESHOLD);
  }

  private async synthesizeResults(runId: string): Promise<readonly AgentFinding[]> {
    try {
      const results = await this.searchRunMemories(runId);
      return results
        .filter((r) => !r.content.startsWith('AGENT_COMPLETE'))
        .map((r) => parseFinding(r.content, r.source))
        .filter((f): f is AgentFinding => f !== null);
    } catch (err) {
      this.outputChannel.appendLine(
        `[Orchestrator] Synthesis error: ${err instanceof Error ? err.message : String(err)}`,
      );
      return [];
    }
  }

  private handleTerminalClose(terminal: vscode.Terminal): void {
    if (!this.activeRun) {
      return;
    }

    const agent = this.activeRun.agents.find((a) => a.terminalName === terminal.name);
    if (agent) {
      const newCompleted = new Set([...this.activeRun.completedRoles, agent.id]);
      this.activeRun = { ...this.activeRun, completedRoles: newCompleted };
      this.outputChannel.appendLine(`[Orchestrator] Terminal closed for agent: ${agent.role} (${agent.id})`);
    }
  }

  private async launchFindingsSession(
    runId: string,
    findings: readonly AgentFinding[],
    roles: readonly AgentRole[],
  ): Promise<void> {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const claudeCommand = config.get<string>(CONFIG.CLAUDE_COMMAND) ?? DEFAULTS.CLAUDE_COMMAND;
    const workspacePath = this.getWorkspacePath();

    // Build a findings summary prompt
    const findingsText = findings.map((f, i) => {
      const fileLine = f.file ? `\nFile: ${f.file}${f.line ? `:${f.line}` : ''}` : '';
      return `${i + 1}. [${f.severity.toUpperCase()}] [${f.role}] ${f.title}${fileLine}\n   ${f.description}${f.suggestion ? `\n   Suggestion: ${f.suggestion}` : ''}`;
    }).join('\n\n');

    const prompt = `SerialTree Agent Analysis Complete (Run: ${runId})
Agents: ${roles.join(', ')}
Total Findings: ${findings.length}

${findingsText}

---
These findings were discovered by SerialTree agents analyzing this codebase.
All findings are also stored in SerialMemory under run ID "${runId}".

start implementing fixes for all problems`;

    // Write findings to a prompt file for safe shell handling
    const promptFile = await writePromptFile(runId, 'findings-session', prompt);

    const terminal = vscode.window.createTerminal({
      name: `SerialTree: Findings (${runId})`,
      cwd: workspacePath,
      iconPath: new vscode.ThemeIcon('checklist'),
    });

    terminal.show(false);
    // Launch interactive Claude session — pass prompt via $(cat) for interactive TUI mode
    terminal.sendText(`${claudeCommand} --dangerously-skip-permissions "$(cat '${promptFile}')"`);

    this.outputChannel.appendLine(
      `[Orchestrator] Launched findings session with ${findings.length} findings`,
    );
  }

  private async cleanupRun(): Promise<void> {
    if (!this.activeRun) {
      return;
    }

    for (const promptFile of this.activeRun.promptFiles) {
      await fs.unlink(promptFile).catch(() => {});
    }

    this.activeRun = null;
  }

  private findTerminalByName(name: string): vscode.Terminal | undefined {
    return vscode.window.terminals.find((t) => t.name === name);
  }

  get isRunning(): boolean {
    return this.activeRun !== null && !this.activeRun.cancelled;
  }

  get currentRunId(): string | null {
    return this.activeRun?.runId ?? null;
  }

  dispose(): void {
    this.terminalCloseListener.dispose();
    this.cleanupRun();
  }
}

function generateRunId(): string {
  const timestamp = Date.now().toString(36);
  const random = crypto.randomBytes(4).toString('hex');
  return `run-${timestamp}-${random}`;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function writePromptFile(runId: string, role: string, prompt: string): Promise<string> {
  const tempDir = path.join(os.tmpdir(), 'serialtree-prompts');
  await fs.mkdir(tempDir, { recursive: true });
  const filePath = path.join(tempDir, `${runId}-${role}.txt`);
  await fs.writeFile(filePath, prompt, 'utf-8');
  return filePath;
}

function parseFinding(content: string, source?: string): AgentFinding | null {
  const role = extractRoleFromSource(source);
  const severityMatch = content.match(/\[(CRITICAL|HIGH|MEDIUM|LOW)\]/i);
  const fileMatch = content.match(/File:\s*([^\s:]+)(?::(\d+))?/);
  const descriptionMatch = content.match(/Description:\s*(.+?)(?:\n|Suggestion:|$)/s);
  const suggestionMatch = content.match(/Suggestion:\s*(.+?)$/s);

  const severity = severityMatch
    ? (severityMatch[1].toLowerCase() as AgentFinding['severity'])
    : 'medium';

  const titleLine = content.split('\n')[0] ?? content.slice(0, MAX_TITLE_PREVIEW);
  const title = titleLine.replace(/\[(CRITICAL|HIGH|MEDIUM|LOW)\]\s*/i, '').trim();

  if (!title) {
    return null;
  }

  return {
    role,
    severity,
    title: title.slice(0, MAX_TITLE_LENGTH),
    file: fileMatch?.[1],
    line: fileMatch?.[2] ? parseInt(fileMatch[2], 10) : undefined,
    description: descriptionMatch?.[1]?.trim() ?? content.slice(0, MAX_DESCRIPTION_LENGTH),
    suggestion: suggestionMatch?.[1]?.trim(),
  };
}

function sanitizeFileName(name: string): string {
  return name.replace(/[^a-zA-Z0-9_-]/g, '_').slice(0, 50);
}

function isValidAgentRole(value: string): value is AgentRole {
  return (Object.keys(AGENT_CONFIGS) as string[]).includes(value);
}

function extractRoleFromSource(source?: string): AgentRole {
  if (!source) {
    return 'reviewer';
  }

  const match = source.match(/serialtree-agent-(\w+)/);
  const role = match?.[1];

  return role && isValidAgentRole(role) ? role : 'reviewer';
}
