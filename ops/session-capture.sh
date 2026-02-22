#!/usr/bin/env bash
# session-capture.sh - POST tool activity through MCP HTTP transport
# Called from PostToolUse hooks for Write/Edit/Bash
# Reads tool result JSON from stdin, sends JSON-RPC to MCP at localhost:4545
# The MCP client already has the API key — no extra env vars needed
# Designed for <100ms execution time — single HTTP POST, fire-and-forget
set -euo pipefail

# MCP HTTP transport (always localhost, started alongside stdio)
MCP_URL="${MCP_HTTP_URL:-http://localhost:4545}/mcp"
MCP_TOKEN="${MCP_HTTP_TOKEN:-}"

# Session ID from Claude Code env var, fallback to date-based
SESSION_ID="${CLAUDE_SESSION_ID:-$(date +%Y%m%d)}"

# Read tool result from stdin (Claude Code passes JSON)
INPUT=$(cat 2>/dev/null || echo '{}')

# Extract tool name and file path using jq (fast) or python3 (fallback)
if command -v jq &>/dev/null; then
    TOOL_NAME=$(echo "$INPUT" | jq -r '.tool_name // .tool // "unknown"' 2>/dev/null || echo "unknown")
    FILE_PATH=$(echo "$INPUT" | jq -r '.file_path // .path // ""' 2>/dev/null || echo "")
    RESULT=$(echo "$INPUT" | jq -r 'tostring' 2>/dev/null | head -c 200 || echo "")
elif command -v python3 &>/dev/null; then
    read -r TOOL_NAME FILE_PATH RESULT <<< "$(python3 -c "
import json, sys
try:
    d = json.loads(sys.stdin.read())
    tool = d.get('tool_name', d.get('tool', 'unknown'))
    fpath = d.get('file_path', d.get('path', ''))
    result = json.dumps(d)[:200]
    print(f'{tool} {fpath} {result}')
except:
    print('unknown  {}')
" <<< "$INPUT" 2>/dev/null || echo "unknown  {}")"
else
    TOOL_NAME="unknown"
    FILE_PATH=""
    RESULT=""
fi

# Build JSON-RPC payload for MCP tools/call
if command -v jq &>/dev/null; then
    PAYLOAD=$(jq -nc \
        --arg sid "$SESSION_ID" \
        --arg tool "$TOOL_NAME" \
        --arg file "$FILE_PATH" \
        --arg result "$RESULT" \
        '{
            jsonrpc: "2.0",
            id: 1,
            method: "tools/call",
            params: {
                name: "capture_entry",
                arguments: {
                    session_id: $sid,
                    tool: $tool,
                    file: $file,
                    result: $result
                }
            }
        }')
else
    PAYLOAD="{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"capture_entry\",\"arguments\":{\"session_id\":\"$SESSION_ID\",\"tool\":\"$TOOL_NAME\",\"file\":\"$FILE_PATH\",\"result\":\"\"}}}"
fi

# POST to MCP HTTP transport (fire-and-forget with 2s timeout)
if command -v curl &>/dev/null; then
    curl -s -m 2 -X POST "$MCP_URL" \
        -H "Content-Type: application/json" \
        ${MCP_TOKEN:+-H "Authorization: Bearer $MCP_TOKEN"} \
        -d "$PAYLOAD" > /dev/null 2>&1 || true
elif command -v wget &>/dev/null; then
    wget -q -O /dev/null --timeout=2 \
        --header="Content-Type: application/json" \
        ${MCP_TOKEN:+--header="Authorization: Bearer $MCP_TOKEN"} \
        --post-data="$PAYLOAD" "$MCP_URL" 2>/dev/null || true
fi

echo "Activity captured"
