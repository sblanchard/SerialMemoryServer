import * as vscode from 'vscode';
import { MEMORY_TYPES } from '../constants';
import type { MemoryType } from '../types/memory';

interface MemoryQuickPickResult {
  readonly type: MemoryType;
  readonly label: string;
}

export async function showMemoryTypeQuickPick(): Promise<MemoryQuickPickResult | undefined> {
  const items = MEMORY_TYPES.map(t => ({
    label: t.label,
    description: t.description,
    value: t.value,
  }));

  const selected = await vscode.window.showQuickPick(items, {
    placeHolder: 'Select memory type for this ingestion',
    title: 'SerialTree: Memory Type',
  });

  if (!selected) {
    return undefined;
  }

  return {
    type: selected.value as MemoryType,
    label: selected.label,
  };
}
