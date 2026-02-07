import * as vscode from 'vscode';
import type { McpClient } from '../services/McpClient';
import type { ApiClient } from '../services/ApiClient';
import { EXTENSION_ID, CONFIG } from '../constants';

export class GraphViewProvider {
  private panel: vscode.WebviewPanel | null = null;

  constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly mcpClient: McpClient,
    private readonly apiClient: ApiClient,
  ) {}

  show(): void {
    if (this.panel) {
      this.panel.reveal(vscode.ViewColumn.One);
      return;
    }

    this.panel = vscode.window.createWebviewPanel(
      `${EXTENSION_ID}.graph`,
      'SerialTree: Knowledge Graph',
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

    this.panel.webview.onDidReceiveMessage(async (message: WebviewMessage) => {
      switch (message.type) {
        case 'requestData':
          await this.loadGraphData();
          break;
        case 'search':
          await this.loadGraphData(message.query);
          break;
        case 'nodeClick':
          await this.handleNodeClick(message.nodeId ?? '', message.label ?? '', message.group ?? '');
          break;
      }
    });

    this.panel.onDidDispose(() => {
      this.panel = null;
    });
  }

  private async loadGraphData(query?: string): Promise<void> {
    try {
      const connectionMode = vscode.workspace.getConfiguration(EXTENSION_ID).get<string>(CONFIG.CONNECTION_MODE);
      let graphData;

      if (connectionMode === 'api') {
        graphData = await this.apiClient.fetchGraphData(50, query);
      } else {
        graphData = await this.mcpClient.exportGraph(50, query);
      }

      // Transform edges to links format expected by force-graph
      const links = (graphData.edges ?? []).map(e => ({
        source: e.from,
        target: e.to,
        label: e.label,
        value: 1,
      }));

      this.postMessage({
        type: 'graphData',
        nodes: graphData.nodes ?? [],
        links,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to load graph';
      vscode.window.showErrorMessage(`SerialTree Graph: ${message}`);
    }
  }

  private async handleNodeClick(nodeId: string, label: string, group: string): Promise<void> {
    const action = await vscode.window.showInformationMessage(
      `${group}: ${label}`,
      'Search Related',
      'Copy ID',
    );

    if (action === 'Search Related') {
      await this.loadGraphData(label);
    } else if (action === 'Copy ID') {
      await vscode.env.clipboard.writeText(nodeId);
      vscode.window.showInformationMessage('Node ID copied');
    }
  }

  private postMessage(message: Record<string, unknown>): void {
    this.panel?.webview.postMessage(message);
  }

  private getHtml(webview: vscode.Webview): string {
    const scriptUri = webview.asWebviewUri(
      vscode.Uri.joinPath(this.extensionUri, 'dist', 'webview', 'graph.js'),
    );

    const nonce = getNonce();

    // Note: 'unsafe-eval' required for Three.js WebGL
    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-${nonce}' 'unsafe-eval'; style-src 'unsafe-inline'; img-src ${webview.cspSource} blob: data:; connect-src blob:;">
  <title>SerialTree Knowledge Graph</title>
  <style>
    html, body { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #0B0F1A; }
    #root { width: 100%; height: 100%; }
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
  }
}

interface WebviewMessage {
  type: string;
  query?: string;
  nodeId?: string;
  label?: string;
  group?: string;
}

function getNonce(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let result = '';
  for (let i = 0; i < 32; i++) {
    result += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return result;
}
