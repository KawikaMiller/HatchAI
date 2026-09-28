# HatchAI

A tiny desktop pet that lives beside your Claude Code / Codex sessions. It hatches from an egg, evolves as you use Claude, reacts to what your sessions are doing with short speech bubbles, and can optionally have those bubbles written live by a small AI call instead of drawn from a fixed table.

HatchAI is a standalone, self-contained app. It installs and owns the small hooks it uses to see what your sessions are doing, so it needs nothing else installed — not Claude Buddy, which it was extracted from, and not Claude Buddy's hooks.

## Requirements

- **Windows** for now. The code is written cross-platform, but only Windows has been built, tested and smoke-tested — see [Status](#status).
- At least one of **Claude Code, Codex or Grok Build**, which is what the pet reacts to.
- To build from source: the **.NET 10 SDK**.

## Quick start

1. Build HatchAI (see [Building](#building)), or grab a published build if one is attached to a release.
2. Run `HatchAI.exe`. A small egg appears near the bottom-right of your screen.
3. Open **Settings** from the tray icon and click **Install hooks** under *Sessions*. The row says what it installed and for which CLIs.
4. Restart any Claude Code, Codex or Grok sessions that were already open (hooks are read when a session starts), then use them as normal. The pet reacts to your sessions and grows as you use them.

If you use Codex, it will ask you to trust the new hooks the next time it starts — it runs no hook until you do.

## How it works

HatchAI never talks to Claude Code, Codex or Grok directly. Each of them can run a *hook* — a small command it runs on events like a session starting, a prompt being sent, a tool being used, or a session ending. HatchAI's hook (`HatchAIHook.ps1`, or `HatchAIHook.sh` on macOS) writes a tiny status file per session into a temp folder, and HatchAI polls that folder, turns the files into a stream of session events, and drives a small creature and its evolution off them.

**Installing the hooks** (the Settings button, or `Hooks\install-hooks.ps1` by hand) copies the hook script to `%LOCALAPPDATA%\HatchAI\` and adds HatchAI's entries to:

- Claude Code: `~\.claude\settings.json` (plus any extra Claude Code profile folders you've configured),
- Codex: `~\.codex\hooks.json`, if Codex is installed,
- Grok Build: its own file, `~\.grok\hooks\hatchai.json`, if Grok is installed.

It **merges, never overwrites**: everything already in those files — your model, permissions, status line, and other tools' hooks — is left exactly as it was, and a backup of the previous file is kept beside it as `*.hatchai-backup`. HatchAI's entries are recognised by the script name `HatchAIHook.ps1`, so re-running the install replaces them rather than adding a second set, and it never touches anyone else's entries. Running it twice changes nothing the second time. `-Uninstall` on any of the installers removes just HatchAI's entries.

The status-file format, and the folder it lives in (`claude_buddy` under your temp path), are the ones Claude Buddy's hooks use — HatchAI's hook is a fork of Claude Buddy's, and deliberately writes the same thing to the same place. That keeps HatchAI's reader compatible with both, and means a machine that has both apps installed just has two hooks writing one identical file per session. HatchAI's installer and Claude Buddy's each recognise only their own entries, so neither ever removes the other's. `EXTRACTION-PLAN.md` has the design record of the extraction and of this change.

HatchAI and Claude Buddy can also run at the same time — they use different single-instance locks and different settings files — though if Claude Buddy's own buddy feature is also on, you'll see two pets. That's expected for now; the plan is for Claude Buddy to drop its in-app buddy once this app is the place for it.

## What it does

- **Hatches, then evolves.** Egg → Hatchling → First → Second → Third evolution, driven by how many output tokens your Claude Code / Codex sessions produce (combined across every session, not per-session).
- **Rebirth.** Once fully evolved and past a further token threshold, you can choose to have your buddy be reborn as a new one. The old one is kept in a history list with its final stats, and a lifetime token total is never reset by a rebirth.
- **Speech bubbles.** Short reactions to session events — a reply finishing, a question waiting on you, a long silence — drawn from a built-in table by default.
- **Optional AI-written bubbles**, off by default. When turned on in Settings, each bubble is instead written live by a small model call (via your own Claude Code login), reacting to what you're actually working on. Settings explains exactly what is and isn't sent, and roughly what it costs per bubble.
- **A bubble log**, on by default, recording each bubble's moment and outcome (and its text) to a local file, for debugging or just watching it work. Off switch is in Settings.
- **A card** (click the pet) showing its name, species, rarity, stats and progress, plus its history of past lives.

## Building

```powershell
dotnet build HatchAI.csproj -c Release
dotnet publish HatchAI.csproj -c Release -r win-x64
```

The publish step produces a single self-contained `HatchAI.exe` under `bin\Release\net10.0\win-x64\publish\` (or wherever you point `-o`).

**Launch switches**, for development: `HatchAI.exe --settings` opens straight to the Settings window; `HatchAI.exe --card` opens the buddy's card immediately, without needing to click the pet first.

**Test-only environment variables** (never needed for normal use — they redirect state so tests don't touch your real settings): `HATCHAI_SETTINGS_DIR`, `HATCHAI_STATUS_ROOT`, `HATCHAI_LOG_DIR`, and `HATCHAI_INSTALLER_SANDBOX`, which makes the hook installers refuse to write anywhere outside the folder it names.

**Installing the hooks by hand** — the scripts in `Hooks\` are the same ones the Settings button runs (it carries them embedded in the exe). `Hooks\install-hooks.ps1` wires every CLI it finds; `install-windows-hooks.ps1`, `install-codex-hooks.ps1` and `install-grok-hooks.ps1` do one CLI each, and all of them take explicit target paths (`-SettingsPath`, `-CodexHome`, `-GrokHome`, `-InstallDir`, `-TempDir`) so they can be pointed at a scratch folder to see exactly what they would do.

## Running the tests

```powershell
dotnet test tests\Tests.sln -c Release
```

This runs three suites: `tests\UnitTests` (the pure creature/ledger/prompt logic), `tests\IntegrationTests` (settings round-trips, the status-file reader, and the hook script and installers run as real subprocesses against scratch files — including the check that installing into an existing settings file leaves everything else in it exactly as it was), and `tests\UiTests` (headless Avalonia UI tests — no window is ever actually shown). The hook tests run under Windows PowerShell 5.1 and, if it's installed, PowerShell 7.

## Settings and data

Everything HatchAI stores lives under `%APPDATA%\HatchAI\` on Windows:

- `settings.json` — preferences, window position, and the buddy's own state (its identity, token count, evolution stage, and rebirth history).
- `bubble-log.jsonl` — the bubble log, if it's on (the default).

Settings has an **Import** option to copy a buddy's state over from an existing Claude Buddy installation's `settings.json`, read-only, if you were already running the in-app buddy there.

## Status

This app was extracted from [Claude Buddy](https://github.com/Uplift-Foundation/Claude-Buddy)'s in-app "buddy" feature (originally built there as CB-195 and CB-202) into its own project, because the two apps were starting to overlap conceptually with Claude Buddy's separate persona/voice system. `EXTRACTION-PLAN.md` is the full design and risk record from that move — read it if you're curious what was ported verbatim, what was rebuilt smaller, and what's still assumed rather than verified.

**Verified so far:** builds and all three test suites pass on Windows; a published build was smoke-tested against real Claude Code sessions and real transcripts on a real machine (the pet appeared, reacted to real session activity, evolved, and its settings/position survived a restart); the rebirth flow works end to end. HatchAI's own hook installer has been verified on Windows against scratch copies of real configuration only: installed alongside Claude Buddy's entries without changing them, idempotent, and the wired commands, run the way Claude Code runs them, produce status files HatchAI reads. That smoke test predates HatchAI owning its hooks — it ran on Claude Buddy's.

**Not yet done:**
- **HatchAI's own hooks in daily use** — nobody has yet installed them into a real Claude Code, Codex or Grok configuration and used them.
- **Codex and Grok hooks** — the installers are tested against scratch files, but neither CLI was installed on the machine this was built on, so neither has fired HatchAI's hook for real.
- **WSL sessions** — Claude Buddy's installer can also wire Claude Code inside WSL distros; HatchAI's does not.
- **macOS** — the code stays cross-platform where it can, and the hook and its installers have macOS twins, but nothing has been built, run or verified there.
- **An installer, or starting automatically at login.**
- **A live AI-generated bubble**, end to end in a real published build — the feature is built and tested against a fake CLI, but nobody has actually turned it on and watched a real one arrive yet.
- **Screenshot tests** (Claude Buddy has these for its own UI; HatchAI doesn't yet).

## Project layout

The folders are for people reading the code. Every file is still in the one flat `HatchAI` namespace, and `HatchAI.csproj` picks the sources up by the SDK's default glob, so moving a file between folders needs no project edits.

- `App/` — startup and process plumbing: `Program`, `Startup`, the Avalonia `App`, the single-instance lock, the crash log, `HatchAISettings`, the macOS window helpers, and `BuddyController`, the orchestrator that wires sessions, creature, ledger, bubbles and windows together.
- `Creature/` — the buddy itself: taxonomy, genome, the hatch roll, evolution progress, its state, `BuddyStore` (which persists that state into settings), and `BuddySprite`, the pure rasterizer that draws it.
- `Ledger/` — token counting: reading transcripts for output tokens, and formatting the totals.
- `Bubbles/` — speech bubbles: when one is raised, the built-in line table, voice profiles, the optional AI-written bubbles (`BubbleText`, `BubbleVoice`, `ClaudeCliBubbleGenerator`), the bubble log, and the bubble window.
- `Sessions/` — what the sessions are doing: reading the hooks' status files into snapshots, process liveness, turn signals, and turning those into moments, focus and placement.
- `Hooks/` — HatchAI's hook script and its installers (PowerShell for Windows, bash for macOS), embedded into the exe at build time. `App/HookSetup.cs` is what runs them from Settings.
- `Transcripts/` — reading Claude Code and Codex transcripts, and finding their config roots and the `claude` binary.
- `UI/` — the pet window, its card, the sprite control, the tray, and the settings window.
- `Assets/` — icons. `tests/` — the three test suites and `Tests.sln`.
