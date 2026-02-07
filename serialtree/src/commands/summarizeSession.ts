import * as vscode from 'vscode';
import type { McpClient } from '../services/McpClient';
import type { JournalWatcher } from '../services/JournalWatcher';

export async function summarizeSession(
  mcpClient: McpClient,
  watcher: JournalWatcher,
): Promise<void> {
  const entries = watcher.sessionEntries;

  if (entries.length === 0) {
    vscode.window.showInformationMessage('No session activity to summarize');
    return;
  }

  const grouped = watcher.getGroupedEntries();

  const summaryParts: string[] = [
    `Session Summary — ${new Date().toISOString().split('T')[0]}`,
    `Category: session_summary`,
    `---`,
    `Files modified: ${grouped.file_edit.length}`,
    `Commands run: ${grouped.command.length}`,
    `Errors encountered: ${grouped.error.length}`,
    `Findings: ${grouped.finding.length}`,
  ];

  if (grouped.error.length > 0) {
    summaryParts.push('', 'Key errors:');
    for (const err of grouped.error.slice(0, 5)) {
      summaryParts.push(`- ${err.content.slice(0, 100)}`);
    }
  }

  if (grouped.file_edit.length > 0) {
    summaryParts.push('', 'Files touched:');
    const uniqueFiles = [...new Set(grouped.file_edit.map(e => e.file).filter(Boolean))];
    for (const file of uniqueFiles.slice(0, 10)) {
      summaryParts.push(`- ${file}`);
    }
  }

  const summary = summaryParts.join('\n');

  try {
    if (!mcpClient.isConnected) {
      vscode.window.showWarningMessage('MCP client not connected. Cannot ingest summary.');
      return;
    }

    await mcpClient.ingest(summary, 'serialtree-session', 'context', {
      type: 'session_summary',
      entry_count: entries.length,
    });

    vscode.window.showInformationMessage(
      `Session summary ingested (${entries.length} entries)`,
    );
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Summarization failed';
    vscode.window.showErrorMessage(`SerialTree: ${message}`);
  }
}
