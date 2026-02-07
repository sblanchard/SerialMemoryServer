import * as vscode from 'vscode';
import { McpClient } from './services/McpClient';
import { ApiClient } from './services/ApiClient';
import { JournalWatcher } from './services/JournalWatcher';
import { ClaudeCodeBridge } from './services/ClaudeCodeBridge';
import { CodeAnalyzer } from './services/CodeAnalyzer';
import { ActivityProvider } from './providers/ActivityProvider';
import { FindingsProvider } from './providers/FindingsProvider';
import { SearchViewProvider } from './providers/SearchViewProvider';
import { GraphViewProvider } from './providers/GraphViewProvider';
import { ingestSelection } from './commands/ingestSelection';
import { analyzeWorkspace } from './commands/analyzeWorkspace';
import { sendToClaudeCode } from './commands/sendToClaudeCode';
import { summarizeSession } from './commands/summarizeSession';
import { EXTENSION_ID, CMD, VIEW, CONFIG } from './constants';
import type { CodeFinding } from './types/memory';

let outputChannel: vscode.OutputChannel;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  outputChannel = vscode.window.createOutputChannel('SerialTree');
  outputChannel.appendLine('SerialTree activating...');

  // --- Services ---
  const mcpClient = new McpClient();
  const apiClient = new ApiClient();
  const journalWatcher = new JournalWatcher();
  const claudeBridge = new ClaudeCodeBridge();
  const codeAnalyzer = new CodeAnalyzer(mcpClient);

  // Forward MCP logs to output channel
  mcpClient.on('log', (msg: string) => outputChannel.appendLine(`[MCP] ${msg}`));
  mcpClient.on('error', (err: Error) => outputChannel.appendLine(`[MCP Error] ${err.message}`));
  mcpClient.on('exit', (code: number | null) => outputChannel.appendLine(`[MCP] Process exited (${code})`));

  // --- Connect MCP ---
  const connectionMode = vscode.workspace.getConfiguration(EXTENSION_ID).get<string>(CONFIG.CONNECTION_MODE);

  if (connectionMode === 'mcp') {
    connectMcp(mcpClient);
  }

  // --- Start journal watcher ---
  journalWatcher.on('error', (err: Error) => outputChannel.appendLine(`[Journal] ${err.message}`));
  journalWatcher.start();

  // --- Tree Data Providers ---
  const activityProvider = new ActivityProvider(journalWatcher);
  const findingsProvider = new FindingsProvider();

  context.subscriptions.push(
    vscode.window.registerTreeDataProvider(VIEW.ACTIVITY, activityProvider),
    vscode.window.registerTreeDataProvider(VIEW.FINDINGS, findingsProvider),
  );

  // --- Webview Providers ---
  const searchProvider = new SearchViewProvider(context.extensionUri, mcpClient, apiClient);
  const graphProvider = new GraphViewProvider(context.extensionUri, mcpClient, apiClient);

  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider(SearchViewProvider.viewType, searchProvider),
  );

  // --- Commands ---
  context.subscriptions.push(
    vscode.commands.registerCommand(CMD.SHOW_GRAPH, () => graphProvider.show()),

    vscode.commands.registerCommand(CMD.SEARCH_MEMORY, () => searchProvider.showPanel()),

    vscode.commands.registerCommand(CMD.SHOW_ACTIVITY, () => {
      vscode.commands.executeCommand(`${VIEW.ACTIVITY}.focus`);
    }),

    vscode.commands.registerCommand(CMD.ANALYZE_WORKSPACE, () =>
      analyzeWorkspace(codeAnalyzer, findingsProvider),
    ),

    vscode.commands.registerCommand(CMD.INGEST_SELECTION, () =>
      ingestSelection(mcpClient),
    ),

    vscode.commands.registerCommand(CMD.SEND_TO_CLAUDE, () =>
      sendToClaudeCode(claudeBridge),
    ),

    vscode.commands.registerCommand(CMD.SUMMARIZE_SESSION, () =>
      summarizeSession(mcpClient, journalWatcher),
    ),

    vscode.commands.registerCommand(CMD.SHOW_FINDINGS, () => {
      vscode.commands.executeCommand(`${VIEW.FINDINGS}.focus`);
    }),

    vscode.commands.registerCommand(CMD.REFRESH_ACTIVITY, () =>
      activityProvider.refresh(),
    ),

    vscode.commands.registerCommand(CMD.REFRESH_FINDINGS, () =>
      findingsProvider.refresh(),
    ),

    vscode.commands.registerCommand(CMD.FINDING_SEND_TO_CLAUDE, (item: { finding?: CodeFinding }) => {
      if (item?.finding) {
        claudeBridge.sendFinding(item.finding);
      }
    }),

    vscode.commands.registerCommand(CMD.FINDING_OPEN_FILE, (item: { finding?: CodeFinding }) => {
      if (item?.finding) {
        const uri = vscode.Uri.file(item.finding.file);
        const position = new vscode.Position(item.finding.line - 1, 0);
        vscode.window.showTextDocument(uri, {
          selection: new vscode.Range(position, position),
        });
      }
    }),

    vscode.commands.registerCommand(CMD.FINDING_DISMISS, (item: { finding?: CodeFinding }) => {
      if (item?.finding) {
        findingsProvider.dismissFinding(item.finding);
      }
    }),
  );

  // --- Auto-analyze on open ---
  const autoAnalyze = vscode.workspace.getConfiguration(EXTENSION_ID).get<boolean>(CONFIG.AUTO_ANALYZE_ON_OPEN);
  if (autoAnalyze) {
    analyzeWorkspace(codeAnalyzer, findingsProvider);
  }

  // --- Disposables ---
  context.subscriptions.push(
    { dispose: () => mcpClient.dispose() },
    { dispose: () => journalWatcher.dispose() },
    { dispose: () => graphProvider.dispose() },
    { dispose: () => activityProvider.dispose() },
    { dispose: () => findingsProvider.dispose() },
    outputChannel,
  );

  outputChannel.appendLine('SerialTree activated');
}

export function deactivate(): void {
  // Disposables are cleaned up via context.subscriptions
}

function connectMcp(client: McpClient): void {
  client.connect().then(
    () => {
      outputChannel.appendLine('[MCP] Connected');
      vscode.window.setStatusBarMessage('SerialTree: MCP connected', 3000);
    },
    (err: Error) => {
      outputChannel.appendLine(`[MCP] Connection failed: ${err.message}`);
      vscode.window.showWarningMessage(
        `SerialTree: MCP connection failed — ${err.message}. Check serialtree.mcpProjectPath setting.`,
      );
    },
  );
}
