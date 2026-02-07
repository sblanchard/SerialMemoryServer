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
    const timestamp = (raw.timestamp as string) ?? new Date().toISOString();
    const content = (raw.content as string) ?? (raw.message as string) ?? JSON.stringify(raw);

    let type: SessionEntry['type'] = 'unknown';
    const rawType = (raw.type as string) ?? '';

    if (rawType.includes('edit') || rawType.includes('file')) {
      type = 'file_edit';
    } else if (rawType.includes('command') || rawType.includes('tool')) {
      type = 'command';
    } else if (rawType.includes('error')) {
      type = 'error';
    } else if (rawType.includes('finding')) {
      type = 'finding';
    } else if (rawType.includes('memory')) {
      type = 'memory';
    }

    return {
      timestamp,
      type,
      content,
      file: raw.file as string | undefined,
      detail: raw.detail as string | undefined,
    };
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
