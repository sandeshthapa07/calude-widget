#!/bin/sh
# Claude Widget hook for macOS and Linux, called by Claude Code (see `install`).
#   claude-widget-hook.sh hook        PreToolUse / PostToolUse / ...: track which tool is running
#   claude-widget-hook.sh statusline  save what Claude sends (plan limits, context), print a line
# Only POSIX sh, grep, sed, tr: no jq, node or python needed. Prints nothing for hooks
# (the output of some hooks is fed back to Claude). Always exits 0.

base="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/widget"
input=$(cat)

# First "key":"value" pair in the JSON (Claude sends compact, single-line JSON).
field() {
  printf '%s' "$input" | grep -o "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -n 1 |
    sed 's/^.*:[[:space:]]*"\(.*\)"$/\1/'
}
safe() { printf '%s' "$1" | tr -cd 'A-Za-z0-9_-' | cut -c1-100; }

sid=$(safe "$(field session_id)")
[ -n "$sid" ] || exit 0

case "$1" in
  hook)
    dir="$base/live/$sid"
    id=$(safe "$(field tool_use_id)")
    case "$(field hook_event_name)" in
      PreToolUse)
        # A PostToolUse that ran first (async hooks can start out of order) wins.
        [ -n "$id" ] && [ ! -e "$dir/$id.done" ] || exit 0
        mkdir -p "$dir" && printf '%s' "$input" > "$dir/$id.tmp$$" && mv -f "$dir/$id.tmp$$" "$dir/$id.run"
        ;;
      PostToolUse|PostToolUseFailure)
        [ -n "$id" ] || exit 0
        mkdir -p "$dir" && : > "$dir/$id.done" && rm -f "$dir/$id.run"
        ;;
      UserPromptSubmit|Stop)
        rm -f "$dir"/* 2>/dev/null
        ;;
      SessionEnd)
        rm -rf "$dir"
        ;;
    esac
    ;;
  statusline)
    dir="$base/statusline"
    mkdir -p "$dir" && printf '%s' "$input" > "$dir/$sid.json.tmp$$" && mv -f "$dir/$sid.json.tmp$$" "$dir/$sid.json"
    # The widget app turns each capture into a ready line (<session>.txt).
    if [ -f "$dir/$sid.txt" ]; then cat "$dir/$sid.txt"; else field display_name; fi
    ;;
esac
exit 0
