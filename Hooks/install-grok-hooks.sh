#!/usr/bin/env bash
# Installs HatchAI's hook into Grok Build's macOS hook config.
#
# PORTED, NOT VERIFIED. Forked from Claude Buddy's tools/install-grok-hooks.sh;
# HatchAI has never been run on a Mac.
#
# Grok discovers global hooks from $GROK_HOME/hooks/*.json and always trusts
# them, so HatchAI writes a file of its own, hooks/hatchai.json, and never
# opens anybody else's. Re-running rewrites only that file.
#
#   install-grok-hooks.sh              # install / repair
#   install-grok-hooks.sh --uninstall  # remove just HatchAI's file
#   install-grok-hooks.sh --profile-dir .grok-work   # a second GROK_HOME too
#
# One fix over the source: it passed ${UNINSTALL:+--uninstall} to each extra
# profile, and UNINSTALL is 0 or 1 -- both non-empty -- so an install removed
# the hooks from every extra profile it was meant to wire. The source's two
# sibling installers had already found and fixed that exact mistake.
# HatchAI has no saved list of extra Grok homes, so only --profile-dir adds one.

set -euo pipefail

UNINSTALL=0
NO_PROFILES=0
EXTRA_PROFILES=()
GROK_DIR="${GROK_HOME:-$HOME/.grok}"
HOOK_DIR=""
HOOKS_FILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) UNINSTALL=1; shift ;;
    --grok-home) GROK_DIR="$2"; shift 2 ;;
    --profile-dir) EXTRA_PROFILES+=("$2"); shift 2 ;;
    --no-profiles) NO_PROFILES=1; shift ;;
    --hook-dir) HOOK_DIR="$2"; shift 2 ;;
    --hooks-file) HOOKS_FILE="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$HOOK_DIR" ]] || HOOK_DIR="$GROK_DIR/hatchai"
[[ -n "$HOOKS_FILE" ]] || HOOKS_FILE="$GROK_DIR/hooks/hatchai.json"

HERE="$(cd "$(dirname "$0")" && pwd)"

in_sandbox() {
  [[ -z "${HATCHAI_INSTALLER_SANDBOX:-}" ]] && return 0
  case "$1" in
    "${HATCHAI_INSTALLER_SANDBOX%/}"/*) return 0 ;;
    *) echo "HATCHAI_INSTALLER_SANDBOX is set and $1 is outside it. Refusing to write." >&2; exit 1 ;;
  esac
}

SOURCE="$HERE/HatchAIHook.sh"
INSTALLED="$HOOK_DIR/HatchAIHook.sh"

if [[ "$HOOK_DIR" == "$HOME/.grok/hatchai" ]]; then
  CONFIGURED='$HOME/.grok/hatchai/HatchAIHook.sh'
else
  CONFIGURED="$INSTALLED"
fi

in_sandbox "$HOOKS_FILE"

if [[ $UNINSTALL -eq 1 ]]; then
  rm -f "$HOOKS_FILE"
  echo "Removed HatchAI hooks from $HOOKS_FILE."
  echo "The installed hook script was left in place; delete $HOOK_DIR if you want it gone."
else
  if [[ ! -f "$SOURCE" ]]; then
    echo "Can't find HatchAIHook.sh next to $HERE." >&2
    exit 1
  fi
  in_sandbox "$INSTALLED"
  mkdir -p "$HOOK_DIR"
  cp "$SOURCE" "$INSTALLED"
  chmod +x "$INSTALLED"
  echo "Hook installed: $INSTALLED"

  mkdir -p "$(dirname "$HOOKS_FILE")"
  # timeout is explicit because Grok's default for observe hooks is 5 seconds.
  cat > "$HOOKS_FILE" <<EOF
{
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok idle", "timeout": 15 } ] }
    ],
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok generating", "timeout": 15 } ] }
    ],
    "PreToolUse": [
      { "matcher": ".*", "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok generating", "timeout": 15 } ] }
    ],
    "Notification": [
      { "matcher": "permission_prompt", "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok waiting", "timeout": 15 } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok idle", "timeout": 15 } ] }
    ],
    "SessionEnd": [
      { "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok ended", "timeout": 15 } ] }
    ]
  }
}
EOF
  echo "Wired HatchAI hooks into $HOOKS_FILE"
  echo
  echo "Restart any running Grok sessions: hooks are read at session start."
fi

[[ $NO_PROFILES -eq 1 ]] && exit 0

mode=()
[[ $UNINSTALL -eq 1 ]] && mode=(--uninstall)

for name in "${EXTRA_PROFILES[@]+"${EXTRA_PROFILES[@]}"}"; do
  [[ -n "$name" ]] || continue
  echo
  echo "=== extra Grok home: $HOME/$name"
  "$0" "${mode[@]+"${mode[@]}"}" --grok-home "$HOME/$name" --no-profiles
done
