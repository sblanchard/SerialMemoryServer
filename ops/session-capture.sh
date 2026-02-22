#!/usr/bin/env bash
# session-capture.sh - POST tool activity to SerialMemory API over HTTP
# Called from PostToolUse hooks for Write/Edit/Bash
# Reads tool result JSON from stdin, POSTs to /api/captures/entry
# Designed for <100ms execution time — single HTTP POST, no MCP calls
set -euo pipefail

# SerialMemory API endpoint (set via env or default to localhost)
API_URL="${SERIALMEMORY_ENDPOINT:-http://localhost:5000}"
API_KEY="${SERIALMEMORY_API_KEY:-}"

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

# Timestamp in ISO 8601
TS=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Build JSON payload
if command -v jq &>/dev/null; then
    PAYLOAD=$(jq -nc \
        --arg sid "$SESSION_ID" \
        --arg ts "$TS" \
        --arg tool "$TOOL_NAME" \
        --arg file "$FILE_PATH" \
        --arg result "$RESULT" \
        '{session_id: $sid, ts: $ts, tool: $tool, file: $file, result: $result}')
else
    PAYLOAD="{\"session_id\":\"$SESSION_ID\",\"ts\":\"$TS\",\"tool\":\"$TOOL_NAME\",\"file\":\"$FILE_PATH\",\"result\":\"\"}"
fi

# POST to API (fire-and-forget with timeout)
AUTH_HEADER=""
if [ -n "$API_KEY" ]; then
    AUTH_HEADER="-H \"Authorization: Bearer $API_KEY\""
fi

if command -v curl &>/dev/null; then
    curl -s -m 2 -X POST "${API_URL}/api/captures/entry" \
        -H "Content-Type: application/json" \
        ${API_KEY:+-H "Authorization: Bearer $API_KEY"} \
        -d "$PAYLOAD" > /dev/null 2>&1 || true
elif command -v wget &>/dev/null; then
    echo "$PAYLOAD" | wget -q -O /dev/null --timeout=2 \
        --header="Content-Type: application/json" \
        ${API_KEY:+--header="Authorization: Bearer $API_KEY"} \
        --post-data="$PAYLOAD" "${API_URL}/api/captures/entry" 2>/dev/null || true
fi

echo "Activity captured"
