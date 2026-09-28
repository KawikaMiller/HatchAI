#!/usr/bin/env bash
# Wires HatchAI's hook into every agent CLI on this Mac.
#
# PORTED, NOT VERIFIED. Forked from Claude Buddy's tools/install-hooks.sh,
# without its crash keep-alive LaunchAgent (a Claude Buddy feature HatchAI does
# not have). HatchAI has never been run on a Mac; install-hooks.ps1 is the
# verified twin.
#
#   install-hooks.sh              # install / repair everything found
#   install-hooks.sh --uninstall  # remove just HatchAI's entries, everywhere
#
# A CLI that is not installed is skipped and said so, not treated as a failure.
# Every sub-installer merges rather than overwrites and recognises only its own
# entries, so Claude Buddy's hooks on the same machine are left alone.
#
# The last lines of output are "SUMMARY <CLI>: <outcome>", as on Windows.

set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"

FORWARD=()
UNINSTALL=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) FORWARD+=(--uninstall); UNINSTALL=1; shift ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    *)
      echo "unknown option: $1" >&2
      echo "This takes --uninstall and nothing else; run a per-CLI installer for more." >&2
      exit 2
      ;;
  esac
done

have_claude_code() { [[ -d "$HOME/.claude" ]] || command -v claude >/dev/null 2>&1; }
have_codex() { [[ -d "${CODEX_HOME:-$HOME/.codex}" ]] || command -v codex >/dev/null 2>&1; }
have_grok() { [[ -d "${GROK_HOME:-$HOME/.grok}" ]] || command -v grok >/dev/null 2>&1; }

summary=()
failed=0

run_one() {
  local label="$1" script="$2"
  local path="$HERE/$script"
  if [[ ! -f "$path" ]]; then
    summary+=("$label: failed (couldn't find $script)"); failed=1; return
  fi

  echo "=== $label"
  if bash "$path" "${FORWARD[@]+"${FORWARD[@]}"}"; then
    if [[ $UNINSTALL -eq 1 ]]; then summary+=("$label: removed"); else summary+=("$label: wired"); fi
  else
    summary+=("$label: failed"); failed=1
  fi
  echo
}

if have_claude_code; then run_one "Claude Code" install-macos-hooks.sh
else summary+=("Claude Code: not installed, skipped"); fi

if have_codex; then run_one "Codex" install-codex-hooks.sh
else summary+=("Codex: not installed, skipped"); fi

if have_grok; then run_one "Grok Build" install-grok-hooks.sh
else summary+=("Grok Build: not installed, skipped"); fi

for line in "${summary[@]}"; do echo "SUMMARY $line"; done

exit $failed
