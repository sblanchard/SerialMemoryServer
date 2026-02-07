import * as vscode from 'vscode';
import type { CodeAnalyzer } from '../services/CodeAnalyzer';
import type { FindingsProvider } from '../providers/FindingsProvider';

export async function analyzeWorkspace(
  analyzer: CodeAnalyzer,
  findingsProvider: FindingsProvider,
): Promise<void> {
  const result = await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: 'SerialTree: Analyzing workspace...',
      cancellable: false,
    },
    async () => {
      return analyzer.analyzeWorkspace();
    },
  );

  findingsProvider.setFindings(result.findings);

  // Ingest high-severity findings into SerialMemory
  await analyzer.ingestFindings(result.findings);

  const critical = result.findings.filter(f => f.severity === 'critical').length;
  const high = result.findings.filter(f => f.severity === 'high').length;

  vscode.window.showInformationMessage(
    `SerialTree: Found ${result.findings.length} issues in ${result.filesScanned} files (${critical} critical, ${high} high) — ${result.duration}ms`,
  );
}
