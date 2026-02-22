#!/usr/bin/env bash
# session-summarize.sh - Summarize session activity via SerialMemory HTTP API
# Reads capture status from API and outputs structured summary text
# Usage: bash ops/session-summarize.sh <precompact|session_end>
set -euo pipefail

TRIGGER="${1:-session_end}"

# SerialMemory API endpoint
API_URL="${SERIALMEMORY_ENDPOINT:-http://localhost:5000}"
API_KEY="${SERIALMEMORY_API_KEY:-}"

# Build auth header
AUTH_OPTS=""
if [ -n "$API_KEY" ]; then
    AUTH_OPTS="-H \"Authorization: Bearer $API_KEY\""
fi

# Fetch capture status from API
STATUS=""
if command -v curl &>/dev/null; then
    STATUS=$(curl -s -m 5 "${API_URL}/api/captures/status" \
        -H "Accept: application/json" \
        ${API_KEY:+-H "Authorization: Bearer $API_KEY"} 2>/dev/null || echo "")
elif command -v wget &>/dev/null; then
    STATUS=$(wget -q -O - --timeout=5 \
        --header="Accept: application/json" \
        ${API_KEY:+--header="Authorization: Bearer $API_KEY"} \
        "${API_URL}/api/captures/status" 2>/dev/null || echo "")
fi

# Parse capture status
TOTAL_UNDRAINED=0
SESSION_COUNT=0
if [ -n "$STATUS" ] && command -v jq &>/dev/null; then
    TOTAL_UNDRAINED=$(echo "$STATUS" | jq -r '.totalUndrained // 0' 2>/dev/null || echo "0")
    SESSION_COUNT=$(echo "$STATUS" | jq -r '.sessions | length // 0' 2>/dev/null || echo "0")
fi

echo ""
echo "AUTO-SESSION SUMMARY ($TRIGGER)"
echo "================================"

if [ "$TOTAL_UNDRAINED" -gt 0 ]; then
    echo "Undrained captures: $TOTAL_UNDRAINED entries across $SESSION_COUNT session(s)"
    echo ""

    if command -v jq &>/dev/null && [ -n "$STATUS" ]; then
        echo "Sessions with pending captures:"
        echo "$STATUS" | jq -r '.sessions[]? | "  - \(.sessionId // "unknown"): \(.entryCount) entries (\(.firstTs // "?") to \(.lastTs // "?"))"' 2>/dev/null || true
    fi
else
    echo "No pending captures in buffer."
fi

echo ""
if [ "$TRIGGER" = "precompact" ]; then
    echo "CONTEXT COMPACTION IMMINENT"
    echo ""
    if [ "$TOTAL_UNDRAINED" -gt 0 ]; then
        echo "ACTION: Call mcp__serialmemory-memory__drain_session_captures to flush $TOTAL_UNDRAINED entries."
    fi
    echo "ACTION: Call mcp__serialmemory-memory__execute_tool with:"
    echo "  tool_path: 'summarization.summarize_context'"
    echo "  arguments: {\"hours_back\": 4, \"store_summary\": true}"
    echo ""
    echo "Also ingest any critical context not yet saved via mcp__serialmemory-memory__memory_ingest."
    echo "After compaction, call mcp__serialmemory-memory__instantiate_context to restore context."
else
    echo "ACTION: Call mcp__serialmemory-memory__end_conversation_session to trigger:"
    if [ "$TOTAL_UNDRAINED" -gt 0 ]; then
        echo "  1. Drain $TOTAL_UNDRAINED capture entries into memories"
    fi
    echo "  2. AI summarization (LLM summary stored as session_summary)"
    echo "  3. Session close"
    echo ""
    echo "Or call mcp__serialmemory-memory__drain_session_captures first, then end session."
    echo "Or manually save additional insights via mcp__serialmemory-memory__memory_ingest."
fi
echo ""
