#!/usr/bin/env bash
# install-hooks.sh - Install SerialMemory Claude Code hooks
# Usage: bash ops/install-hooks.sh
#
# Installs session lifecycle hooks and MCP tool status indicators for ALL tools.
# Covers all 40+ SerialMemory MCP tools from ToolHierarchy.cs.
# Uses jq for JSON manipulation (falls back to python3).
# Safe to re-run: overwrites hooks, preserves all other settings.
set -euo pipefail

SETTINGS_FILE="$HOME/.claude/settings.json"

echo "=== SerialMemory Hooks Installer ==="
echo ""

# Check for jq or python3
if command -v jq &>/dev/null; then
    JSON_TOOL="jq"
elif command -v python3 &>/dev/null; then
    JSON_TOOL="python3"
else
    echo "[ERROR] Neither jq nor python3 found. Install one and retry."
    exit 1
fi
echo "[OK] Using $JSON_TOOL for JSON manipulation"

# Complete hooks configuration covering all SerialMemory MCP tools.
# Tool prefix: mcp__serialmemory-memory__ (matches MCP server name "serialmemory-memory")
# Core tools (8) are directly exposed; gateway tools (38) go through execute_tool.
read -r -d '' HOOKS_JSON << 'HOOKS_EOF' || true
{
  "SessionStart": [
    {
      "matcher": "compact",
      "hooks": [
        {
          "type": "command",
          "command": "echo && echo 'POST-COMPACTION CONTEXT RELOAD' && echo && echo 'REQUIRED: Call these MCP tools to restore context:' && echo '  1. mcp__serialmemory-memory__instantiate_context with project_or_subject for the current project' && echo '  2. mcp__serialmemory-memory__memory_search_index for token-efficient search (then memory_fetch for details)' && echo && echo 'Progressive disclosure workflow: search_index → timeline → fetch (saves ~10x tokens)' && echo"
        }
      ]
    },
    {
      "matcher": "startup|resume|clear",
      "hooks": [
        {
          "type": "command",
          "command": "echo 'CLAUDE: Call mcp__serialmemory-memory__initialise_conversation_session and mcp__serialmemory-memory__instantiate_context for project context. For token-efficient search use memory_search_index → memory_timeline → memory_fetch workflow.'"
        }
      ]
    }
  ],
  "UserPromptSubmit": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "command",
          "command": "cat > /dev/null; echo 'CONTEXT: Use mcp__serialmemory-memory__memory_search to find relevant context before answering.'"
        }
      ]
    }
  ],
  "SessionEnd": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "command",
          "command": "bash ~/Projects/SerialMemoryServer/ops/session-summarize.sh session_end"
        }
      ]
    }
  ],
  "PreCompact": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "command",
          "command": "bash ~/Projects/SerialMemoryServer/ops/session-summarize.sh precompact"
        }
      ]
    }
  ],
  "PreToolUse": [
    { "matcher": "mcp__serialmemory-memory__memory_search",       "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Searching memory...'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_ingest",        "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Ingesting to memory...'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_about_user",    "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Loading user persona...'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_multi_hop_search", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Multi-hop memory search...'" }] },
    { "matcher": "mcp__serialmemory-memory__initialise_conversation_session", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Starting session...'" }] },
    { "matcher": "mcp__serialmemory-memory__end_conversation_session", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Ending session...'" }] },
    { "matcher": "mcp__serialmemory-memory__instantiate_context",  "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Loading context...'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_search_index",  "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Compact search...'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_timeline",      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Loading timeline...'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_fetch",         "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Fetching memories...'" }] },
    { "matcher": "mcp__serialmemory-memory__get_tools_in_category", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Browsing tools...'" }] },
    { "matcher": "mcp__serialmemory-memory__execute_tool",         "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Executing tool...'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_lineage",       "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Tracing memory lineage...'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_trace",         "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Tracing memory...'" }] },
    { "matcher": "mcp__serialmemory-memory__drain_session_captures", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Draining captures...'" }] },
    { "matcher": "mcp__serialmemory-memory__capture_status",       "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Checking captures...'" }] },
    { "matcher": "mcp__serialmemory-memory__goal_set",             "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Setting goal...'" }] },
    { "matcher": "mcp__serialmemory-memory__goal_list",            "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Listing goals...'" }] },
    { "matcher": "mcp__serialmemory-memory__goal_complete",        "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Completing goal...'" }] },
    { "matcher": "mcp__serialmemory-memory__summarize_session",    "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Summarizing session...'" }] },
    { "matcher": "mcp__serialmemory-memory__summarize_context",    "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Summarizing context...'" }] },
    { "matcher": "Write", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Writing file...'" }] },
    { "matcher": "Edit",  "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Editing file...'" }] },
    { "matcher": "Bash",  "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Running command...'" }] }
  ],
  "PostToolUse": [
    { "matcher": "mcp__serialmemory-memory__memory_search",       "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Search complete'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_ingest",        "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Memory ingested'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_about_user",    "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Persona loaded'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_multi_hop_search", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Multi-hop complete'" }] },
    { "matcher": "mcp__serialmemory-memory__initialise_conversation_session", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Session started'" }] },
    { "matcher": "mcp__serialmemory-memory__end_conversation_session", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Session ended'" }] },
    { "matcher": "mcp__serialmemory-memory__instantiate_context",  "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Context loaded'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_search_index",  "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Index search complete'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_timeline",      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Timeline loaded'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_fetch",         "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Memories fetched'" }] },
    { "matcher": "mcp__serialmemory-memory__get_tools_in_category", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Tools listed'" }] },
    { "matcher": "mcp__serialmemory-memory__execute_tool",         "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Tool executed'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_lineage",       "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Lineage traced'" }] },
    { "matcher": "mcp__serialmemory-memory__memory_trace",         "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Memory traced'" }] },
    { "matcher": "mcp__serialmemory-memory__drain_session_captures", "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Captures drained'" }] },
    { "matcher": "mcp__serialmemory-memory__capture_status",       "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Capture status loaded'" }] },
    { "matcher": "mcp__serialmemory-memory__goal_set",             "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Goal set'" }] },
    { "matcher": "mcp__serialmemory-memory__goal_list",            "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Goals loaded'" }] },
    { "matcher": "mcp__serialmemory-memory__goal_complete",        "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Goal completed'" }] },
    { "matcher": "mcp__serialmemory-memory__summarize_session",    "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Session summarized'" }] },
    { "matcher": "mcp__serialmemory-memory__summarize_context",    "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Context summarized'" }] },
    { "matcher": "Write", "hooks": [{ "type": "command", "command": "bash ~/Projects/SerialMemoryServer/ops/session-capture.sh" }] },
    { "matcher": "Edit",  "hooks": [{ "type": "command", "command": "bash ~/Projects/SerialMemoryServer/ops/session-capture.sh" }] },
    { "matcher": "Bash",  "hooks": [{ "type": "command", "command": "bash ~/Projects/SerialMemoryServer/ops/session-capture.sh" }] }
  ],
  "Stop": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "command",
          "command": "cat > /dev/null; echo 'Response complete'"
        }
      ]
    }
  ],
  "SubagentStop": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "command",
          "command": "cat > /dev/null; echo 'Subagent task complete'"
        }
      ]
    }
  ]
}
HOOKS_EOF

