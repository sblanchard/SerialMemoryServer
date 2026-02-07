import * as vscode from 'vscode';
import type { AgentOrchestrator } from '../services/AgentOrchestrator';
import type { FindingsProvider } from '../providers/FindingsProvider';
import type { AgentRole, AgentFinding } from '../types/agent';
import type { CodeFinding } from '../types/memory';
import { AGENT_CONFIGS } from '../types/agent';
import { EXTENSION_ID, CONFIG, DEFAULTS } from '../constants';

interface RoleQuickPickItem extends vscode.QuickPickItem {
  readonly role: AgentRole;
}

export async function orchestrateAgents(
  orchestrator: AgentOrchestrator,
  findingsProvider: FindingsProvider,
): Promise<void> {
  if (orchestrator.isRunning) {
    vscode.window.showWarningMessage('SerialTree: An agent orchestration is already running.');
    return;
  }

  const analysisRoles: AgentRole[] = ['security', 'performance', 'patterns', 'reviewer', 'custom'];
  const roleItems: readonly RoleQuickPickItem[] = analysisRoles.map((role) => {
    const config = AGENT_CONFIGS[role];
    return {
      label: `$(${config.icon}) ${config.label}`,
      description: config.description,
      role: config.role,
      picked: config.role !== 'custom',
    };
  });

  const selected = await vscode.window.showQuickPick([...roleItems], {
    title: 'SerialTree: Select Agent Roles',
    placeHolder: 'Choose which agents to spawn (multi-select)',
    canPickMany: true,
  });

  if (!selected || selected.length === 0) {
    return;
  }

  const roles = selected.map((item) => item.role);

  const customPrompt = roles.includes('custom')
    ? await vscode.window.showInputBox({
        title: 'Custom Agent Prompt',
        prompt: 'What should the custom agent analyze?',
        placeHolder: 'e.g., Find all React components missing error boundaries',
      })
    : undefined;

  if (roles.includes('custom') && customPrompt === undefined) {
    return;
  }

  const config = vscode.workspace.getConfiguration(EXTENSION_ID);
  const scope = config.get<string[]>(CONFIG.AGENT_SCOPE) ?? DEFAULTS.AGENT_SCOPE;

  const result = await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: `SerialTree: Running ${roles.length} agent(s)...`,
      cancellable: true,
    },
    async (progress, token) => {
      const updateProgress = setInterval(() => {
        if (orchestrator.isRunning) {
          progress.report({ message: 'Agents analyzing codebase...' });
        }
      }, 5000);

      token.onCancellationRequested(() => {
        clearInterval(updateProgress);
        orchestrator.cancelRun();
      });

      try {
        return await orchestrator.orchestrateAnalysis(roles, scope, { customPrompt });
      } finally {
        clearInterval(updateProgress);
      }
    },
  );

  if (!result) {
    return;
  }

  const codeFindings = result.findings.map(agentFindingToCodeFinding);
  findingsProvider.setFindings(codeFindings);

  const critical = result.findings.filter((f) => f.severity === 'critical').length;
  const high = result.findings.filter((f) => f.severity === 'high').length;
  const timedOut = result.timedOutAgents.length > 0
    ? ` (${result.timedOutAgents.length} timed out)`
    : '';

  vscode.window.showInformationMessage(
    `SerialTree: ${result.completedAgents.length}/${result.roles.length} agents complete${timedOut} — ` +
    `${result.findings.length} findings (${critical} critical, ${high} high) in ${formatDuration(result.duration)}`,
  );
}

function agentFindingToCodeFinding(finding: AgentFinding): CodeFinding {
  const typeMap: Record<AgentRole, CodeFinding['type']> = {
    security: 'bug',
    performance: 'performance',
    patterns: 'tech_debt',
    reviewer: 'bug',
    custom: 'tech_debt',
    explorer: 'tech_debt',
    'explorer-sub': 'tech_debt',
  };

  return {
    type: typeMap[finding.role],
    severity: finding.severity,
    file: finding.file ?? 'unknown',
    line: finding.line ?? 1,
    title: `[${finding.role}] ${finding.title}`,
    description: finding.description,
    suggestion: finding.suggestion,
  };
}

function formatDuration(ms: number): string {
  if (ms < 1000) {
    return `${ms}ms`;
  }
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) {
    return `${seconds}s`;
  }
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  return `${minutes}m ${remainingSeconds}s`;
}
