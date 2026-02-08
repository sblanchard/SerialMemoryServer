import * as assert from 'assert';
import { aggregateAll, type AggregateParams } from '../../src/services/CanvasDataAggregator';
import type { SearchResult } from '../../src/types/memory';
import type { GraphData } from '../../src/types/graph';
import type { SpawnedAgent, AgentFinding, ModuleDescriptor } from '../../src/types/agent';
import type { SessionEntry } from '../../src/types/memory';

function createBaseParams(overrides?: Partial<AggregateParams>): AggregateParams {
  return {
    graphData: { nodes: [], edges: [], memories: [] },
    searchResults: [],
    agents: [],
    agentStatus: new Map(),
    agentFindings: [],
    codeFindings: [],
    modules: [],
    activities: [],
    maxNodes: 500,
    ...overrides,
  };
}

suite('CanvasDataAggregator Test Suite', () => {
  test('aggregateAll with empty inputs returns empty data', () => {
    const result = aggregateAll(createBaseParams());

    assert.strictEqual(result.nodes.length, 0);
    assert.strictEqual(result.edges.length, 0);
  });

  test('aggregateAll includes memory nodes from search results', () => {
    const searchResults: SearchResult[] = [
      { id: '1', content: 'Memory 1', createdAt: '2024-01-01T00:00:00Z', entities: [], memoryType: 'decision' },
      { id: '2', content: 'Memory 2', createdAt: '2024-01-02T00:00:00Z', entities: [], memoryType: 'learning' },
    ];

    const result = aggregateAll(createBaseParams({ searchResults }));

    const memoryNodes = result.nodes.filter(n => n.nodeType === 'memory');
    assert.strictEqual(memoryNodes.length, 2);
  });

  test('aggregateAll includes entity nodes from graph data', () => {
    const graphData: GraphData = {
      nodes: [
        { id: 'e1', label: 'Acme Corp', group: 'ORG' },
        { id: 'e2', label: 'John Smith', group: 'PERSON' },
      ],
      edges: [{ from: 'e1', to: 'e2', label: 'employs' }],
      memories: [],
    };

    const result = aggregateAll(createBaseParams({ graphData }));

    const entityNodes = result.nodes.filter(n => n.nodeType === 'entity');
    assert.strictEqual(entityNodes.length, 2);

    const relationshipEdges = result.edges.filter(e => e.edgeType === 'relationship');
    assert.strictEqual(relationshipEdges.length, 1);
  });

  test('aggregateAll includes agent nodes', () => {
    const agents: SpawnedAgent[] = [
      { id: 'run-1-security', role: 'security', terminalName: 'test', startedAt: Date.now() },
    ];
    const agentStatus = new Map<string, 'running' | 'completed' | 'failed'>([
      ['run-1-security', 'running'],
    ]);

    const result = aggregateAll(createBaseParams({ agents, agentStatus }));

    const agentNodes = result.nodes.filter(n => n.nodeType === 'agent');
    assert.strictEqual(agentNodes.length, 1);
  });

  test('aggregateAll includes finding nodes from code findings', () => {
    const codeFindings = [
      {
        type: 'bug' as const,
        severity: 'high' as const,
        file: 'src/api.ts',
        line: 42,
        title: 'Null check missing',
        description: 'Missing null check on user input',
      },
    ];

    const result = aggregateAll(createBaseParams({ codeFindings }));

    const findingNodes = result.nodes.filter(n => n.nodeType === 'finding');
    assert.strictEqual(findingNodes.length, 1);
  });

  test('aggregateAll includes module nodes', () => {
    const modules: ModuleDescriptor[] = [
      { name: 'Auth Module', memoryContent: '# Module: Auth\nHandles authentication' },
    ];

    const result = aggregateAll(createBaseParams({ modules }));

    const moduleNodes = result.nodes.filter(n => n.nodeType === 'module');
    assert.strictEqual(moduleNodes.length, 1);
  });

  test('aggregateAll includes activity nodes', () => {
    const activities: SessionEntry[] = [
      { timestamp: '2024-01-15T10:00:00Z', type: 'file_edit', content: 'Edit: auth.ts', file: 'src/auth.ts' },
      { timestamp: '2024-01-15T10:01:00Z', type: 'command', content: '$ npm test' },
    ];

    const result = aggregateAll(createBaseParams({ activities }));

    const activityNodes = result.nodes.filter(n => n.nodeType === 'activity');
    assert.strictEqual(activityNodes.length, 2);
  });

  test('aggregateAll truncates to maxNodes with priority', () => {
    const agents: SpawnedAgent[] = Array.from({ length: 3 }, (_, i) => ({
      id: `run-1-agent-${i}`,
      role: 'reviewer' as const,
      terminalName: `term-${i}`,
      startedAt: Date.now(),
    }));
    const agentStatus = new Map(agents.map(a => [a.id, 'running' as const]));

    const searchResults: SearchResult[] = Array.from({ length: 10 }, (_, i) => ({
      id: `mem-${i}`,
      content: `Memory ${i}`,
      createdAt: '2024-01-01T00:00:00Z',
      entities: [],
    }));

    const activities: SessionEntry[] = Array.from({ length: 10 }, (_, i) => ({
      timestamp: `2024-01-15T10:${String(i).padStart(2, '0')}:00Z`,
      type: 'command' as const,
      content: `Command ${i}`,
    }));

    const result = aggregateAll(createBaseParams({
      agents,
      agentStatus,
      searchResults,
      activities,
      maxNodes: 8,
    }));

    assert.ok(result.nodes.length <= 8, `Expected <= 8 nodes, got ${result.nodes.length}`);

    // Agents should be prioritized
    const agentNodes = result.nodes.filter(n => n.nodeType === 'agent');
    assert.strictEqual(agentNodes.length, 3, 'All agent nodes should be preserved');
  });

  test('aggregateAll output is immutable (new arrays)', () => {
    const params = createBaseParams({
      searchResults: [
        { id: '1', content: 'test', createdAt: '2024-01-01T00:00:00Z', entities: [] },
      ],
    });

    const result1 = aggregateAll(params);
    const result2 = aggregateAll(params);

    assert.notStrictEqual(result1.nodes, result2.nodes, 'Each call should return new arrays');
    assert.notStrictEqual(result1.edges, result2.edges);
  });

  test('aggregateAll creates entity-link edges between memories and entities', () => {
    const searchResults: SearchResult[] = [{
      id: '1',
      content: 'Works at Acme',
      createdAt: '2024-01-01T00:00:00Z',
      entities: [{ id: 'e1', name: 'Acme', type: 'ORG' }],
    }];

    const graphData: GraphData = {
      nodes: [{ id: 'e1', label: 'Acme', group: 'ORG' }],
      edges: [],
      memories: [],
    };

    const result = aggregateAll(createBaseParams({ searchResults, graphData }));

    const entityLinks = result.edges.filter(e => e.edgeType === 'entity-link');
    assert.ok(entityLinks.length > 0, 'Should create entity-link edges');
  });
});