mkdir -p "$(dirname "$SETTINGS_FILE")"

merge_with_jq() {
    local hooks_json="$1"
    if [ -f "$SETTINGS_FILE" ]; then
        local existing
        existing=$(cat "$SETTINGS_FILE")
        echo "$existing" | jq --argjson hooks "$hooks_json" '.hooks = $hooks' > "$SETTINGS_FILE.tmp"
        mv "$SETTINGS_FILE.tmp" "$SETTINGS_FILE"
        echo "[OK] Hooks merged into $SETTINGS_FILE"
    else
        echo "$hooks_json" | jq '{hooks: .}' > "$SETTINGS_FILE"
        echo "[OK] Created $SETTINGS_FILE with hooks"
    fi
}

merge_with_python() {
    local hooks_json="$1"
    python3 -c "
import json, sys

hooks = json.loads(sys.argv[1])
settings_path = sys.argv[2]

try:
    with open(settings_path) as f:
        settings = json.load(f)
    print('[OK] Merging hooks into existing settings')
except FileNotFoundError:
    settings = {}
    print('[OK] Creating new settings file')

settings['hooks'] = hooks

with open(settings_path, 'w') as f:
    json.dump(settings, f, indent=2)
    f.write('\n')

print(f'[OK] Hooks written to {settings_path}')
" "$hooks_json" "$SETTINGS_FILE"
}

if [ "$JSON_TOOL" = "jq" ]; then
    merge_with_jq "$HOOKS_JSON"
else
    merge_with_python "$HOOKS_JSON"
fi

echo ""
echo "=== Installation Complete ==="
echo ""
echo "Hooks installed:"
echo "  SessionStart      - Context reload prompts (compact / startup)"
echo "  UserPromptSubmit  - Context search reminder"
echo "  PreCompact        - Auto-summarize session + save to memory"
echo "  SessionEnd        - Auto-summarize session + save to memory"
echo "  PreToolUse        - Status indicators for 12 MCP tools + Write/Edit/Bash"
echo "  PostToolUse       - Completion indicators for MCP tools + session capture (Write/Edit/Bash)"
echo "  Stop              - Response complete indicator"
echo "  SubagentStop      - Subagent completion indicator"
echo ""
echo "MCP tool prefix: mcp__serialmemory-memory__"
echo "Tool coverage: 23 matchers (8 core + 3 disclosure + 3 goals + 2 captures + 2 summarization + 5 meta/observability)"
echo "Progressive disclosure: memory_search_index, memory_timeline, memory_fetch (saves ~10x tokens)"
echo "Captures: POST to HTTP API (no local filesystem) — drain_session_captures, capture_status"
echo "Gateway tools (50+) are covered by the execute_tool matcher."
echo ""
echo "Note: This only installs SerialMemory hooks."
echo "      Your existing non-hook settings are preserved."
echo ""
echo "Restart Claude Code to activate."
