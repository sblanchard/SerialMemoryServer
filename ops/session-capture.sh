#!/usr/bin/env bash
# session-capture.sh - Capture tool activity from Claude Code PostToolUse hooks
# Called from PostToolUse hooks for Write/Edit/Bash
# Reads tool result JSON from stdin, appends JSONL entry to session log
# Designed for <50ms execution time - no MCP calls, just file appends
set -euo pipefail

# Session log directory
LOG_DIR="$HOME/.cc-serialmemory/sessions"
mkdir -p "$LOG_DIR"

# Session ID from Claude Code env var, fallback to date-based
SESSION_ID="${CLAUDE_SESSION_ID:-$(date +%Y%m%d)}"
LOG_FILE="$LOG_DIR/${SESSION_ID}.jsonl"

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

# Append JSONL entry (atomic write via echo)
if command -v jq &>/dev/null; then
    jq -nc --arg ts "$TS" --arg tool "$TOOL_NAME" --arg file "$FILE_PATH" --arg result "$RESULT" \
        '{ts: $ts, tool: $tool, file: $file, result: $result}' >> "$LOG_FILE"
else
    echo "{\"ts\":\"$TS\",\"tool\":\"$TOOL_NAME\",\"file\":\"$FILE_PATH\",\"result\":\"\"}" >> "$LOG_FILE"
fi

echo "Activity logged"
