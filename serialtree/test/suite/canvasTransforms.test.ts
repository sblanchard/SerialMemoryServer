import * as assert from 'assert';
import {
  transformMemory,
  transformEntity,
  transformAgent,
  transformFinding,
  transformModule,
  transformActivity,
  buildEdgesFromGraph,
  buildEntityLinks,
  aggregateAll,
} from '../../src/services/CanvasDataAggregator';
import type { SearchResult } from '../../src/types/memory';
import type { GraphNode, GraphEdge } from '../../src/types/graph';
import type { SpawnedAgent, AgentFinding } from '../../src/types/agent';

suite('Canvas Transforms Test Suite', () => {
  // --- Memory transforms ---

  test('transformMemory creates correct node shape', () => {
    const searchResult: SearchResult = {
      id: 'mem-1',
      content: 'This is a test memory about something important',
      createdAt: '2024-01-15T10:00:00Z',
      similarity: 0.85,
      entities: [{ id: 'e1', name: 'TestEntity', type: 'ORG' }],
      memoryType: 'decision',
      source: 'test',
    };

    const node = transformMemory(searchResult);

    assert.strictEqual(node.nodeType, 'memory');
    assert.ok(node.id.startsWith('mem-'));
    assert.strictEqual(node.timestamp, '2024-01-15T10:00:00Z');
    assert.ok('content' in node);
    assert.ok('similarity' in node);
    assert.ok('entities' in node);
  });

  test('transformMemory truncates long labels', () => {
    const longContent = 'A'.repeat(200);
    const result: SearchResult = {
      id: 'mem-long',
      content: longContent,
      createdAt: '2024-01-01T00:00:00Z',
      entities: [],
    };

    const node = transformMemory(result);
    assert.ok(node.label.length <= 63, 'Label should be truncated');
  });

  test('transformMemory returns immutable result', () => {
    const result: SearchResult = {
      id: 'mem-imm',
      content: 'test',
      createdAt: '2024-01-01T00:00:00Z',
      entities: [],
    };

    const node1 = transformMemory(result);
    const node2 = transformMemory(result);

    assert.notStrictEqual(node1, node2, 'Each call should return a new object');
    assert.deepStrictEqual(node1, node2, 'But with equal content');
  });

  // --- Entity transforms ---

  test('transformEntity creates correct node shape', () => {
    const graphNode: GraphNode = {
      id: 'ent-1',
      label: 'Acme Corp',
      group: 'ORG',
    };

    const node = transformEntity(graphNode);

    assert.strictEqual(node.nodeType, 'entity');
    assert.ok(node.id.startsWith('ent-'));
    assert.strictEqual(node.label, 'Acme Corp');
    assert.ok('entityType' in node);
  });

  // --- Agent transforms ---

  test('transformAgent creates correct node shape', () => {
    const agent: SpawnedAgent = {
      id: 'run-1-security',
      role: 'security',
      terminalName: 'SerialTree Agent: Security Reviewer',
      startedAt: Date.now() - 5000,
    };

    const node = transformAgent(agent, 'running', 3);

    assert.strictEqual(node.nodeType, 'agent');
    assert.ok(node.id.startsWith('agt-'));
    assert.ok('status' in node);
    assert.ok('findingCount' in node);
  });

  test('transformAgent supports all status values', () => {
    const agent: SpawnedAgent = {
      id: 'run-1-test',
      role: 'reviewer',
      terminalName: 'test',
      startedAt: Date.now(),
    };

    for (const status of ['running', 'completed', 'failed'] as const) {
      const node = transformAgent(agent, status, 0);
      assert.ok('status' in node);
    }
  });

  // --- Finding transforms ---

  test('transformFinding creates correct node shape', () => {
    const finding: AgentFinding = {
      role: 'security',
      severity: 'high',
      title: 'SQL Injection Risk',
      file: 'src/api.ts',
      line: 42,
      description: 'Unparameterized query detected',
      suggestion: 'Use parameterized queries',
    };

    const node = transformFinding(finding, 0);

    assert.strictEqual(node.nodeType, 'finding');
    assert.ok(node.id.startsWith('fnd-'));
    assert.ok('severity' in node);
    assert.ok('description' in node);
  });

  // --- Module transforms ---

  test('transformModule creates correct node shape', () => {
    const mod = {
      name: 'Authentication Module',
      memoryContent: '# Module: Authentication\nHandles user auth\nKey files: src/auth.ts, src/middleware.ts',
    };

    const node = transformModule(mod, 0);

    assert.strictEqual(node.nodeType, 'module');
    assert.ok(node.id.startsWith('mod-'));
    assert.strictEqual(node.label, 'Authentication Module');
    assert.ok('purpose' in node);
    assert.ok('keyFiles' in node);
  });

  // --- Activity transforms ---

  test('transformActivity creates correct node shape', () => {
    const entry = {
      timestamp: '2024-01-15T10:30:00Z',
      type: 'file_edit' as const,
      content: 'Edit: auth.ts',
      file: 'src/auth.ts',
    };

    const node = transformActivity(entry, 0);

    assert.strictEqual(node.nodeType, 'activity');
    assert.ok(node.id.startsWith('act-'));
    assert.ok('activityType' in node);
  });

  // --- Edge transforms ---

  test('buildEdgesFromGraph creates relationship edges', () => {
    const graphEdges: GraphEdge[] = [
      { from: 'a', to: 'b', label: 'works at' },
      { from: 'c', to: 'd', label: 'located in' },
    ];

    const edges = buildEdgesFromGraph(graphEdges);

    assert.strictEqual(edges.length, 2);
    for (const edge of edges) {
      assert.strictEqual(edge.edgeType, 'relationship');
      assert.ok(edge.id.length > 0);
    }
  });

  test('buildEdgesFromGraph returns new array', () => {
    const graphEdges: GraphEdge[] = [{ from: 'a', to: 'b' }];
    const edges1 = buildEdgesFromGraph(graphEdges);
    const edges2 = buildEdgesFromGraph(graphEdges);

    assert.notStrictEqual(edges1, edges2);
    assert.deepStrictEqual(edges1, edges2);
  });

  // --- Entity links ---

  test('buildEntityLinks creates entity-link edges', () => {
    const memories = [{ id: 'mem-1', label: 'test', nodeType: 'memory' as const }];
    const entities = [{ id: 'ent-e1', label: 'TestEntity', nodeType: 'entity' as const }];
    const searchResults: SearchResult[] = [{
      id: '1',
      content: 'test',
      createdAt: '2024-01-01T00:00:00Z',
      entities: [{ id: 'e1', name: 'TestEntity', type: 'ORG' }],
    }];

    const edges = buildEntityLinks(memories, entities, searchResults);

    assert.ok(edges.length > 0, 'Should create entity-link edges');
    for (const edge of edges) {
      assert.strictEqual(edge.edgeType, 'entity-link');
    }
  });

  // --- aggregateAll ---

  test('aggregateAll returns valid CanvasData', () => {
    const result = aggregateAll({
      graphData: { nodes: [], edges: [], memories: [] },
      searchResults: [],
      agents: [],
      agentStatus: new Map(),
      agentFindings: [],
      codeFindings: [],
      modules: [],
      activities: [],
      maxNodes: 500,
    });

    assert.ok(Array.isArray(result.nodes));
    assert.ok(Array.isArray(result.edges));
  });

  test('aggregateAll respects maxNodes limit', () => {
    const searchResults: SearchResult[] = Array.from({ length: 20 }, (_, i) => ({
      id: `mem-${i}`,
      content: `Memory ${i}`,
      createdAt: '2024-01-01T00:00:00Z',
      entities: [],
    }));

    const result = aggregateAll({
      graphData: { nodes: [], edges: [], memories: [] },
      searchResults,
      agents: [],
      agentStatus: new Map(),
      agentFindings: [],
      codeFindings: [],
      modules: [],
      activities: [],
      maxNodes: 5,
    });

    assert.ok(result.nodes.length <= 5, `Expected <= 5 nodes, got ${result.nodes.length}`);
  });

  test('aggregateAll produces unique node IDs', () => {
    const searchResults: SearchResult[] = [
      { id: 'mem-1', content: 'Test 1', createdAt: '2024-01-01T00:00:00Z', entities: [] },
      { id: 'mem-2', content: 'Test 2', createdAt: '2024-01-02T00:00:00Z', entities: [] },
    ];

    const result = aggregateAll({
      graphData: { nodes: [{ id: 'e1', label: 'Entity', group: 'ORG' }], edges: [], memories: [] },
      searchResults,
      agents: [],
      agentStatus: new Map(),
      agentFindings: [],
      codeFindings: [],
      modules: [],
      activities: [],
      maxNodes: 500,
    });

    const ids = result.nodes.map(n => n.id);
    const uniqueIds = new Set(ids);
    assert.strictEqual(ids.length, uniqueIds.size, 'All node IDs should be unique');
  });
});
