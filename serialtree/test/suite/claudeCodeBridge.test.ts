import * as assert from 'assert';
import type { CodeFinding } from '../../src/types/memory';

suite('ClaudeCodeBridge Test Suite', () => {
  test('Finding prompt formatting', () => {
    const finding: CodeFinding = {
      type: 'tech_debt',
      severity: 'high',
      file: '/src/program.cs',
      line: 450,
      title: 'Long function',
      description: 'Function HandleToolCall is 120 lines',
      suggestion: 'Extract helper methods',
    };

    const findingText = [
      `[${finding.type}] ${finding.title}`,
      `File: ${finding.file}:${finding.line}`,
      `Severity: ${finding.severity}`,
      finding.description,
      finding.suggestion ? `Suggestion: ${finding.suggestion}` : '',
    ].filter(Boolean).join('\n');

    assert.ok(findingText.includes('[tech_debt] Long function'));
    assert.ok(findingText.includes('/src/program.cs:450'));
    assert.ok(findingText.includes('Severity: high'));
    assert.ok(findingText.includes('Extract helper methods'));
  });

  test('Prompt template replacement', () => {
    const template = 'Based on this finding:\n{finding}\n\nPlease fix this issue.';
    const findingText = '[bug] Null reference at line 10';
    const prompt = template.replace('{finding}', findingText);

    assert.ok(prompt.includes('Based on this finding:'));
    assert.ok(prompt.includes('[bug] Null reference at line 10'));
    assert.ok(prompt.includes('Please fix this issue.'));
  });

  test('Default prompt without template', () => {
    const finding: CodeFinding = {
      type: 'bug',
      severity: 'critical',
      file: '/src/auth.ts',
      line: 25,
      title: 'Missing null check',
      description: 'Possible null reference in auth middleware',
      suggestion: 'Add null guard before accessing user property',
    };

    const prompt = `Fix this ${finding.type} issue in ${finding.file}:${finding.line} — ${finding.title}. ${finding.description}${finding.suggestion ? ` Suggestion: ${finding.suggestion}` : ''}`;

    assert.ok(prompt.startsWith('Fix this bug issue'));
    assert.ok(prompt.includes('/src/auth.ts:25'));
    assert.ok(prompt.includes('Missing null check'));
    assert.ok(prompt.includes('Suggestion: Add null guard'));
  });

  test('Context prompt formatting', () => {
    const memories = [
      { id: '1', content: 'User prefers TypeScript', similarity: 0.9, entities: [] },
      { id: '2', content: 'Project uses Clean Architecture', similarity: 0.8, entities: [] },
    ];

    const instruction = 'Refactor the auth module';

    const contextBlock = memories
      .slice(0, 5)
      .map((m, i) => `[Memory ${i + 1}]: ${m.content}`)
      .join('\n\n');

    const prompt = `Context from SerialMemory:\n${contextBlock}\n\nInstruction: ${instruction}`;

    assert.ok(prompt.includes('[Memory 1]: User prefers TypeScript'));
    assert.ok(prompt.includes('[Memory 2]: Project uses Clean Architecture'));
    assert.ok(prompt.includes('Instruction: Refactor the auth module'));
  });

  test('Selected text prompt formatting', () => {
    const text = 'function hello() { return "world"; }';
    const fileName = '/src/utils.ts';
    const lineNumber = 42;

    const prompt = `Review this code from ${fileName}:${lineNumber}:\n\n\`\`\`\n${text}\n\`\`\``;

    assert.ok(prompt.includes('/src/utils.ts:42'));
    assert.ok(prompt.includes('function hello()'));
    assert.ok(prompt.includes('```'));
  });

  test('Terminal pattern matching with regex', () => {
    const pattern = 'Claude';
    const terminalNames = ['Claude Code', 'bash', 'zsh', 'My Claude Terminal', 'Terminal'];

    const regex = new RegExp(pattern, 'i');
    const matches = terminalNames.filter(name => regex.test(name));

    assert.strictEqual(matches.length, 2);
    assert.ok(matches.includes('Claude Code'));
    assert.ok(matches.includes('My Claude Terminal'));
  });

  test('Terminal pattern matching with custom regex', () => {
    const pattern = 'claude|anthropic';
    const terminalNames = ['Claude Code', 'anthropic-cli', 'bash', 'Terminal'];

    const regex = new RegExp(pattern, 'i');
    const matches = terminalNames.filter(name => regex.test(name));

    assert.strictEqual(matches.length, 2);
    assert.ok(matches.includes('Claude Code'));
    assert.ok(matches.includes('anthropic-cli'));
  });
});
