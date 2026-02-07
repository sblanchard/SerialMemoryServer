// JSON-RPC 2.0 types for MCP protocol

export interface JsonRpcRequest {
  readonly jsonrpc: '2.0';
  readonly id: number;
  readonly method: string;
  readonly params?: Record<string, unknown>;
}

export interface JsonRpcResponse {
  readonly jsonrpc: '2.0';
  readonly id: number;
  readonly result?: unknown;
  readonly error?: JsonRpcError;
}

export interface JsonRpcError {
  readonly code: number;
  readonly message: string;
  readonly data?: unknown;
}

export interface JsonRpcNotification {
  readonly jsonrpc: '2.0';
  readonly method: string;
  readonly params?: Record<string, unknown>;
}

// MCP-specific types

export interface McpToolCall {
  readonly name: string;
  readonly arguments: Record<string, unknown>;
}

export interface McpToolResult {
  readonly content: ReadonlyArray<McpContent>;
  readonly isError?: boolean;
}

export interface McpContent {
  readonly type: 'text' | 'image' | 'resource';
  readonly text?: string;
  readonly data?: string;
  readonly mimeType?: string;
}

export interface McpInitializeResult {
  readonly protocolVersion: string;
  readonly capabilities: Record<string, unknown>;
  readonly serverInfo: {
    readonly name: string;
    readonly version: string;
  };
}

// Tool schema types
export interface McpToolSchema {
  readonly name: string;
  readonly description: string;
  readonly inputSchema: {
    readonly type: 'object';
    readonly properties: Record<string, McpPropertySchema>;
    readonly required?: readonly string[];
  };
}

export interface McpPropertySchema {
  readonly type: string;
  readonly description?: string;
  readonly enum?: readonly string[];
  readonly default?: unknown;
}
