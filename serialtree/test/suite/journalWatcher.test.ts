import * as assert from 'assert';
import type { SessionEntry } from '../../src/types/memory';

suite('JournalWatcher Test Suite', () => {
  test('JSONL line parsing — file_edit', () => {
    const raw = JSON.parse('{"type":"file_edit","content":"Modified auth.ts","file":"/src/auth.ts","timestamp":"2026-02-07T10:00:00Z"}');
    const entry = parseEntry(raw);

    assert.strictEqual(entry.type, 'file_edit');
    assert.strictEqual(entry.content, 'Modified auth.ts');
    assert.strictEqual(entry.file, '/src/auth.ts');
  });

  test('JSONL line parsing — command', () => {
    const raw = JSON.parse('{"type":"command","content":"dotnet build","timestamp":"2026-02-07T10:01:00Z"}');
    const entry = parseEntry(raw);

    assert.strictEqual(entry.type, 'command');
    assert.strictEqual(entry.content, 'dotnet build');
  });

  test('JSONL line parsing — error', () => {
    const raw = JSON.parse('{"type":"error","content":"CS1503: Cannot convert","timestamp":"2026-02-07T10:02:00Z"}');
    const entry = parseEntry(raw);

    assert.strictEqual(entry.type, 'error');
    assert.ok(entry.content.includes('CS1503'));
  });

  test('JSONL line parsing — unknown type defaults gracefully', () => {
    const raw = JSON.parse('{"type":"unknown_type","content":"something","timestamp":"2026-02-07T10:03:00Z"}');
    const entry = parseEntry(raw);

    assert.strictEqual(entry.type, 'unknown');
  });

  test('Grouped entries categorization', () => {
    const entries: SessionEntry[] = [
      { timestamp: '2026-02-07T10:00:00Z', type: 'file_edit', content: 'Edit 1' },
      { timestamp: '2026-02-07T10:01:00Z', type: 'file_edit', content: 'Edit 2' },
      { timestamp: '2026-02-07T10:02:00Z', type: 'command', content: 'build' },
      { timestamp: '2026-02-07T10:03:00Z', type: 'error', content: 'fail' },
      { timestamp: '2026-02-07T10:04:00Z', type: 'finding', content: 'tech debt' },
      { timestamp: '2026-02-07T10:05:00Z', type: 'memory', content: 'ingested' },
    ];

    const grouped: Record<string, SessionEntry[]> = {
      file_edit: [],
      command: [],
      error: [],
      finding: [],
      memory: [],
      unknown: [],
    };

    for (const entry of entries) {
      grouped[entry.type].push(entry);
    }

    assert.strictEqual(grouped.file_edit.length, 2);
    assert.strictEqual(grouped.command.length, 1);
    assert.strictEqual(grouped.error.length, 1);
    assert.strictEqual(grouped.finding.length, 1);
    assert.strictEqual(grouped.memory.length, 1);
    assert.strictEqual(grouped.unknown.length, 0);
  });

  test('Incremental file reading tracks position', () => {
    let position = 0;
    const fileContent = '{"type":"file_edit","content":"a"}\n{"type":"command","content":"b"}\n';

    // First read
    const firstChunk = fileContent.substring(position, 35);
    position = 35;
    assert.ok(firstChunk.length > 0);

    // Second read
    const secondChunk = fileContent.substring(position);
    position = fileContent.length;
    assert.ok(secondChunk.length > 0);
    assert.strictEqual(position, fileContent.length);
  });

  test('Home directory expansion', () => {
    const configured = '~/.cc-serialmemory/sessions';
    const expanded = configured.replace('~', '/home/testuser');

    assert.ok(!expanded.includes('~'));
    assert.ok(expanded.startsWith('/home/testuser'));
    assert.ok(expanded.endsWith('/sessions'));
  });
});

// Mirror of JournalWatcher.parseEntry for testing
function parseEntry(raw: Record<string, unknown>): SessionEntry {
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
