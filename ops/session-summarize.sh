#!/usr/bin/env bash
# session-summarize.sh - Query capture status through MCP HTTP transport
# Sends JSON-RPC to MCP at localhost:4545, which forwards to the API
# The MCP client already has the API key — no extra env vars needed
# Usage: bash ops/session-summarize.sh <precompact|session_end>
set -euo pipefail

TRIGGER="${1:-session_end}"

# MCP HTTP transport (always localhost, started alongside stdio)
MCP_URL="${MCP_HTTP_URL:-http://localhost:4545}/mcp"
MCP_TOKEN="${MCP_HTTP_TOKEN:-}"

# JSON-RPC payload for capture_status tool
JSONRPC='{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"capture_status","arguments":{}}}'

# Call MCP HTTP transport
RESPONSE=""
if command -v curl &>/dev/null; then
    RESPONSE=$(curl -s -m 5 -X POST "$MCP_URL" \
        -H "Content-Type: application/json" \
        ${MCP_TOKEN:+-H "Authorization: Bearer $MCP_TOKEN"} \
        -d "$JSONRPC" 2>/dev/null || echo "")
elif command -v wget &>/dev/null; then
    RESPONSE=$(wget -q -O - --timeout=5 \
        --header="Content-Type: application/json" \
        ${MCP_TOKEN:+--header="Authorization: Bearer $MCP_TOKEN"} \
        --post-data="$JSONRPC" "$MCP_URL" 2>/dev/null || echo "")
fi

# Parse the MCP response — result.content[0].text contains the API response
TOTAL_UNDRAINED=0
SESSION_COUNT=0
if [ -n "$RESPONSE" ] && command -v jq &>/dev/null; then
    # Extract the text payload from MCP JSON-RPC response
    STATUS=$(echo "$RESPONSE" | jq -r '.result.content[0].text // ""' 2>/dev/null || echo "")
    if [ -n "$STATUS" ]; then
        TOTAL_UNDRAINED=$(echo "$STATUS" | jq -r '.totalUndrained // 0' 2>/dev/null || echo "0")
        SESSION_COUNT=$(echo "$STATUS" | jq -r '.sessions | length // 0' 2>/dev/null || echo "0")
    fi
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
