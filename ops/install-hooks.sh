#!/usr/bin/env bash
# install-hooks.sh - Install SerialMemory Claude Code hooks
# Usage: bash ops/install-hooks.sh
#
# Installs session lifecycle hooks and MCP tool status indicators.
# Uses jq for JSON manipulation (falls back to python3).
# Safe to re-run: merges hooks into existing settings without overwriting other config.
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

# The hooks configuration matching the current production setup
# Tool prefix: mcp__serialmemory__ (MCP server name from settings)
read -r -d '' HOOKS_JSON << 'HOOKS_EOF' || true
{
  "SessionStart": [
    {
      "matcher": "compact",
      "hooks": [
        {
          "type": "command",
          "command": "echo && echo 'POST-COMPACTION CONTEXT RELOAD' && echo && echo 'REQUIRED: Call these MCP tools to restore context:' && echo '  1. mcp__serialmemory__instantiate_context with project_or_subject for the current project' && echo '  2. mcp__serialmemory__memory_search for specific topics if needed' && echo && echo 'This will reload pre-compaction findings from SerialMemory.' && echo"
        }
      ]
    },
    {
      "matcher": "startup|resume|clear",
      "hooks": [
        {
          "type": "command",
          "command": "echo 'CLAUDE: Call mcp__serialmemory__initialise_conversation_session and mcp__serialmemory__instantiate_context for project context'"
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
          "command": "echo && echo 'Session Ending' && echo 'Use mcp__serialmemory__memory_ingest to save session insights.' && echo 'Include: decisions made, problems solved, code changes, next steps.' && echo"
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
          "command": "echo && echo 'CONTEXT COMPACTION IMMINENT - INGEST SESSION FINDINGS NOW' && echo && echo 'REQUIRED: Before context is compacted, call mcp__serialmemory__memory_ingest with:' && echo '  - Project name and current task' && echo '  - Key discoveries and root causes found' && echo '  - Code changes made and files modified' && echo '  - Decisions made and rationale' && echo '  - Current state and blockers' && echo '  - Next steps to continue after compaction' && echo && echo 'Format: Single comprehensive memory covering the session work.' && echo"
        }
      ]
    }
  ],
  "PreToolUse": [
    {
      "matcher": "mcp__serialmemory__memory_search",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Searching memory...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_ingest",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Ingesting to memory...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_multi_hop_search",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Multi-hop memory search...'" }]
    },
    {
      "matcher": "mcp__serialmemory__detect_contradictions",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Detecting contradictions...'" }]
    },
    {
      "matcher": "mcp__serialmemory__detect_hallucinations",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Detecting hallucinations...'" }]
    },
    {
      "matcher": "mcp__serialmemory__engineering_analyze",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Engineering analysis...'" }]
    },
    {
      "matcher": "mcp__serialmemory__engineering_reason",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Multi-model reasoning...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_update",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Updating memory...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_delete",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Deleting memory...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_merge",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Merging memories...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_split",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Splitting memory...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_reinforce",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Reinforcing memory...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_decay",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Applying decay...'" }]
    },
    {
      "matcher": "mcp__serialmemory__export_workspace",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Exporting workspace...'" }]
    },
    {
      "matcher": "mcp__serialmemory__export_memories",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Exporting memories...'" }]
    },
    {
      "matcher": "mcp__serialmemory__export_graph",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Exporting graph...'" }]
    },
    {
      "matcher": "mcp__serialmemory__get_graph_statistics",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Getting graph stats...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_trace",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Tracing memory events...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_lineage",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Tracing memory lineage...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_explain",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Explaining memory state...'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_conflicts",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Checking conflicts...'" }]
    },
    {
      "matcher": "mcp__serialmemory__verify_memory_integrity",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Verifying integrity...'" }]
    },
    {
      "matcher": "mcp__serialmemory__scan_loops",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Scanning for loops...'" }]
    },
    {
      "matcher": "Write",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Writing file...'" }]
    },
    {
      "matcher": "Edit",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Editing file...'" }]
    },
    {
      "matcher": "Bash",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Running command...'" }]
    }
  ],
  "PostToolUse": [
    {
      "matcher": "mcp__serialmemory__memory_search",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Search complete'" }]
    },
    {
      "matcher": "mcp__serialmemory__memory_ingest",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Memory ingested'" }]
    },
    {
      "matcher": "mcp__serialmemory__detect_contradictions",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Contradiction check complete'" }]
    },
    {
      "matcher": "mcp__serialmemory__detect_hallucinations",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'Hallucination check complete'" }]
    },
    {
      "matcher": "Write",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'File written'" }]
    },
    {
      "matcher": "Edit",
      "hooks": [{ "type": "command", "command": "cat > /dev/null; echo 'File edited'" }]
    }
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
echo "  SessionStart  - Context reload prompts (compact / startup)"
echo "  PreCompact    - Save critical context before compaction"
echo "  SessionEnd    - Prompt to save session insights"
echo "  PreToolUse    - Status indicators for MCP tools + Write/Edit/Bash"
echo "  PostToolUse   - Completion indicators for MCP tools + Write/Edit"
echo "  Stop          - Response complete indicator"
echo "  SubagentStop  - Subagent completion indicator"
echo ""
echo "MCP tool prefix: mcp__serialmemory__"
echo ""
echo "Note: This only installs SerialMemory hooks."
echo "      Your existing non-hook settings are preserved."
echo ""
echo "Restart Claude Code to activate."
