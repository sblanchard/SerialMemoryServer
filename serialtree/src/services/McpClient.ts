import { ChildProcess, spawn } from 'child_process';
import { EventEmitter } from 'events';
import * as vscode from 'vscode';
import type { JsonRpcRequest, JsonRpcResponse, McpInitializeResult, McpToolResult } from '../types/mcp';
import type { SearchResult, IngestResult, ToolInfo } from '../types/memory';
import type { GraphData } from '../types/graph';
import { EXTENSION_ID, CONFIG } from '../constants';

interface PendingRequest {
  readonly resolve: (value: unknown) => void;
  readonly reject: (error: Error) => void;
}

export class McpClient extends EventEmitter {
  private process: ChildProcess | null = null;
  private requestId = 0;
  private readonly pending = new Map<number, PendingRequest>();
  private buffer = '';
  private initialized = false;

  async connect(): Promise<void> {
    const config = vscode.workspace.getConfiguration(EXTENSION_ID);
    const mcpPath = config.get<string>(CONFIG.MCP_PROJECT_PATH);

    if (!mcpPath) {
      throw new Error('serialtree.mcpProjectPath not configured. Set it in VS Code settings.');
    }

    return new Promise((resolve, reject) => {
      this.process = spawn('dotnet', ['run', '--project', mcpPath], {
        stdio: ['pipe', 'pipe', 'pipe'],
        env: { ...process.env },
      });

      this.process.stdout?.on('data', (chunk: Buffer) => {
        this.handleData(chunk.toString());
      });

      this.process.stderr?.on('data', (chunk: Buffer) => {
        const msg = chunk.toString().trim();
        if (msg) {
          this.emit('log', msg);
        }
      });

      this.process.on('error', (err) => {
        this.emit('error', err);
        if (!this.initialized) {
          reject(err);
        }
      });

      this.process.on('exit', (code) => {
        this.emit('exit', code);
        this.cleanup();
      });

      // Initialize MCP handshake
      this.initialize()
        .then(() => {
          this.initialized = true;
          resolve();
        })
        .catch(reject);
    });
  }

  private async initialize(): Promise<McpInitializeResult> {
    const result = await this.sendRequest('initialize', {
      protocolVersion: '2024-11-05',
      capabilities: {},
      clientInfo: { name: 'serialtree', version: '0.1.0' },
    });

    await this.sendNotification('notifications/initialized', {});
    return result as McpInitializeResult;
  }

  private handleData(data: string): void {
    this.buffer += data;

    const lines = this.buffer.split('\n');
    this.buffer = lines.pop() ?? '';

    for (const line of lines) {
      const trimmed = line.trim();
      if (!trimmed) {
        continue;
      }

      try {
        const message = JSON.parse(trimmed) as JsonRpcResponse;
        const pending = this.pending.get(message.id);

        if (!pending) {
          continue;
        }

        this.pending.delete(message.id);

        if (message.error) {
          pending.reject(new Error(message.error.message));
        } else {
          pending.resolve(message.result);
        }
      } catch {
        // Non-JSON output, treat as log
        this.emit('log', trimmed);
      }
    }
  }

  private sendRequest(method: string, params?: Record<string, unknown>): Promise<unknown> {
    return new Promise((resolve, reject) => {
      if (!this.process?.stdin?.writable) {
        reject(new Error('MCP process not connected'));
        return;
      }

      const id = ++this.requestId;
      const request: JsonRpcRequest = {
        jsonrpc: '2.0',
        id,
        method,
        params,
      };

      this.pending.set(id, { resolve, reject });
      this.process.stdin.write(JSON.stringify(request) + '\n');
    });
  }

  private sendNotification(method: string, params?: Record<string, unknown>): void {
    if (!this.process?.stdin?.writable) {
      return;
    }

    const notification = {
      jsonrpc: '2.0' as const,
      method,
      params,
    };

    this.process.stdin.write(JSON.stringify(notification) + '\n');
  }

  private async callTool(name: string, args: Record<string, unknown>): Promise<McpToolResult> {
    const result = await this.sendRequest('tools/call', {
      name,
      arguments: args,
    });
    return result as McpToolResult;
  }

  private extractText(result: McpToolResult): string {
    const textContent = result.content.find(c => c.type === 'text');
    return textContent?.text ?? '';
  }

  // --- Public API ---

  async search(
    query: string,
    mode: 'semantic' | 'text' | 'hybrid' = 'hybrid',
    memoryType?: string,
    limit = 20,
    threshold = 0.5,
  ): Promise<SearchResult[]> {
    const args: Record<string, unknown> = { query, mode, limit, threshold };
    if (memoryType) {
      args.memory_type = memoryType;
    }

    const result = await this.callTool('memory_search', args);
    const text = this.extractText(result);

    try {
      return JSON.parse(text) as SearchResult[];
    } catch {
      return [];
    }
  }

  async ingest(
    content: string,
    source: string,
    memoryType?: string,
    metadata?: Record<string, unknown>,
  ): Promise<IngestResult> {
    const args: Record<string, unknown> = {
      content,
      source,
      extract_entities: true,
    };
    if (memoryType) {
      args.memory_type = memoryType;
    }
    if (metadata) {
      args.metadata = metadata;
    }

    const result = await this.callTool('memory_ingest', args);
    const text = this.extractText(result);

    try {
      return JSON.parse(text) as IngestResult;
    } catch {
      return { memoryId: text };
    }
  }

  async getToolsInCategory(category: string): Promise<ToolInfo[]> {
    const result = await this.callTool('get_tools_in_category', { path: category });
    const text = this.extractText(result);

    try {
      return JSON.parse(text) as ToolInfo[];
    } catch {
      return [];
    }
  }

  async executeTool(toolPath: string, args: Record<string, unknown>): Promise<unknown> {
    const result = await this.callTool('execute_tool', {
      tool_path: toolPath,
      arguments: args,
    });
    const text = this.extractText(result);

    try {
      return JSON.parse(text);
    } catch {
      return text;
    }
  }

  async exportGraph(limit = 50, query?: string): Promise<GraphData> {
    const result = await this.callTool('execute_tool', {
      tool_path: 'export.export_graph',
      arguments: { limit, query },
    });
    const text = this.extractText(result);

    try {
      return JSON.parse(text) as GraphData;
    } catch {
      return { nodes: [], edges: [], memories: [] };
    }
  }

  async instantiateContext(project: string): Promise<string> {
    const result = await this.callTool('execute_tool', {
      tool_path: 'session.instantiate_context',
      arguments: { project },
    });
    return this.extractText(result);
  }

  async aboutUser(): Promise<string> {
    const result = await this.callTool('memory_about_user', {});
    return this.extractText(result);
  }

  async multiHopSearch(query: string, hops = 2): Promise<unknown> {
    const result = await this.callTool('memory_multi_hop_search', {
      query,
      hops,
    });
    const text = this.extractText(result);

    try {
      return JSON.parse(text);
    } catch {
      return text;
    }
  }

  get isConnected(): boolean {
    return this.initialized && this.process !== null;
  }

  private cleanup(): void {
    this.initialized = false;

    for (const [id, pending] of this.pending) {
      pending.reject(new Error('MCP process exited'));
      this.pending.delete(id);
    }
  }

  dispose(): void {
    if (this.process) {
      this.process.kill();
      this.process = null;
    }
    this.cleanup();
  }
}
