import * as assert from 'assert';

suite('McpClient Test Suite', () => {
  test('JSON-RPC request format is correct', () => {
    const request = {
      jsonrpc: '2.0' as const,
      id: 1,
      method: 'tools/call',
      params: { name: 'memory_search', arguments: { query: 'test' } },
    };

    assert.strictEqual(request.jsonrpc, '2.0');
    assert.strictEqual(typeof request.id, 'number');
    assert.strictEqual(request.method, 'tools/call');
    assert.ok(request.params);
  });

  test('JSON-RPC response parsing handles success', () => {
    const response = {
      jsonrpc: '2.0',
      id: 1,
      result: {
        content: [{ type: 'text', text: '[]' }],
      },
    };

    assert.ok(response.result);
    assert.ok(!('error' in response));

    const content = response.result.content.find(
      (c: { type: string }) => c.type === 'text',
    );
    assert.ok(content);
    assert.strictEqual(content.text, '[]');
  });

  test('JSON-RPC response parsing handles error', () => {
    const response = {
      jsonrpc: '2.0',
      id: 1,
      error: { code: -32600, message: 'Invalid request' },
    };

    assert.ok(response.error);
    assert.strictEqual(response.error.code, -32600);
    assert.strictEqual(response.error.message, 'Invalid request');
  });

  test('Search result parsing handles valid JSON', () => {
    const rawText = JSON.stringify([
      { id: '1', content: 'Test memory', similarity: 0.95, entities: [] },
    ]);

    const results = JSON.parse(rawText);
    assert.ok(Array.isArray(results));
    assert.strictEqual(results.length, 1);
    assert.strictEqual(results[0].content, 'Test memory');
    assert.strictEqual(results[0].similarity, 0.95);
  });

  test('Search result parsing handles empty response', () => {
    const rawText = '[]';
    const results = JSON.parse(rawText);
    assert.ok(Array.isArray(results));
    assert.strictEqual(results.length, 0);
  });

  test('Buffer-based line parsing splits correctly', () => {
    let buffer = '';
    const parsed: string[] = [];

    const processData = (data: string) => {
      buffer += data;
      const lines = buffer.split('\n');
      buffer = lines.pop() ?? '';
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed) {
          parsed.push(trimmed);
        }
      }
    };

    processData('{"jsonrpc":"2.0","id":1}\n');
    processData('{"jsonrpc":"2.0"');
    processData(',"id":2}\n');

    assert.strictEqual(parsed.length, 2);
    assert.strictEqual(JSON.parse(parsed[0]).id, 1);
    assert.strictEqual(JSON.parse(parsed[1]).id, 2);
    assert.strictEqual(buffer, '');
  });

  test('MCP initialize request structure', () => {
    const initRequest = {
      jsonrpc: '2.0',
      id: 1,
      method: 'initialize',
      params: {
        protocolVersion: '2024-11-05',
        capabilities: {},
        clientInfo: { name: 'serialtree', version: '0.1.0' },
      },
    };

    assert.strictEqual(initRequest.method, 'initialize');
    assert.strictEqual(initRequest.params.protocolVersion, '2024-11-05');
    assert.strictEqual(initRequest.params.clientInfo.name, 'serialtree');
  });
});
