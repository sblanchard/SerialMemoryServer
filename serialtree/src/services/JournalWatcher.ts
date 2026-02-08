import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { EventEmitter } from 'events';
import * as vscode from 'vscode';
import type { SessionEntry } from '../types/memory';
import { EXTENSION_ID, CONFIG, DEFAULTS } from '../constants';

export class JournalWatcher extends EventEmitter {
  private watcher: fs.FSWatcher | null = null;
  private fileWatchers = new Map<string, fs.FSWatcher>();
  private filePositions = new Map<string, number>();
  private readonly entries: SessionEntry[] = [];

  get sessionEntries(): readonly SessionEntry[] {
    return this.entries;
  }

  start(): void {
    const dir = this.resolveLogDir();

    try {
      fs.mkdirSync(dir, { recursive: true });
    } catch {
      // Directory may already exist
    }

    try {
      this.watcher = fs.watch(dir, (eventType, filename) => {
        if (filename?.endsWith('.jsonl')) {
          this.watchFile(path.join(dir, filename));
        }
      });

      // Watch existing JSONL files
      const existing = fs.readdirSync(dir).filter(f => f.endsWith('.jsonl'));
      for (const file of existing) {
        this.watchFile(path.join(dir, file));
      }
    } catch (err) {
      this.emit('error', err);
    }
  }

  private resolveLogDir(): string {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const configured = config.get<string>(CONFIG.SESSION_LOG_DIR) ?? DEFAULTS.SESSION_LOG_DIR;
    return configured.replace('~', os.homedir());
  }

  private watchFile(filePath: string): void {
    if (this.fileWatchers.has(filePath)) {
      return;
    }

    // Read existing content from current position
    this.readNewLines(filePath);

    try {
      const watcher = fs.watch(filePath, () => {
        this.readNewLines(filePath);
      });

      this.fileWatchers.set(filePath, watcher);
    } catch (err) {
      this.emit('error', err);
    }
  }

  private readNewLines(filePath: string): void {
    try {
      const stat = fs.statSync(filePath);
      const position = this.filePositions.get(filePath) ?? 0;

      if (stat.size <= position) {
        return;
      }

      const fd = fs.openSync(filePath, 'r');
      const bufferSize = stat.size - position;
      const buffer = Buffer.alloc(bufferSize);
      fs.readSync(fd, buffer, 0, bufferSize, position);
      fs.closeSync(fd);

      this.filePositions.set(filePath, stat.size);

      const lines = buffer.toString('utf-8').split('\n').filter(l => l.trim());

      for (const line of lines) {
        try {
          const raw = JSON.parse(line) as Record<string, unknown>;
          const entry = this.parseEntry(raw);
          this.entries.push(entry);
          this.emit('entry', entry);
        } catch {
          // Skip malformed lines
        }
      }

      if (lines.length > 0) {
        this.emit('update', this.entries);
      }
    } catch {
      // File may have been removed
    }
  }

  private parseEntry(raw: Record<string, unknown>): SessionEntry {
    const timestamp = (raw.ts as string) ?? (raw.timestamp as string) ?? new Date().toISOString();
    const tool = (raw.tool as string) ?? '';
    const file = (raw.file as string) || undefined;

    const type = classifyTool(tool, raw);
    const content = buildContent(tool, file, raw);
    const detail = typeof raw.result === 'string' ? truncateDetail(raw.result) : undefined;

    return { timestamp, type, content, file, detail };
  }

  getGroupedEntries(): Record<SessionEntry['type'], SessionEntry[]> {
    const grouped: Record<string, SessionEntry[]> = {
      file_edit: [],
      command: [],
      error: [],
      finding: [],
      memory: [],
      unknown: [],
    };

    for (const entry of this.entries) {
      grouped[entry.type].push(entry);
    }

    return grouped as Record<SessionEntry['type'], SessionEntry[]>;
  }

  dispose(): void {
    this.watcher?.close();
    for (const [, w] of this.fileWatchers) {
      w.close();
    }
    this.fileWatchers.clear();
  }
}

const EDIT_TOOLS = new Set(['Edit', 'Write', 'NotebookEdit']);
const COMMAND_TOOLS = new Set(['Bash', 'Task', 'Glob', 'Grep', 'Read', 'WebFetch', 'WebSearch']);
const MEMORY_TOOLS = new Set(['memory_ingest', 'memory_search', 'memory_update', 'memory_delete']);

function classifyTool(tool: string, raw: Record<string, unknown>): SessionEntry['type'] {
  if (!tool) {
    const rawType = (raw.type as string) ?? '';
    if (rawType.includes('error')) { return 'error'; }
    if (rawType.includes('finding')) { return 'finding'; }
    if (rawType.includes('memory')) { return 'memory'; }
    return 'unknown';
  }

  if (EDIT_TOOLS.has(tool)) { return 'file_edit'; }
  if (COMMAND_TOOLS.has(tool)) { return 'command'; }
  if (MEMORY_TOOLS.has(tool) || tool.startsWith('memory_')) { return 'memory'; }
  return 'command';
}

function buildContent(tool: string, file: string | undefined, raw: Record<string, unknown>): string {
  if (!tool) {
    return (raw.content as string) ?? (raw.message as string) ?? JSON.stringify(raw);
  }

  const fileName = file ? path.basename(file) : '';

  if (EDIT_TOOLS.has(tool) && fileName) {
    return `${tool}: ${fileName}`;
  }

  if (tool === 'Bash') {
    const cmd = extractBashCommand(raw);
    return cmd ? `$ ${cmd}` : 'Bash command';
  }

  if (tool === 'Read' && fileName) {
    return `Read: ${fileName}`;
  }

  if (tool === 'Glob' || tool === 'Grep') {
    return `${tool}: search`;
  }

  if (fileName) {
    return `${tool}: ${fileName}`;
  }

  return tool;
}

function extractBashCommand(raw: Record<string, unknown>): string | undefined {
  if (typeof raw.command === 'string') {
    return truncateLine(raw.command, 80);
  }

  if (typeof raw.result !== 'string') {
    return undefined;
  }

  try {
    const parsed = JSON.parse(raw.result);
    if (typeof parsed.command === 'string') {
      return truncateLine(parsed.command, 80);
    }
  } catch {
    // Not JSON, ignore
  }

  return undefined;
}

function truncateLine(text: string, max: number): string {
  const line = text.split('\n')[0] ?? text;
  return line.length > max ? `${line.slice(0, max)}...` : line;
}

function truncateDetail(text: string): string | undefined {
  if (text.length <= 200) {
    return text;
  }
  return `${text.slice(0, 200)}...`;
}
