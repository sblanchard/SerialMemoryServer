import * as vscode from 'vscode';
import type { McpClient } from '../services/McpClient';
import { showMemoryTypeQuickPick } from '../providers/MemoryQuickPick';

export async function ingestSelection(mcpClient: McpClient): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    vscode.window.showWarningMessage('No active editor with selection');
    return;
  }

  const selection = editor.selection;
  const text = editor.document.getText(selection);

  if (!text.trim()) {
    vscode.window.showWarningMessage('No text selected');
    return;
  }

  const typeResult = await showMemoryTypeQuickPick();
  if (!typeResult) {
    return; // User cancelled
  }

  const fileName = editor.document.fileName;
  const lineNumber = selection.start.line + 1;

  try {
    const result = await mcpClient.ingest(
      text,
      `vscode:${fileName}:${lineNumber}`,
      typeResult.type,
      {
        file: fileName,
        line: lineNumber,
        vscode_source: true,
      },
    );

    vscode.window.showInformationMessage(
      `Ingested as ${typeResult.label} (${result.memoryId})`,
    );
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Ingestion failed';
    vscode.window.showErrorMessage(`SerialTree: ${message}`);
  }
}
