#!/usr/bin/env bash
# install-hooks.sh - Install SerialMemory session capture hooks for Claude Code
# Usage: bash ops/install-hooks.sh
set -euo pipefail

HOOKS_DIR="$HOME/.claude/hooks"
SETTINGS_FILE="$HOME/.claude/settings.json"
HOOK_SCRIPT="$HOOKS_DIR/session-capture.sh"

echo "=== SerialMemory Hooks Installer ==="
echo ""

# Create hooks directory
mkdir -p "$HOOKS_DIR"

# Write the session-capture hook script
cat > "$HOOK_SCRIPT" << 'HOOKEOF'
#!/usr/bin/env bash
set -euo pipefail
EVENT="${1:-}"
SESSION_LOG="/tmp/serialmemory_session_$PPID.jsonl"

case "$EVENT" in
    session_start)
        echo '{"ts":"'"$(date -u +%FT%TZ)"'","cwd":"'"$(pwd)"'"}' > "$SESSION_LOG"
        echo "CONTEXT: Use mcp__serialmemory-memory__memory_search for relevant project context."
        echo "CONTEXT: Use mcp__serialmemory-memory__instantiate_context for session continuity."
        ;;
    post_tool_use)
        TOOL_NAME="${2:-unknown}"
        echo '{"tool":"'"$TOOL_NAME"'","ts":"'"$(date -u +%FT%TZ)"'"}' >> "$SESSION_LOG" 2>/dev/null || true
        ;;
    pre_compact)
        echo "WARNING: Context compacting! Save critical context with mcp__serialmemory-memory__memory_ingest NOW."
        echo "Include: decisions made, problems solved, architecture patterns, key file changes."
        ;;
    stop)
        if [ -f "$SESSION_LOG" ]; then
            COUNT=$(wc -l < "$SESSION_LOG" 2>/dev/null || echo "0")
            echo "SESSION COMPLETE: $COUNT tool events captured."
            echo "IMPORTANT: Use mcp__serialmemory-memory__memory_ingest to save session summary."
            echo "Include: decisions, file changes, problems solved, next steps."
            rm -f "$SESSION_LOG"
        fi
        ;;
    session_end)
        rm -f "$SESSION_LOG" 2>/dev/null || true
        ;;
esac
HOOKEOF

chmod +x "$HOOK_SCRIPT"
echo "[OK] Hook script installed: $HOOK_SCRIPT"

# Merge hooks into settings.json
HOOKS_JSON='{
  "SessionStart": [{"matcher": "*", "hooks": [{"type": "command", "command": "bash ~/.claude/hooks/session-capture.sh session_start"}]}],
  "PostToolUse": [{"matcher": "*", "hooks": [{"type": "command", "command": "bash ~/.claude/hooks/session-capture.sh post_tool_use"}]}],
  "PreCompact": [{"matcher": "*", "hooks": [{"type": "command", "command": "bash ~/.claude/hooks/session-capture.sh pre_compact"}]}],
  "Stop": [{"matcher": "*", "hooks": [{"type": "command", "command": "bash ~/.claude/hooks/session-capture.sh stop"}]}],
  "SessionEnd": [{"matcher": "*", "hooks": [{"type": "command", "command": "bash ~/.claude/hooks/session-capture.sh session_end"}]}]
}'

if [ -f "$SETTINGS_FILE" ]; then
    # Check if hooks already exist
    if python3 -c "import json; d=json.load(open('$SETTINGS_FILE')); exit(0 if 'hooks' in d else 1)" 2>/dev/null; then
        echo "[SKIP] Hooks already configured in $SETTINGS_FILE"
        echo "       To reinstall, remove the 'hooks' key first."
    else
        # Merge hooks into existing settings
        python3 -c "
import json, sys
with open('$SETTINGS_FILE') as f:
    settings = json.load(f)
settings['hooks'] = json.loads('''$HOOKS_JSON''')
with open('$SETTINGS_FILE', 'w') as f:
    json.dump(settings, f, indent=2)
print('[OK] Hooks added to $SETTINGS_FILE')
"
    fi
else
    # Create new settings file with hooks
    mkdir -p "$(dirname "$SETTINGS_FILE")"
    python3 -c "
import json
settings = {'hooks': json.loads('''$HOOKS_JSON''')}
with open('$SETTINGS_FILE', 'w') as f:
    json.dump(settings, f, indent=2)
print('[OK] Created $SETTINGS_FILE with hooks')
"
fi

echo ""
echo "=== Installation Complete ==="
echo ""
echo "Hooks installed:"
echo "  SessionStart  - Prompts for context search at session start"
echo "  PostToolUse   - Logs tool usage to session log"
echo "  PreCompact    - Warns before context compaction"
echo "  Stop          - Prompts for session summary ingest"
echo "  SessionEnd    - Cleans up session log"
echo ""
echo "Restart Claude Code to activate."
