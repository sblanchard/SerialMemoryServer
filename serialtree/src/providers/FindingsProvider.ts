import * as vscode from 'vscode';
import type { CodeFinding } from '../types/memory';

type SeverityLabel = 'Critical' | 'High' | 'Medium' | 'Low';

const SEVERITY_ICONS: Record<SeverityLabel, string> = {
  Critical: 'error',
  High: 'warning',
  Medium: 'info',
  Low: 'circle-outline',
};

const TYPE_LABELS: Record<CodeFinding['type'], string> = {
  tech_debt: 'tech_debt',
  bug: 'bug',
  performance: 'perf',
  quick_win: 'quick_win',
};

class SeverityGroupItem extends vscode.TreeItem {
  constructor(
    public readonly severityLabel: SeverityLabel,
    public readonly findings: readonly CodeFinding[],
  ) {
    super(
      `${severityLabel} (${findings.length})`,
      findings.length > 0 ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.None,
    );
    this.iconPath = new vscode.ThemeIcon(SEVERITY_ICONS[severityLabel]);
    this.contextValue = 'severityGroup';
  }
}

class FindingItem extends vscode.TreeItem {
  constructor(public readonly finding: CodeFinding) {
    const shortFile = finding.file.split('/').pop() ?? finding.file;
    super(
      `[${TYPE_LABELS[finding.type]}] ${finding.title}`,
      vscode.TreeItemCollapsibleState.None,
    );
    this.description = `${shortFile}:${finding.line}`;
    this.tooltip = `${finding.description}\n\n${finding.suggestion ?? ''}`;
    this.contextValue = 'finding';

    this.command = {
      command: 'vscode.open',
      title: 'Open File',
      arguments: [
        vscode.Uri.file(finding.file),
        {
          selection: new vscode.Range(
            new vscode.Position(finding.line - 1, 0),
            new vscode.Position(finding.line - 1, 0),
          ),
        },
      ],
    };

    switch (finding.severity) {
      case 'critical':
        this.iconPath = new vscode.ThemeIcon('error', new vscode.ThemeColor('errorForeground'));
        break;
      case 'high':
        this.iconPath = new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
        break;
      case 'medium':
        this.iconPath = new vscode.ThemeIcon('info', new vscode.ThemeColor('notificationsInfoIcon.foreground'));
        break;
      case 'low':
        this.iconPath = new vscode.ThemeIcon('circle-outline');
        break;
    }
  }
}

export class FindingsProvider implements vscode.TreeDataProvider<SeverityGroupItem | FindingItem> {
  private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;
  private findings: CodeFinding[] = [];
  private readonly dismissed = new Set<string>();

  setFindings(findings: CodeFinding[]): void {
    this.findings = findings;
    this._onDidChangeTreeData.fire();
  }

  dismissFinding(finding: CodeFinding): void {
    const key = `${finding.file}:${finding.line}:${finding.title}`;
    this.dismissed.add(key);
    this._onDidChangeTreeData.fire();
  }

  getActiveFindings(): readonly CodeFinding[] {
    return this.findings.filter(f => {
      const key = `${f.file}:${f.line}:${f.title}`;
      return !this.dismissed.has(key);
    });
  }

  refresh(): void {
    this._onDidChangeTreeData.fire();
  }

  getTreeItem(element: SeverityGroupItem | FindingItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: SeverityGroupItem | FindingItem): (SeverityGroupItem | FindingItem)[] {
    if (!element) {
      return this.getRootGroups();
    }

    if (element instanceof SeverityGroupItem) {
      return element.findings.map(f => new FindingItem(f));
    }

    return [];
  }

  private getRootGroups(): SeverityGroupItem[] {
    const active = this.getActiveFindings();
    const groups: Record<SeverityLabel, CodeFinding[]> = {
      Critical: [],
      High: [],
      Medium: [],
      Low: [],
    };

    const severityMap: Record<string, SeverityLabel> = {
      critical: 'Critical',
      high: 'High',
      medium: 'Medium',
      low: 'Low',
    };

    for (const finding of active) {
      const label = severityMap[finding.severity];
      groups[label].push(finding);
    }

    return (Object.entries(groups) as [SeverityLabel, CodeFinding[]][])
      .filter(([, items]) => items.length > 0)
      .map(([label, items]) => new SeverityGroupItem(label, items));
  }

  dispose(): void {
    this._onDidChangeTreeData.dispose();
  }
}
