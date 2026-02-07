import * as vscode from 'vscode';
import type { CodeFinding, SearchResult } from '../types/memory';
import { EXTENSION_ID, CONFIG, DEFAULTS } from '../constants';

export class ClaudeCodeBridge {
  findClaudeTerminal(): vscode.Terminal | undefined {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const pattern = config.get<string>(CONFIG.CLAUDE_TERMINAL_PATTERN) ?? DEFAULTS.CLAUDE_TERMINAL_PATTERN;

    try {
      const regex = new RegExp(pattern, 'i');
      return vscode.window.terminals.find(t => regex.test(t.name));
    } catch {
      // Invalid regex, fall back to includes
      return vscode.window.terminals.find(t =>
        t.name.toLowerCase().includes(pattern.toLowerCase()),
      );
    }
  }

  async sendPrompt(prompt: string): Promise<void> {
    const terminal = this.findClaudeTerminal();

    if (!terminal) {
      const action = await vscode.window.showWarningMessage(
        'No Claude Code terminal found. Start Claude Code first.',
        'Open Terminal',
      );
      if (action === 'Open Terminal') {
        vscode.commands.executeCommand('workbench.action.terminal.new');
      }
      return;
    }

    terminal.show(true);
    terminal.sendText(prompt);
  }

  async sendFinding(finding: CodeFinding): Promise<void> {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const template = config.get<string>(CONFIG.PROMPT_TEMPLATE) ?? '';

    const findingText = [
      `[${finding.type}] ${finding.title}`,
      `File: ${finding.file}:${finding.line}`,
      `Severity: ${finding.severity}`,
      finding.description,
      finding.suggestion ? `Suggestion: ${finding.suggestion}` : '',
    ].filter(Boolean).join('\n');

    const prompt = template
      ? template.replace('{finding}', findingText)
      : `Fix this ${finding.type} issue in ${finding.file}:${finding.line} — ${finding.title}. ${finding.description}${finding.suggestion ? ` Suggestion: ${finding.suggestion}` : ''}`;

    await this.sendPrompt(prompt);
  }

  async sendContextPrompt(memories: SearchResult[], instruction: string): Promise<void> {
    const contextBlock = memories
      .slice(0, 5)
      .map((m, i) => `[Memory ${i + 1}]: ${m.content}`)
      .join('\n\n');

    const prompt = `Context from SerialMemory:\n${contextBlock}\n\nInstruction: ${instruction}`;
    await this.sendPrompt(prompt);
  }

  async sendSelectedText(text: string, fileName: string, lineNumber: number): Promise<void> {
    const prompt = `Review this code from ${fileName}:${lineNumber}:\n\n\`\`\`\n${text}\n\`\`\``;
    await this.sendPrompt(prompt);
  }

  get isAvailable(): boolean {
    return this.findClaudeTerminal() !== undefined;
  }
}
