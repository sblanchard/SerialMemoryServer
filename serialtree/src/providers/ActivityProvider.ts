import * as vscode from 'vscode';
import type { JournalWatcher } from '../services/JournalWatcher';
import type { SessionEntry } from '../types/memory';

type ActivityGroupLabel = 'Files Modified' | 'Commands Run' | 'Errors' | 'Findings' | 'Memories' | 'Other';

const TYPE_TO_GROUP: Record<SessionEntry['type'], ActivityGroupLabel> = {
  file_edit: 'Files Modified',
  command: 'Commands Run',
  error: 'Errors',
  finding: 'Findings',
  memory: 'Memories',
  unknown: 'Other',
};

const GROUP_ICONS: Record<ActivityGroupLabel, string> = {
  'Files Modified': 'edit',
  'Commands Run': 'terminal',
  'Errors': 'error',
  'Findings': 'lightbulb',
  'Memories': 'database',
  'Other': 'info',
};

class ActivityGroupItem extends vscode.TreeItem {
  constructor(
    public readonly groupLabel: ActivityGroupLabel,
    public readonly entries: readonly SessionEntry[],
  ) {
    super(
      `${groupLabel} (${entries.length})`,
      entries.length > 0 ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.None,
    );
    this.iconPath = new vscode.ThemeIcon(GROUP_ICONS[groupLabel]);
    this.contextValue = 'activityGroup';
  }
}

class ActivityEntryItem extends vscode.TreeItem {
  constructor(public readonly entry: SessionEntry) {
    const label = truncate(entry.content, 60);
    super(label, vscode.TreeItemCollapsibleState.None);

    this.tooltip = entry.content;
    this.description = formatRelativeTime(entry.timestamp);
    this.contextValue = 'activityEntry';

    if (entry.file) {
      this.command = {
        command: 'vscode.open',
        title: 'Open File',
        arguments: [vscode.Uri.file(entry.file)],
      };
      this.iconPath = new vscode.ThemeIcon('file');
    }
  }
}

export class ActivityProvider implements vscode.TreeDataProvider<ActivityGroupItem | ActivityEntryItem> {
  private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  constructor(private readonly watcher: JournalWatcher) {
    watcher.on('update', () => this.refresh());
  }

  refresh(): void {
    this._onDidChangeTreeData.fire();
  }

  getTreeItem(element: ActivityGroupItem | ActivityEntryItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: ActivityGroupItem | ActivityEntryItem): (ActivityGroupItem | ActivityEntryItem)[] {
    if (!element) {
      return this.getRootGroups();
    }

    if (element instanceof ActivityGroupItem) {
      return element.entries.map(e => new ActivityEntryItem(e));
    }

    return [];
  }

  private getRootGroups(): ActivityGroupItem[] {
    const grouped = this.watcher.getGroupedEntries();
    const groups: ActivityGroupItem[] = [];

    for (const [type, label] of Object.entries(TYPE_TO_GROUP)) {
      const entries = grouped[type as SessionEntry['type']];
      if (entries.length > 0) {
        groups.push(new ActivityGroupItem(label, entries));
      }
    }

    // If nothing yet, show empty state
    if (groups.length === 0) {
      const empty = new ActivityGroupItem('Other', []);
      empty.label = 'No session activity yet';
      empty.iconPath = new vscode.ThemeIcon('info');
      return [empty];
    }

    return groups;
  }

  dispose(): void {
    this._onDidChangeTreeData.dispose();
  }
}

function truncate(text: string, maxLength: number): string {
  if (text.length <= maxLength) {
    return text;
  }
  return text.slice(0, maxLength) + '...';
}

function formatRelativeTime(timestamp: string): string {
  const date = new Date(timestamp);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMins / 60);

  if (diffMins < 1) { return 'just now'; }
  if (diffMins < 60) { return `${diffMins}m ago`; }
  if (diffHours < 24) { return `${diffHours}h ago`; }
  return date.toLocaleDateString();
}
