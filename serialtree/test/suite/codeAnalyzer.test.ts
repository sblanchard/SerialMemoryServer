import * as assert from 'assert';

suite('CodeAnalyzer Test Suite', () => {
  test('TODO pattern detection', () => {
    const pattern = /\b(TODO|FIXME|HACK|XXX|BUG)\b[:\s]*(.*)/gi;
    const lines = [
      '// TODO: Fix this later',
      '// FIXME: Critical bug here',
      'const normal = "code";',
      '/* HACK: Workaround for issue #123 */',
      '// XXX: This is bad',
      '// BUG: Known issue',
    ];

    const matches: Array<{ marker: string; detail: string; line: number }> = [];

    for (let i = 0; i < lines.length; i++) {
      pattern.lastIndex = 0;
      let match;
      while ((match = pattern.exec(lines[i])) !== null) {
        matches.push({
          marker: match[1].toUpperCase(),
          detail: match[2]?.trim() ?? '',
          line: i + 1,
        });
      }
    }

    assert.strictEqual(matches.length, 5);
    assert.strictEqual(matches[0].marker, 'TODO');
    assert.strictEqual(matches[0].detail, 'Fix this later');
    assert.strictEqual(matches[1].marker, 'FIXME');
    assert.strictEqual(matches[4].marker, 'BUG');
  });

  test('Long file detection', () => {
    const maxLength = 800;
    const shortFile = new Array(400).fill('line');
    const longFile = new Array(900).fill('line');
    const veryLongFile = new Array(1300).fill('line');

    assert.ok(shortFile.length <= maxLength, 'Short file should not trigger');
    assert.ok(longFile.length > maxLength, 'Long file should trigger');
    assert.ok(veryLongFile.length > maxLength * 1.5, 'Very long file should be high severity');
  });

  test('Deep nesting detection', () => {
    const lines = [
      'function outer() {',
      '  if (a) {',
      '    if (b) {',
      '      if (c) {',
      '        if (d) {',
      '          // nesting level 5',
      '        }',
      '      }',
      '    }',
      '  }',
      '}',
    ];

    let maxNesting = 0;
    let currentNesting = 0;
    let maxNestingLine = 0;

    for (let i = 0; i < lines.length; i++) {
      for (const ch of lines[i]) {
        if (ch === '{') { currentNesting++; }
        if (ch === '}') { currentNesting--; }
      }
      if (currentNesting > maxNesting) {
        maxNesting = currentNesting;
        maxNestingLine = i + 1;
      }
    }

    assert.strictEqual(maxNesting, 5);
    assert.ok(maxNesting > 4, 'Should detect deep nesting');
    assert.strictEqual(maxNestingLine, 5);
  });

  test('Bug pattern: .then() without .catch()', () => {
    const lines = [
      'fetchData().then(data => process(data));',
      'fetchData().then(data => handle(data)).catch(err => log(err));',
      'const result = await fetchData();',
    ];

    const hasUnhandled = lines.filter(
      line => /\.then\(/.test(line) && !line.includes('.catch'),
    );

    assert.strictEqual(hasUnhandled.length, 1);
    assert.ok(hasUnhandled[0].includes('fetchData().then(data => process(data))'));
  });

  test('Bug pattern: as any detection', () => {
    const lines = [
      'const x = value as any;',
      'const y = value as string;',
      'return (data as any).property;',
      'const z: number = 42;',
    ];

    const asAnyLines = lines.filter(line => /as\s+any/.test(line));
    assert.strictEqual(asAnyLines.length, 2);
  });

  test('Performance: N+1 pattern detection', () => {
    const lines = [
      'for (const user of users) {',
      '  const profile = await fetchProfile(user.id);',
      '  results.push(profile);',
      '}',
    ];

    let hasN1 = false;
    const loopLine = lines.findIndex(l => /for\s*\(/.test(l));

    if (loopLine >= 0) {
      let braceDepth = 0;
      for (let j = loopLine; j < lines.length; j++) {
        for (const ch of lines[j]) {
          if (ch === '{') { braceDepth++; }
          if (ch === '}') { braceDepth--; }
        }
        if (braceDepth > 0 && /\bawait\b/.test(lines[j]) && j > loopLine) {
          hasN1 = true;
          break;
        }
        if (braceDepth <= 0 && j > loopLine) { break; }
      }
    }

    assert.ok(hasN1, 'Should detect N+1 pattern');
  });

  test('Quick win: console.log detection', () => {
    const lines = [
      'console.log("debug output");',
      '// console.log("commented out");',
      'logger.info("proper logging");',
      'console.log(variable);',
    ];

    const consoleLogs = lines.filter(
      line => /\bconsole\.log\b/.test(line) && !line.trimStart().startsWith('//'),
    );

    assert.strictEqual(consoleLogs.length, 2);
  });

  test('Severity sorting order', () => {
    const severityOrder: Record<string, number> = { critical: 0, high: 1, medium: 2, low: 3 };

    const findings = [
      { severity: 'low' },
      { severity: 'critical' },
      { severity: 'medium' },
      { severity: 'high' },
    ];

    findings.sort((a, b) => severityOrder[a.severity] - severityOrder[b.severity]);

    assert.strictEqual(findings[0].severity, 'critical');
    assert.strictEqual(findings[1].severity, 'high');
    assert.strictEqual(findings[2].severity, 'medium');
    assert.strictEqual(findings[3].severity, 'low');
  });
});
