#!/usr/bin/env bash
# Installs HatchAI's hook into Claude Code's macOS settings.
#
# PORTED, NOT VERIFIED. Forked from Claude Buddy's tools/install-macos-hooks.sh;
# HatchAI has never been built or run on a Mac, so nothing here has run on one.
# The Windows installer (install-windows-hooks.ps1) is the verified one.
#
#   install-macos-hooks.sh              # install / repair
#   install-macos-hooks.sh --uninstall  # remove just HatchAI's entries
#   install-macos-hooks.sh --settings /scratch/settings.json --hook-dir /scratch/hook
#
# It MERGES, never overwrites: an entry is HatchAI's if its command names
# HatchAIHook.sh, and nothing else in settings.json is touched -- Claude Buddy's
# entries (which name ClaudeBuddyHook.sh) included. Re-running strips and
# re-adds only ours, so it converges rather than duplicating.
#
# Changes from the source, besides the names: a group that held none of our
# entries is left exactly as it was (the source dropped any group left empty
# after filtering, including one that was empty and not its own); the backup is
# settings.json.hatchai-backup, so it can never overwrite Claude Buddy's; extra
# profiles come from HatchAI's own settings; and HATCHAI_INSTALLER_SANDBOX, when
# set, refuses any write outside it.
#
# Extra Claude Code accounts (CLAUDE_CONFIG_DIR=~/.claude-work claude) are wired
# too: every name in HatchAI's claudeCodeProfileDirs setting, or --profile-dir.

set -euo pipefail

UNINSTALL=0
NO_PROFILES=0
EXTRA_PROFILES=()
HOOK_DIR="$HOME/.claude/hatchai"
SETTINGS="$HOME/.claude/settings.json"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) UNINSTALL=1; shift ;;
    --settings) SETTINGS="$2"; shift 2 ;;
    --profile-dir) EXTRA_PROFILES+=("$2"); shift 2 ;;
    --no-profiles) NO_PROFILES=1; shift ;;
    --hook-dir) HOOK_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

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

# The literal string $HOME for the default location, so settings.json stays
# portable across machines; a custom --hook-dir gets its real path.
if [[ "$HOOK_DIR" == "$HOME/.claude/hatchai" ]]; then
  CONFIGURED='$HOME/.claude/hatchai/HatchAIHook.sh'
else
  CONFIGURED="$INSTALLED"
fi

in_sandbox "$SETTINGS"

if [[ $UNINSTALL -eq 0 ]]; then
  if [[ ! -f "$SOURCE" ]]; then
    echo "Can't find HatchAIHook.sh next to $HERE." >&2
    exit 1
  fi
  in_sandbox "$INSTALLED"
  mkdir -p "$HOOK_DIR"
  cp "$SOURCE" "$INSTALLED"
  chmod +x "$INSTALLED"
  echo "Hook installed: $INSTALLED"
elif [[ ! -f "$SETTINGS" ]]; then
  echo "No $SETTINGS, so nothing to remove."
  exit 0
fi

if [[ ! -f "$SETTINGS" ]]; then
  mkdir -p "$(dirname "$SETTINGS")"
  printf '{}' > "$SETTINGS"
  echo "Created $SETTINGS"
fi

BACKUP="$SETTINGS.hatchai-backup"
cp "$SETTINGS" "$BACKUP"
echo "Backed up settings to $BACKUP"

# The JSON surgery runs in JavaScript for Automation, for the source's reason:
# a clean Mac has no guaranteed jq and /usr/bin/python3 is a stub, but
# osascript has a real JSON parser since 10.10.
JXA=$(cat <<'JAVASCRIPT'
ObjC.import('Foundation');

function readUtf8(path) {
  const s = $.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null);
  return s.isNil() ? '' : ObjC.unwrap(s);
}

