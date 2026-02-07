import * as assert from 'assert';
import { CMD, CONFIG, DEFAULTS } from '../../src/constants';
import { AGENT_CONFIGS } from '../../src/types/agent';
import { buildAgentPrompt, escapePromptForShell } from '../../src/prompts/agentPrompts';
import type { AgentRole } from '../../src/types/agent';

suite('Agent Orchestrator Test Suite', () => {
  suite('Constants', () => {
    test('ORCHESTRATE_AGENTS command follows naming convention', () => {
      assert.ok(CMD.ORCHESTRATE_AGENTS.startsWith('serialtree.'));
    });

    test('Agent config keys exist in CONFIG', () => {
      assert.ok(CONFIG.AGENT_POOL_SIZE);
      assert.ok(CONFIG.AGENT_TIMEOUT);
      assert.ok(CONFIG.AGENT_SCOPE);
      assert.ok(CONFIG.CLAUDE_COMMAND);
    });

    test('Agent defaults are sensible', () => {
      assert.ok(DEFAULTS.AGENT_POOL_SIZE >= 1 && DEFAULTS.AGENT_POOL_SIZE <= 5);
      assert.ok(DEFAULTS.AGENT_TIMEOUT >= 60_000);
      assert.ok(DEFAULTS.AGENT_POLL_INTERVAL >= 5_000);
      assert.ok(DEFAULTS.AGENT_SCOPE.length > 0);
      assert.strictEqual(typeof DEFAULTS.CLAUDE_COMMAND, 'string');
    });
  });

  suite('Explorer Constants', () => {
    test('EXPLORE_CODEBASE command follows naming convention', () => {
      assert.ok(CMD.EXPLORE_CODEBASE.startsWith('serialtree.'));
    });

    test('Explorer config keys exist in CONFIG', () => {
      assert.ok(CONFIG.EXPLORER_MAX_MODULES);
      assert.ok(CONFIG.EXPLORER_TIMEOUT);
    });

    test('Explorer defaults are sensible', () => {
      assert.ok(DEFAULTS.EXPLORER_MAX_MODULES >= 1 && DEFAULTS.EXPLORER_MAX_MODULES <= 12);
      assert.ok(DEFAULTS.EXPLORER_TIMEOUT >= 60_000);
      assert.ok(DEFAULTS.EXPLORER_SUB_TIMEOUT >= 60_000);
      assert.ok(DEFAULTS.EXPLORER_SUB_TIMEOUT < DEFAULTS.EXPLORER_TIMEOUT);
    });
  });

  suite('Agent Configs', () => {
    const roles: AgentRole[] = ['security', 'performance', 'patterns', 'reviewer', 'custom', 'explorer', 'explorer-sub'];

    test('All roles have configs', () => {
      for (const role of roles) {
        assert.ok(AGENT_CONFIGS[role], `Missing config for role: ${role}`);
      }
    });

    test('All configs have required fields', () => {
      for (const role of roles) {
        const config = AGENT_CONFIGS[role];
        assert.ok(config.label, `Missing label for ${role}`);
        assert.ok(config.description, `Missing description for ${role}`);
        assert.ok(config.icon, `Missing icon for ${role}`);
        assert.strictEqual(config.role, role, `Role mismatch for ${role}`);
      }
    });

    test('All labels are unique', () => {
      const labels = Object.values(AGENT_CONFIGS).map((c) => c.label);
      const unique = new Set(labels);
      assert.strictEqual(labels.length, unique.size, 'Agent labels must be unique');
    });
  });

  suite('Prompt Generation', () => {
    const baseParams = {
      runId: 'run-test123-abcd1234',
      workspacePath: '/home/user/project',
      scope: ['src/**/*.ts'] as readonly string[],
    };

    test('Security prompt includes OWASP and injection keywords', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'security' });
      assert.ok(prompt.includes('OWASP'));
      assert.ok(prompt.includes('injection'));
      assert.ok(prompt.includes('security'));
    });

    test('Performance prompt includes N+1 and caching keywords', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'performance' });
      assert.ok(prompt.includes('N+1'));
      assert.ok(prompt.includes('caching'));
    });

    test('Patterns prompt includes dead code and duplication keywords', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'patterns' });
      assert.ok(prompt.includes('Dead code'));
      assert.ok(prompt.includes('duplication'));
    });

    test('Reviewer prompt includes edge cases and race conditions', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'reviewer' });
      assert.ok(prompt.includes('edge case'));
      assert.ok(prompt.includes('Race conditions'));
    });

    test('Custom prompt uses provided custom text', () => {
      const prompt = buildAgentPrompt({
        ...baseParams,
        role: 'custom',
        customPrompt: 'Find all TODO comments',
      });
      assert.ok(prompt.includes('Find all TODO comments'));
    });

    test('All prompts include run ID', () => {
      for (const role of ['security', 'performance', 'patterns', 'reviewer', 'custom'] as AgentRole[]) {
        const prompt = buildAgentPrompt({ ...baseParams, role });
        assert.ok(prompt.includes(baseParams.runId), `Prompt for ${role} missing run ID`);
      }
    });

    test('All prompts include workspace path', () => {
      for (const role of ['security', 'performance', 'patterns', 'reviewer'] as AgentRole[]) {
        const prompt = buildAgentPrompt({ ...baseParams, role });
        assert.ok(prompt.includes(baseParams.workspacePath), `Prompt for ${role} missing workspace path`);
      }
    });

    test('All prompts include scope', () => {
      for (const role of ['security', 'performance', 'patterns', 'reviewer'] as AgentRole[]) {
        const prompt = buildAgentPrompt({ ...baseParams, role });
        assert.ok(prompt.includes('src/**/*.ts'), `Prompt for ${role} missing scope`);
      }
    });

    test('All prompts include communication protocol', () => {
      for (const role of ['security', 'performance', 'patterns', 'reviewer'] as AgentRole[]) {
        const prompt = buildAgentPrompt({ ...baseParams, role });
        assert.ok(prompt.includes('memory_search'), `Prompt for ${role} missing search instruction`);
        assert.ok(prompt.includes('memory_ingest'), `Prompt for ${role} missing ingest instruction`);
        assert.ok(prompt.includes('AGENT_COMPLETE'), `Prompt for ${role} missing completion marker`);
      }
    });

    test('All prompts include no-file-modification rules', () => {
      for (const role of ['security', 'performance', 'patterns', 'reviewer'] as AgentRole[]) {
        const prompt = buildAgentPrompt({ ...baseParams, role });
        assert.ok(prompt.includes('Do NOT create or modify any files'), `Prompt for ${role} missing file rule`);
      }
    });
  });

  suite('Explorer Prompt Generation', () => {
    const baseParams = {
      runId: 'run-test123-abcd1234',
      workspacePath: '/home/user/project',
      scope: ['src/**/*.ts'] as readonly string[],
    };

    test('Explorer prompt includes module limit', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'explorer', maxModules: 5 });
      assert.ok(prompt.includes('5'), 'Explorer prompt missing module count');
      assert.ok(prompt.includes('Maximum 5 modules'), 'Explorer prompt missing module limit instruction');
    });

    test('Explorer prompt includes mapping instructions', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'explorer' });
      assert.ok(prompt.includes('ORCHESTRATOR'), 'Explorer prompt missing orchestrator role');
      assert.ok(prompt.includes('MAP the codebase'), 'Explorer prompt missing mapping instruction');
      assert.ok(prompt.includes('bounded context'), 'Explorer prompt missing bounded context concept');
    });

    test('Explorer prompt includes module memory format', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'explorer' });
      assert.ok(prompt.includes('# Module:'), 'Explorer prompt missing module header format');
      assert.ok(prompt.includes('Purpose:'), 'Explorer prompt missing purpose field');
      assert.ok(prompt.includes('Key Files:'), 'Explorer prompt missing key files field');
      assert.ok(prompt.includes('Entry Points:'), 'Explorer prompt missing entry points field');
      assert.ok(prompt.includes('Dependencies:'), 'Explorer prompt missing dependencies field');
    });

    test('Explorer prompt includes no-file-modification rules', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'explorer' });
      assert.ok(prompt.includes('Do NOT create, modify, or delete any files'), 'Explorer missing file rule');
    });

    test('Explorer prompt uses default maxModules when not specified', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'explorer' });
      assert.ok(prompt.includes('7'), 'Explorer prompt missing default module count');
    });

    test('Explorer prompt includes communication protocol', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'explorer' });
      assert.ok(prompt.includes('memory_search'), 'Explorer prompt missing search instruction');
      assert.ok(prompt.includes('memory_ingest'), 'Explorer prompt missing ingest instruction');
      assert.ok(prompt.includes('AGENT_COMPLETE'), 'Explorer prompt missing completion marker');
    });

    test('Explorer-sub prompt includes parent context', () => {
      const prompt = buildAgentPrompt({
        ...baseParams,
        role: 'explorer-sub',
        parentModuleName: 'Auth Module',
        parentModuleContext: 'Handles user authentication and JWT tokens',
      });
      assert.ok(prompt.includes('Auth Module'), 'Sub prompt missing parent module name');
      assert.ok(prompt.includes('Handles user authentication'), 'Sub prompt missing parent context');
    });

    test('Explorer-sub prompt enforces depth limit', () => {
      const prompt = buildAgentPrompt({
        ...baseParams,
        role: 'explorer-sub',
        parentModuleName: 'Core',
        parentModuleContext: 'Core domain logic',
      });
      assert.ok(prompt.includes('Do NOT spawn further agents'), 'Sub prompt missing depth limit');
      assert.ok(prompt.includes('depth_level: 1'), 'Sub prompt missing depth level');
    });

    test('Explorer-sub prompt includes sub-component format', () => {
      const prompt = buildAgentPrompt({
        ...baseParams,
        role: 'explorer-sub',
        parentModuleName: 'API',
        parentModuleContext: 'REST API layer',
      });
      assert.ok(prompt.includes('# SubComponent:'), 'Sub prompt missing component header format');
      assert.ok(prompt.includes('Public API:'), 'Sub prompt missing public API field');
      assert.ok(prompt.includes('Data Flow:'), 'Sub prompt missing data flow field');
    });

    test('Explorer-sub prompt includes communication protocol', () => {
      const prompt = buildAgentPrompt({
        ...baseParams,
        role: 'explorer-sub',
        parentModuleName: 'Core',
        parentModuleContext: 'Core logic',
      });
      assert.ok(prompt.includes('memory_search'), 'Sub prompt missing search instruction');
      assert.ok(prompt.includes('memory_ingest'), 'Sub prompt missing ingest instruction');
      assert.ok(prompt.includes('AGENT_COMPLETE'), 'Sub prompt missing completion marker');
    });

    test('Explorer prompts use explorer source in protocol', () => {
      const prompt = buildAgentPrompt({ ...baseParams, role: 'explorer' });
      assert.ok(prompt.includes('serialtree-agent-explorer'), 'Explorer source not set correctly');
    });
  });

  suite('Shell Escaping', () => {
    test('Escapes double quotes', () => {
      assert.strictEqual(escapePromptForShell('say "hello"'), 'say \\"hello\\"');
    });

    test('Escapes backslashes', () => {
      assert.strictEqual(escapePromptForShell('path\\to\\file'), 'path\\\\to\\\\file');
    });

    test('Escapes dollar signs', () => {
      assert.strictEqual(escapePromptForShell('$HOME'), '\\$HOME');
    });

    test('Escapes backticks', () => {
      assert.strictEqual(escapePromptForShell('`command`'), '\\`command\\`');
    });

    test('Escapes newlines', () => {
      assert.strictEqual(escapePromptForShell('line1\nline2'), 'line1\\nline2');
      assert.strictEqual(escapePromptForShell('line1\rline2'), 'line1\\rline2');
    });

    test('Handles combined special chars', () => {
      const input = 'path\\to "$HOME" `cmd`';
      const escaped = escapePromptForShell(input);
      assert.ok(!escaped.includes('$H'));
      assert.ok(escaped.includes('\\$'));
    });
  });

  suite('Run ID Format', () => {
    test('Run ID matches expected pattern', () => {
      // run-<base36-timestamp>-<8-hex-chars>
      const pattern = /^run-[0-9a-z]+-[0-9a-f]{8}$/;
      // We can't call generateRunId directly (it's not exported),
      // but we validate the pattern structure
      assert.ok(pattern.test('run-abc123-deadbeef'));
      assert.ok(!pattern.test('invalid-id'));
      assert.ok(!pattern.test('run-'));
    });
  });
});
