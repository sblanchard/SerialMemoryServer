import * as vscode from 'vscode';
import type { McpClient } from '../services/McpClient';
import type { ApiClient } from '../services/ApiClient';
import type { AgentOrchestrator } from '../services/AgentOrchestrator';
import type { JournalWatcher } from '../services/JournalWatcher';
import type { FindingsProvider } from './FindingsProvider';
import type { ClaudeCodeBridge } from '../services/ClaudeCodeBridge';
import type { SessionEntry } from '../types/memory';
import {
  aggregateAll,
  transformActivity,
} from '../services/CanvasDataAggregator';
import type { CanvasNode, CanvasEdge } from '../services/CanvasDataAggregator';
import { EXTENSION_ID, CONFIG, DEFAULTS } from '../constants';

export class CanvasViewProvider {
  private panel: vscode.WebviewPanel | null = null;
  private readonly journalListener: vscode.Disposable | null = null;

  constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly mcpClient: McpClient,
    private readonly apiClient: ApiClient,
    private readonly agentOrchestrator: AgentOrchestrator,
    private readonly journalWatcher: JournalWatcher,
    private readonly findingsProvider: FindingsProvider,
    private readonly claudeBridge: ClaudeCodeBridge,
  ) {
    this.journalListener = {
      dispose: () => {},
    };

    this.journalWatcher.on('entry', (entry: SessionEntry) => {
      this.handleNewActivity(entry);
    });
  }

  show(): void {
    if (this.panel) {
      this.panel.reveal(vscode.ViewColumn.One);
      return;
    }

    this.panel = vscode.window.createWebviewPanel(
      `${EXTENSION_ID}.canvas`,
      'SerialTree: Spatial Canvas',
      vscode.ViewColumn.One,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [
          vscode.Uri.joinPath(this.extensionUri, 'dist', 'webview'),
        ],
      },
    );

    this.panel.webview.html = this.getHtml(this.panel.webview);

    this.panel.webview.onDidReceiveMessage(async (message) => {
      try {
        switch (message.type) {
          case 'requestData':
            await this.loadCanvasData();
            break;
          case 'search':
            await this.loadCanvasData(message.query);
            break;
          case 'openFile':
            await this.handleOpenFile(message.file, message.line);
            break;
          case 'sendToClaude':
            await this.claudeBridge.sendPrompt(message.content);
            break;
          case 'dismissFinding': {
            const findings = this.findingsProvider.getActiveFindings();
            const finding = findings.find(
              (f) =>
                `${f.file}:${f.line}:${f.title}` === message.findingId,
            );
            if (finding) {
              this.findingsProvider.dismissFinding(finding);
            }
            break;
          }
        }
      } catch (err) {
        const errorMsg =
          err instanceof Error ? err.message : 'Unknown error';
        vscode.window.showErrorMessage(`SerialTree Canvas: ${errorMsg}`);
      }
    });

    this.panel.onDidDispose(() => {
      this.panel = null;
    });

    this.sendSettings();
  }

  private async loadCanvasData(query?: string): Promise<void> {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const connectionMode = config.get<string>(CONFIG.CONNECTION_MODE);
    const maxNodes =
      config.get<number>(CONFIG.CANVAS_MAX_NODES) ??
      DEFAULTS.CANVAS_MAX_NODES;

    let graphData;
    let searchResults;

    try {
      if (connectionMode === 'api') {
        graphData = await this.apiClient.fetchGraphData(maxNodes, query);
        searchResults = query
          ? await this.apiClient.searchMemories(query)
          : await this.apiClient.fetchRecentMemories(maxNodes);
      } else {
        graphData = await this.mcpClient.exportGraph(maxNodes, query);
        searchResults = query ? await this.mcpClient.search(query) : [];
      }
    } catch (err) {
      const errorMsg =
        err instanceof Error ? err.message : 'Failed to load data';
      vscode.window.showErrorMessage(`SerialTree Canvas: ${errorMsg}`);
      return;
    }

    const codeFindings = [...this.findingsProvider.getActiveFindings()];
    const activities = [...this.journalWatcher.sessionEntries].slice(-50);

    const canvasData = aggregateAll({
      graphData,
      searchResults,
      agents: [],
      agentStatus: new Map(),
      agentFindings: [],
      codeFindings,
      modules: [],
      activities,
      maxNodes,
    });

    this.postMessage({
      type: 'canvasData',
      nodes: canvasData.nodes,
      edges: canvasData.edges,
    });
  }

  private handleNewActivity(entry: SessionEntry): void {
    if (!this.panel) {
      return;
    }

    const activityIndex = Date.now();
    const node = transformActivity(entry, activityIndex);

    this.postMessage({
      type: 'nodeAdded',
      node,
      edges: [],
    });
  }

  private async handleOpenFile(file: string, line?: number): Promise<void> {
    const uri = vscode.Uri.file(file);
    const position = new vscode.Position((line ?? 1) - 1, 0);
    await vscode.window.showTextDocument(uri, {
      selection: new vscode.Range(position, position),
    });
  }

  private sendSettings(): void {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    this.postMessage({
      type: 'settings',
      layout:
        config.get<string>(CONFIG.CANVAS_LAYOUT) ?? DEFAULTS.CANVAS_LAYOUT,
      showMinimap:
        config.get<boolean>(CONFIG.CANVAS_SHOW_MINIMAP) ??
        DEFAULTS.CANVAS_SHOW_MINIMAP,
    });
  }

  private postMessage(message: Record<string, unknown>): void {
    this.panel?.webview.postMessage(message);
  }

  private getHtml(webview: vscode.Webview): string {
    const scriptUri = webview.asWebviewUri(
      vscode.Uri.joinPath(
        this.extensionUri,
        'dist',
        'webview',
        'canvas.js',
      ),
    );

    const cssUri = webview.asWebviewUri(
      vscode.Uri.joinPath(
        this.extensionUri,
        'dist',
        'webview',
        'canvas.css',
      ),
    );

    const nonce = getNonce();

    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-${nonce}'; style-src ${webview.cspSource} 'unsafe-inline'; img-src ${webview.cspSource} data:;">
  <title>SerialTree Spatial Canvas</title>
  <link rel="stylesheet" href="${cssUri}">
  <style>
    html, body { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #0B0F1A; }
    #root { width: 100%; height: 100%; }
    /* React Flow requires pointer-events on the pane for pan/zoom */
    .react-flow__pane { cursor: grab; }
    .react-flow__pane:active { cursor: grabbing; }
  </style>
</head>
<body>
  <div id="root"></div>
  <script nonce="${nonce}" src="${scriptUri}"></script>
</body>
</html>`;
  }

  dispose(): void {
    this.panel?.dispose();
    this.journalWatcher.removeAllListeners('canvas-entry');
  }
}

function getNonce(): string {
  const chars =
    'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let result = '';
  for (let i = 0; i < 32; i++) {
    result += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return result;
}