function run(argv) {
  const settingsPath = argv[0];
  const uninstall = argv[1] === 'uninstall';
  const script = '"' + argv[2] + '"';
  const marker = 'HatchAIHook.sh';

  const raw = readUtf8(settingsPath).trim();
  const settings = raw === '' ? {} : JSON.parse(raw);
  const hadHooks = Object.prototype.hasOwnProperty.call(settings, 'hooks');

  if (hadHooks && settings.hooks !== null && typeof settings.hooks !== 'object') {
    throw new Error('the "hooks" value is not an object; not safe to edit');
  }
  let hooks = (settings.hooks && typeof settings.hooks === 'object') ? settings.hooks : {};

  const wanted = [
    { event: 'SessionStart',     matcher: null,                  state: 'idle' },
    { event: 'UserPromptSubmit', matcher: null,                  state: 'generating' },
    { event: 'PreToolUse',       matcher: '.*',                  state: 'generating' },
    { event: 'Stop',             matcher: null,                  state: 'idle' },
    { event: 'SessionEnd',       matcher: null,                  state: 'ended' },
    { event: 'Notification',     matcher: 'permission_prompt',    state: 'waiting' },
    { event: 'Notification',     matcher: 'elicitation_dialog',   state: 'waiting' },
    { event: 'Notification',     matcher: 'elicitation_complete', state: 'generating' }
  ];

  const isOurs = function (h) {
    return h && typeof h.command === 'string' && h.command.indexOf(marker) !== -1;
  };

  let removedAny = false;
  for (const name of Object.keys(hooks)) {
    if (!Array.isArray(hooks[name])) continue;
    const kept = [];
    let changed = false;

    for (const group of hooks[name]) {
      if (!group || typeof group !== 'object' || !Array.isArray(group.hooks)) { kept.push(group); continue; }
      const inner = group.hooks.filter(function (h) { return !isOurs(h); });
      if (inner.length === group.hooks.length) { kept.push(group); continue; }
      changed = true;
      if (inner.length > 0) { group.hooks = inner; kept.push(group); }
    }

    if (changed) {
      removedAny = true;
      if (kept.length > 0) { hooks[name] = kept; } else { delete hooks[name]; }
    }
  }

  if (!uninstall) {
    for (const entry of wanted) {
      const group = {};
      if (entry.matcher) { group.matcher = entry.matcher; }
      group.hooks = [{ type: 'command', command: 'bash ' + script + ' ' + entry.state }];
      hooks[entry.event] = [].concat(hooks[entry.event] || []).concat([group]);
    }
  }

  settings.hooks = hooks;
  if (Object.keys(hooks).length === 0 && (removedAny || !hadHooks)) { delete settings.hooks; }

  return JSON.stringify(settings, null, 2);
}
JAVASCRIPT
)

MODE=$([[ $UNINSTALL -eq 1 ]] && echo uninstall || echo install)

TMP="$(mktemp "${TMPDIR:-/tmp}/hatchai-settings.XXXXXX")"
trap 'rm -f "$TMP"' EXIT

osascript -l JavaScript -e "$JXA" "$SETTINGS" "$MODE" "$CONFIGURED" > "$TMP"

if ! osascript -l JavaScript -e 'ObjC.import("Foundation"); function run(a){ JSON.parse(ObjC.unwrap($.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null))); return "ok" }' "$TMP" >/dev/null 2>&1; then
  echo "Refusing to write: generated settings.json did not parse. Left $SETTINGS untouched." >&2
  echo "Your backup is at $BACKUP" >&2
  exit 1
fi

mv "$TMP" "$SETTINGS"
trap - EXIT

if [[ $UNINSTALL -eq 1 ]]; then
  echo "Removed HatchAI hooks from $SETTINGS."
  echo "The installed hook script was left in place; delete $HOOK_DIR if you want it gone."
else
  echo "Wired 8 hook entries into $SETTINGS"
  echo
  echo "Restart any running Claude Code sessions: hooks are read at session start."
fi

# --- extra accounts -----------------------------------------------------------

# Names saved in HatchAI's own settings. This file does not follow HOME:
# SpecialFolder.ApplicationData resolves through the OS.
saved_profiles() {
  local settings="${HATCHAI_SETTINGS_DIR:-$HOME/Library/Application Support/HatchAI}/settings.json"
  [[ -f "$settings" ]] || return 0

  osascript -l JavaScript -e '
    ObjC.import("Foundation");
    function run(a) {
      const s = $.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null);
      if (s.isNil()) return "";
      let parsed;
      try { parsed = JSON.parse(ObjC.unwrap(s)); } catch (e) { return ""; }
      const dirs = parsed.claudeCodeProfileDirs;
      if (!Array.isArray(dirs)) return "";
      return dirs.filter(function (d) { return typeof d === "string" && d.length > 0; }).join("\n");
    }' "$settings" 2>/dev/null || true
}

if [[ $NO_PROFILES -eq 0 ]]; then
  profiles=()
  if [[ ${#EXTRA_PROFILES[@]} -gt 0 ]]; then
    profiles=("${EXTRA_PROFILES[@]}")
  else
    while IFS= read -r line; do
      [[ -n "$line" ]] && profiles+=("$line")
    done < <(saved_profiles)
  fi

  for profile in "${profiles[@]+"${profiles[@]}"}"; do
    if [[ "$profile" == */* ]]; then
      echo "Skipping profile '$profile': expected a directory name under \$HOME, not a path." >&2
      continue
    fi

    echo
    echo "--- profile: $profile"

    # An array, not ${UNINSTALL:+--uninstall}: UNINSTALL is 0 or 1 and "0" is
    # non-empty, so :+ would unwire every profile an install meant to wire.
    mode=()
    [[ $UNINSTALL -eq 1 ]] && mode=(--uninstall)

    "$0" "${mode[@]+"${mode[@]}"}" --no-profiles \
         --settings "$HOME/$profile/settings.json" \
         --hook-dir "$HOOK_DIR"
  done
fi
