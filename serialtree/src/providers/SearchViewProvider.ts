import * as vscode from 'vscode';
import type { McpClient } from '../services/McpClient';
import type { ApiClient } from '../services/ApiClient';
import type { SearchResult } from '../types/memory';
import { EXTENSION_ID, CONFIG } from '../constants';

export class SearchViewProvider implements vscode.WebviewViewProvider {
  public static readonly viewType = `${EXTENSION_ID}.searchView`;
  private view?: vscode.WebviewView;

  constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly mcpClient: McpClient,
    private readonly apiClient: ApiClient,
  ) {}

  resolveWebviewView(
    webviewView: vscode.WebviewView,
    _context: vscode.WebviewViewResolveContext,
    _token: vscode.CancellationToken,
  ): void {
    this.view = webviewView;

    webviewView.webview.options = {
      enableScripts: true,
      localResourceRoots: [
        vscode.Uri.joinPath(this.extensionUri, 'dist', 'webview'),
      ],
    };

    webviewView.webview.html = this.getHtml(webviewView.webview);

    webviewView.webview.onDidReceiveMessage(async (message: WebviewMessage) => {
      switch (message.type) {
        case 'search':
          await this.handleSearch(message.query, message.mode);
          break;
        case 'loadRecent':
          await this.handleLoadRecent();
          break;
        case 'openMemory':
          await this.handleOpenMemory(message.memory);
          break;
      }
    });

    // Load recent on open
    this.handleLoadRecent();
  }

  private async handleSearch(query: string, mode: 'semantic' | 'text' | 'hybrid'): Promise<void> {
    this.postMessage({ type: 'loading', value: true });

    try {
      const connectionMode = vscode.workspace.getConfiguration(EXTENSION_ID).get<string>(CONFIG.CONNECTION_MODE);
      let results: SearchResult[];

      if (connectionMode === 'api') {
        results = await this.apiClient.searchMemories(query, mode);
      } else {
        results = await this.mcpClient.search(query, mode);
      }

      this.postMessage({ type: 'results', results, title: `Search: "${query}"` });
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Search failed';
      this.postMessage({ type: 'error', message });
    } finally {
      this.postMessage({ type: 'loading', value: false });
    }
  }

  private async handleLoadRecent(): Promise<void> {
    try {
      const connectionMode = vscode.workspace.getConfiguration(EXTENSION_ID).get<string>(CONFIG.CONNECTION_MODE);
      let results: SearchResult[];

      if (connectionMode === 'api') {
        results = await this.apiClient.fetchRecentMemories();
      } else {
        results = await this.mcpClient.search('', 'hybrid', undefined, 20, 0.0);
      }

      this.postMessage({ type: 'results', results, title: 'Recent Memories' });
    } catch {
      // Silent fail for initial load
    }
  }

  private async handleOpenMemory(memory: MemoryPayload): Promise<void> {
    if (!memory) {
      return;
    }

    const entities = (memory.entities ?? []).map((e: { name?: string; type?: string } | string) =>
      typeof e === 'string' ? e : `${e.name} (${e.type ?? 'unknown'})`,
    );

    const lines = [
      `# Memory: ${memory.id}`,
      '',
      `**Created:** ${memory.createdAt}`,
    ];

    if (memory.memoryType) {
      lines.push(`**Type:** ${memory.memoryType}`);
    }

    if (memory.source) {
      lines.push(`**Source:** ${memory.source}`);
    }

    if (memory.similarity !== undefined && memory.similarity > 0) {
      lines.push(`**Similarity:** ${Math.round(memory.similarity * 100)}%`);
    }

    if (entities.length > 0) {
      lines.push(`**Entities:** ${entities.join(', ')}`);
    }

    lines.push('', '---', '', memory.content);

    const doc = await vscode.workspace.openTextDocument({
      content: lines.join('\n'),
      language: 'markdown',
    });

    await vscode.window.showTextDocument(doc, { preview: true });
  }

  private postMessage(message: Record<string, unknown>): void {
    this.view?.webview.postMessage(message);
  }

  showPanel(): void {
    if (this.view) {
      this.view.show(true);
    }
  }

  private getHtml(webview: vscode.Webview): string {
    const scriptUri = webview.asWebviewUri(
      vscode.Uri.joinPath(this.extensionUri, 'dist', 'webview', 'search.js'),
    );

    const nonce = getNonce();

    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-${nonce}'; style-src 'unsafe-inline';">
  <title>SerialTree Search</title>
  <style>
    body { margin: 0; padding: 8px; font-family: var(--vscode-font-family); color: var(--vscode-foreground); background: var(--vscode-editor-background); }
    * { box-sizing: border-box; }
  </style>
</head>
<body>
  <div id="root"></div>
  <script nonce="${nonce}" src="${scriptUri}"></script>
</body>
</html>`;
  }
}

interface MemoryPayload {
  readonly id: string;
  readonly content: string;
  readonly createdAt: string;
  readonly similarity?: number;
  readonly entities?: ReadonlyArray<{ name?: string; type?: string } | string>;
  readonly memoryType?: string;
  readonly source?: string;
}

interface WebviewMessage {
  type: string;
  query: string;
  mode: 'semantic' | 'text' | 'hybrid';
  memory: MemoryPayload;
}

function getNonce(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let result = '';
  for (let i = 0; i < 32; i++) {
    result += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return result;
}
