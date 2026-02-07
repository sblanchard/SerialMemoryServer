import * as vscode from 'vscode';
import type { AgentOrchestrator } from '../services/AgentOrchestrator';
import { EXTENSION_ID, CONFIG, DEFAULTS } from '../constants';

interface ModuleCountItem extends vscode.QuickPickItem {
  readonly value: number;
}

interface DepthItem extends vscode.QuickPickItem {
  readonly decompose: boolean;
}

export async function exploreCodebase(orchestrator: AgentOrchestrator): Promise<void> {
  if (orchestrator.isRunning) {
    vscode.window.showWarningMessage('SerialTree: An agent orchestration is already running.');
    return;
  }

  const config = vscode.workspace.getConfiguration(EXTENSION_ID);
  const defaultMax = config.get<number>(CONFIG.EXPLORER_MAX_MODULES) ?? DEFAULTS.EXPLORER_MAX_MODULES;

  const moduleCountItems: readonly ModuleCountItem[] = [3, 5, 7, 10, 12].map((n) => ({
    label: `${n} modules`,
    description: n === defaultMax ? '(default)' : undefined,
    value: n,
    picked: n === defaultMax,
  }));

  const selectedCount = await vscode.window.showQuickPick([...moduleCountItems], {
    title: 'SerialTree: Maximum Modules',
    placeHolder: 'How many top-level modules should the explorer identify?',
  });

  if (!selectedCount) {
    return;
  }

  const depthItems: readonly DepthItem[] = [
    {
      label: 'Explore + Decompose',
      description: 'Map modules, then spawn sub-agents to decompose each',
      decompose: true,
    },
    {
      label: 'Explore Only',
      description: 'Map top-level modules only (faster)',
      decompose: false,
    },
  ];

  const selectedDepth = await vscode.window.showQuickPick([...depthItems], {
    title: 'SerialTree: Exploration Depth',
    placeHolder: 'How deep should the exploration go?',
  });

  if (!selectedDepth) {
    return;
  }

  const scope = config.get<string[]>(CONFIG.AGENT_SCOPE) ?? DEFAULTS.AGENT_SCOPE;

  const result = await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: 'SerialTree: Exploring codebase...',
      cancellable: true,
    },
    async (progress, token) => {
      const phaseMessages = selectedDepth.decompose
        ? ['Phase 1: Explorer mapping modules...', 'Phase 2: Sub-agents decomposing modules...']
        : ['Explorer mapping modules...'];

      let phaseIndex = 0;
      progress.report({ message: phaseMessages[0] });

      const progressTimer = setInterval(() => {
        if (orchestrator.isRunning && selectedDepth.decompose && phaseIndex === 0) {
          phaseIndex = 1;
          progress.report({ message: phaseMessages[1] });
        }
      }, 15_000);

      token.onCancellationRequested(() => {
        clearInterval(progressTimer);
        orchestrator.cancelRun();
      });

      try {
        return await orchestrator.orchestrateExploration(scope, {
          maxModules: selectedCount.value,
          decompose: selectedDepth.decompose,
        });
      } finally {
        clearInterval(progressTimer);
      }
    },
  );

  if (!result) {
    return;
  }

  const moduleNames = result.modules.map((m) => m.name).join(', ');
  const subInfo = result.completedSubAgents.length > 0
    ? ` | ${result.completedSubAgents.length} decomposed`
    : '';
  const timedOutInfo = result.timedOutSubAgents.length > 0
    ? ` (${result.timedOutSubAgents.length} timed out)`
    : '';

  const action = await vscode.window.showInformationMessage(
    `SerialTree: Exploration complete — ${result.modules.length} modules identified${subInfo}${timedOutInfo} in ${formatDuration(result.duration)}`,
    'View Knowledge Graph',
    'Details',
  );

  if (action === 'View Knowledge Graph') {
    vscode.commands.executeCommand('serialtree.showGraph');
  } else if (action === 'Details') {
    const detail = [
      `Run ID: ${result.runId}`,
      `Modules: ${moduleNames || 'none'}`,
      `Duration: ${formatDuration(result.duration)}`,
    ];

    if (result.completedSubAgents.length > 0) {
      detail.push(`Decomposed: ${result.completedSubAgents.join(', ')}`);
    }

    if (result.timedOutSubAgents.length > 0) {
      detail.push(`Timed out: ${result.timedOutSubAgents.join(', ')}`);
    }

    vscode.window.showInformationMessage(detail.join('\n'));
  }
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
