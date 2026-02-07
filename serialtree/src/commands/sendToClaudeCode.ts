import * as vscode from 'vscode';
import type { ClaudeCodeBridge } from '../services/ClaudeCodeBridge';

export async function sendToClaudeCode(bridge: ClaudeCodeBridge): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    vscode.window.showWarningMessage('No active editor');
    return;
  }

  const selection = editor.selection;
  const text = editor.document.getText(selection);

  if (!text.trim()) {
    vscode.window.showWarningMessage('No text selected');
    return;
  }

  const fileName = editor.document.fileName;
  const lineNumber = selection.start.line + 1;

  await bridge.sendSelectedText(text, fileName, lineNumber);
}
