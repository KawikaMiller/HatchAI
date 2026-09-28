#!/usr/bin/env bash
# Installs HatchAI's hook into Codex's macOS hook config ($CODEX_HOME/hooks.json).
#
# PORTED, NOT VERIFIED. Forked from Claude Buddy's tools/install-codex-hooks.sh;
# HatchAI has never been run on a Mac. See install-codex-hooks.ps1 for the
# three Codex-specific differences (own file, PermissionRequest instead of
# Notification, async on that event), which are the source's measurements.
#
#   install-codex-hooks.sh              # install / repair
#   install-codex-hooks.sh --uninstall  # remove just HatchAI's entries
#
# Merges, never overwrites: entries naming HatchAIHook.sh are ours, and nothing
# else is touched. Extra Codex homes come from HatchAI's own codexHomes setting,
# or --profile-dir.

set -euo pipefail

UNINSTALL=0
NO_PROFILES=0
EXTRA_PROFILES=()
CODEX_DIR="${CODEX_HOME:-$HOME/.codex}"
HOOK_DIR=""
HOOKS_JSON=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) UNINSTALL=1; shift ;;
    --codex-home) CODEX_DIR="$2"; shift 2 ;;
    --profile-dir) EXTRA_PROFILES+=("$2"); shift 2 ;;
    --no-profiles) NO_PROFILES=1; shift ;;
    --hooks-json) HOOKS_JSON="$2"; shift 2 ;;
    --hook-dir) HOOK_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$HOOK_DIR"   ]] || HOOK_DIR="$CODEX_DIR/hatchai"
[[ -n "$HOOKS_JSON" ]] || HOOKS_JSON="$CODEX_DIR/hooks.json"

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

if [[ "$HOOK_DIR" == "$HOME/.codex/hatchai" ]]; then
  CONFIGURED='$HOME/.codex/hatchai/HatchAIHook.sh'
else
  CONFIGURED="$INSTALLED"
fi

in_sandbox "$HOOKS_JSON"

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
elif [[ ! -f "$HOOKS_JSON" ]]; then
  echo "No $HOOKS_JSON, so nothing to remove."
  exit 0
fi

if [[ ! -f "$HOOKS_JSON" ]]; then
  mkdir -p "$(dirname "$HOOKS_JSON")"
  printf '{}' > "$HOOKS_JSON"
  echo "Created $HOOKS_JSON"
fi

BACKUP="$HOOKS_JSON.hatchai-backup"
cp "$HOOKS_JSON" "$BACKUP"
echo "Backed up hooks to $BACKUP"

JXA=$(cat <<'JAVASCRIPT'
ObjC.import('Foundation');

function readUtf8(path) {
  const s = $.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null);
  return s.isNil() ? '' : ObjC.unwrap(s);
}

function run(argv) {
  const hooksPath = argv[0];
  const uninstall = argv[1] === 'uninstall';
  const script = '"' + argv[2] + '"';
  const marker = 'HatchAIHook.sh';

  const raw = readUtf8(hooksPath).trim();
  const config = raw === '' ? {} : JSON.parse(raw);
  const hadHooks = Object.prototype.hasOwnProperty.call(config, 'hooks');

  if (hadHooks && config.hooks !== null && typeof config.hooks !== 'object') {
    throw new Error('the "hooks" value is not an object; not safe to edit');
  }
  let hooks = (config.hooks && typeof config.hooks === 'object') ? config.hooks : {};

  const wanted = [
    { event: 'SessionStart',      matcher: null, state: 'idle',       async: false },
    { event: 'UserPromptSubmit',  matcher: null, state: 'generating', async: false },
    { event: 'PreToolUse',        matcher: '.*', state: 'generating', async: false },
    { event: 'PermissionRequest', matcher: null, state: 'waiting',    async: true  },
    { event: 'PostToolUse',       matcher: '.*', state: 'generating', async: false },
    { event: 'Stop',              matcher: null, state: 'idle',       async: false },
    { event: 'SessionEnd',        matcher: null, state: 'ended',      async: false }
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
      const handler = { type: 'command', command: 'bash ' + script + ' codex ' + entry.state };
      if (entry.async) { handler.async = true; }
      const group = {};
      if (entry.matcher) { group.matcher = entry.matcher; }
      group.hooks = [handler];
      hooks[entry.event] = [].concat(hooks[entry.event] || []).concat([group]);
    }
  }

  config.hooks = hooks;
  if (Object.keys(hooks).length === 0 && (removedAny || !hadHooks)) { delete config.hooks; }

  return JSON.stringify(config, null, 2);
}
JAVASCRIPT
)

MODE=$([[ $UNINSTALL -eq 1 ]] && echo uninstall || echo install)

TMP="$(mktemp "${TMPDIR:-/tmp}/hatchai-codex-hooks.XXXXXX")"
trap 'rm -f "$TMP"' EXIT

osascript -l JavaScript -e "$JXA" "$HOOKS_JSON" "$MODE" "$CONFIGURED" > "$TMP"

if ! osascript -l JavaScript -e 'ObjC.import("Foundation"); function run(a){ JSON.parse(ObjC.unwrap($.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null))); return "ok" }' "$TMP" >/dev/null 2>&1; then
  echo "Refusing to write: generated hooks.json did not parse. Left $HOOKS_JSON untouched." >&2
  echo "Your backup is at $BACKUP" >&2
  exit 1
fi

# Codex hashes hooks.json and re-asks for trust when it changes, so an
# unchanged result is not written back.
if cmp -s "$TMP" "$HOOKS_JSON"; then
  echo "$HOOKS_JSON already has these hooks; left unchanged."
  rm -f "$TMP"
else
  mv "$TMP" "$HOOKS_JSON"
fi
trap - EXIT

if [[ $UNINSTALL -eq 1 ]]; then
  echo "Removed HatchAI hooks from $HOOKS_JSON."
  echo "The installed hook script was left in place; delete $HOOK_DIR if you want it gone."
  exit 0
fi

echo "Wired 7 hook entries into $HOOKS_JSON"
echo
echo "One more step, and nothing works without it:"
echo
echo "  Codex will not run a hook it has not been told to trust, and a hooks.json"
echo "  written by anything other than Codex itself starts out untrusted. Start"
echo "  Codex and accept the hook review it shows you, or run /hooks inside it"
echo "  and trust the HatchAI entries."
echo
echo "Then restart any running Codex sessions: hooks are read at session start."

# --- extra Codex accounts -----------------------------------------------------

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
      const dirs = parsed.codexHomes;
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
    echo "--- codex profile: $profile"

    mode=()
    [[ $UNINSTALL -eq 1 ]] && mode=(--uninstall)

    "$0" "${mode[@]+"${mode[@]}"}" --no-profiles --codex-home "$HOME/$profile"
  done
fi
