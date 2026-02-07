#!/usr/bin/env bash
# session-summarize.sh - Summarize session activity for PreCompact/SessionEnd hooks
# Reads session capture JSONL log and outputs structured summary text
# Usage: bash ops/session-summarize.sh <precompact|session_end>
set -euo pipefail

TRIGGER="${1:-session_end}"
LOG_DIR="$HOME/.cc-serialmemory/sessions"
SESSION_ID="${CLAUDE_SESSION_ID:-$(date +%Y%m%d)}"
LOG_FILE="$LOG_DIR/${SESSION_ID}.jsonl"

# Fallback message if no session log exists
if [ ! -f "$LOG_FILE" ] || [ ! -s "$LOG_FILE" ]; then
    if [ "$TRIGGER" = "precompact" ]; then
        echo ""
        echo "CONTEXT COMPACTION IMMINENT"
        echo ""
        echo "Auto-capture drain and AI summarization will run at session end."
        echo "For PreCompact, call mcp__serialmemory-memory__execute_tool with:"
        echo "  tool_path: 'summarization.summarize_context'"
        echo "  arguments: {\"hours_back\": 4, \"store_summary\": true}"
        echo ""
        echo "Also ingest any critical context not yet saved via mcp__serialmemory-memory__memory_ingest."
        echo "After compaction, call mcp__serialmemory-memory__instantiate_context to restore context."
    else
        echo ""
        echo "Session Ending — auto-drain and AI summarization will run automatically via end_conversation_session."
        echo "Call mcp__serialmemory-memory__end_conversation_session to trigger auto-capture drain + summarization."
        echo "Or manually ingest key insights via mcp__serialmemory-memory__memory_ingest."
    fi
    exit 0
fi

# Extract structured summary using jq or python3
if command -v jq &>/dev/null; then
    TOTAL=$(wc -l < "$LOG_FILE" | tr -d ' ')
    FIRST_TS=$(head -1 "$LOG_FILE" | jq -r '.ts // "unknown"' 2>/dev/null || echo "unknown")
    LAST_TS=$(tail -1 "$LOG_FILE" | jq -r '.ts // "unknown"' 2>/dev/null || echo "unknown")

    FILES_CREATED=$(jq -r 'select(.tool == "Write") | .file' "$LOG_FILE" 2>/dev/null | sort -u | head -20 || echo "")
    FILES_MODIFIED=$(jq -r 'select(.tool == "Edit") | .file' "$LOG_FILE" 2>/dev/null | sort -u | head -20 || echo "")
    ERRORS=$(jq -r 'select(.result | test("error|fail|Error|Fail"; "i")) | "\(.tool): \(.result)"' "$LOG_FILE" 2>/dev/null | head -10 || echo "")
elif command -v python3 &>/dev/null; then
    read -r TOTAL FIRST_TS LAST_TS <<< "$(python3 -c "
import json, sys
lines = open('$LOG_FILE').readlines()
entries = [json.loads(l) for l in lines if l.strip()]
print(len(entries), entries[0].get('ts','?') if entries else '?', entries[-1].get('ts','?') if entries else '?')
" 2>/dev/null || echo "0 unknown unknown")"

    FILES_CREATED=$(python3 -c "
import json
for l in open('$LOG_FILE'):
    d = json.loads(l)
    if d.get('tool') == 'Write' and d.get('file'):
        print(d['file'])
" 2>/dev/null | sort -u | head -20 || echo "")

    FILES_MODIFIED=$(python3 -c "
import json
for l in open('$LOG_FILE'):
    d = json.loads(l)
    if d.get('tool') == 'Edit' and d.get('file'):
        print(d['file'])
" 2>/dev/null | sort -u | head -20 || echo "")

    ERRORS=$(python3 -c "
import json, re
for l in open('$LOG_FILE'):
    d = json.loads(l)
    r = d.get('result','')
    if re.search(r'error|fail', r, re.I):
        print(f\"{d.get('tool','?')}: {r[:200]}\")
" 2>/dev/null | head -10 || echo "")
else
    TOTAL=$(wc -l < "$LOG_FILE" | tr -d ' ')
    FIRST_TS="unknown"
    LAST_TS="unknown"
    FILES_CREATED=""
    FILES_MODIFIED=""
    ERRORS=""
fi

# Format output
echo ""
echo "AUTO-SESSION SUMMARY ($TRIGGER)"
echo "================================"
echo "Session span: $FIRST_TS to $LAST_TS"
echo "Total tool invocations: $TOTAL"

if [ -n "$FILES_CREATED" ]; then
    echo ""
    echo "Files created:"
    echo "$FILES_CREATED" | while read -r f; do [ -n "$f" ] && echo "  - $f"; done
fi

if [ -n "$FILES_MODIFIED" ]; then
    echo ""
    echo "Files modified:"
    echo "$FILES_MODIFIED" | while read -r f; do [ -n "$f" ] && echo "  - $f"; done
fi

if [ -n "$ERRORS" ]; then
    echo ""
    echo "Errors encountered:"
    echo "$ERRORS" | while read -r e; do [ -n "$e" ] && echo "  - $e"; done
fi

echo ""
if [ "$TRIGGER" = "precompact" ]; then
    echo "ACTION: Call mcp__serialmemory-memory__execute_tool with:"
    echo "  tool_path: 'summarization.summarize_context'"
    echo "  arguments: {\"hours_back\": 4, \"store_summary\": true}"
    echo ""
    echo "Also ingest any critical findings not captured above via mcp__serialmemory-memory__memory_ingest."
    echo "After compaction, call mcp__serialmemory-memory__instantiate_context to restore context."
else
    echo "ACTION: Call mcp__serialmemory-memory__end_conversation_session to trigger:"
    echo "  1. Auto-capture drain (ingests JSONL session logs as memories)"
    echo "  2. AI summarization (LLM summary stored as session_summary)"
    echo "  3. Session close"
    echo ""
    echo "Or manually save additional insights via mcp__serialmemory-memory__memory_ingest."
fi
echo ""
