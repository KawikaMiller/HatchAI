# HatchAI

A tiny desktop pet that lives beside your Claude Code / Codex sessions. It hatches from an egg, evolves as you use Claude, reacts to what your sessions are doing with short speech bubbles, and can optionally have those bubbles written live by a small AI call instead of drawn from a fixed table.

HatchAI is a standalone app — it is not a Claude Buddy plugin, and Claude Buddy does not need to be running for HatchAI to run. It does need **Claude Buddy's hooks installed**, because that's how it learns what your sessions are doing (see [How it works](#how-it-works)).

## Requirements

- **Windows** for now. The code is written cross-platform, but only Windows has been built, tested and smoke-tested — see [Status](#status).
- **[Claude Buddy](https://github.com/Uplift-Foundation/Claude-Buddy)'s hooks installed**, with at least one of Claude Code, Codex or Grok configured to use them. HatchAI reads the same session-status files those hooks already write; it never installs its own hooks.
- To build from source: the **.NET 10 SDK**.

## Quick start

1. Install Claude Buddy and run its hook installer (see its own README), even if you don't want to run Claude Buddy itself day to day.
2. Build HatchAI (see [Building](#building)), or grab a published build if one is attached to a release.
3. Run `HatchAI.exe`. A small egg appears near the bottom-right of your screen.
4. Use Claude Code, Codex or Grok as normal. The pet reacts to your sessions and grows as you use them.

## How it works

HatchAI never talks to Claude Code, Codex or Grok directly, and it never installs a hook of its own. Instead, it polls the same temp directory Claude Buddy's hooks already write session-status files into (`claude_buddy` under your OS temp path), turns those into a stream of session events, and drives a small creature and its evolution off them — the exact mechanism Claude Buddy's own in-app buddy used before this app was extracted from it. `EXTRACTION-PLAN.md` in this repo has the full design record of that extraction: what was ported, what was rebuilt, and what the two apps still share.

Because of that, HatchAI and Claude Buddy can run at the same time without conflicting — they use different single-instance locks and different settings files — though if Claude Buddy's own buddy feature is also on, you'll see two pets. That's expected for now; the plan is for Claude Buddy to drop its in-app buddy once this app is the place for it.

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

**Test-only environment variables** (never needed for normal use — they redirect state so tests don't touch your real settings): `HATCHAI_SETTINGS_DIR`, `HATCHAI_STATUS_ROOT`, `HATCHAI_LOG_DIR`.

## Running the tests

```powershell
dotnet test tests\Tests.sln -c Release
```

This runs three suites: `tests\UnitTests` (the pure creature/ledger/prompt logic), `tests\IntegrationTests` (settings round-trips and the status-file reader against real hook-shaped files), and `tests\UiTests` (headless Avalonia UI tests — no window is ever actually shown).

## Settings and data

Everything HatchAI stores lives under `%APPDATA%\HatchAI\` on Windows:

- `settings.json` — preferences, window position, and the buddy's own state (its identity, token count, evolution stage, and rebirth history).
- `bubble-log.jsonl` — the bubble log, if it's on (the default).

Settings has an **Import** option to copy a buddy's state over from an existing Claude Buddy installation's `settings.json`, read-only, if you were already running the in-app buddy there.

## Status

This app was extracted from [Claude Buddy](https://github.com/Uplift-Foundation/Claude-Buddy)'s in-app "buddy" feature (originally built there as CB-195 and CB-202) into its own project, because the two apps were starting to overlap conceptually with Claude Buddy's separate persona/voice system. `EXTRACTION-PLAN.md` is the full design and risk record from that move — read it if you're curious what was ported verbatim, what was rebuilt smaller, and what's still assumed rather than verified.

**Verified so far:** builds and all three test suites pass on Windows; a published build was smoke-tested against real Claude Code sessions and real transcripts on a real machine (the pet appeared, reacted to real session activity, evolved, and its settings/position survived a restart); the rebirth flow works end to end.

**Not yet done:**
- **macOS** — the code stays cross-platform where it can, but nothing has been built, run or verified there.
- **An installer, or starting automatically at login.**
- **A live AI-generated bubble**, end to end in a real published build — the feature is built and tested against a fake CLI, but nobody has actually turned it on and watched a real one arrive yet.
- **Screenshot tests** (Claude Buddy has these for its own UI; HatchAI doesn't yet).

## Project layout

The folders are for people reading the code. Every file is still in the one flat `HatchAI` namespace, and `HatchAI.csproj` picks the sources up by the SDK's default glob, so moving a file between folders needs no project edits.

- `App/` — startup and process plumbing: `Program`, `Startup`, the Avalonia `App`, the single-instance lock, the crash log, `HatchAISettings`, the macOS window helpers, and `BuddyController`, the orchestrator that wires sessions, creature, ledger, bubbles and windows together.
- `Creature/` — the buddy itself: taxonomy, genome, the hatch roll, evolution progress, its state, `BuddyStore` (which persists that state into settings), and `BuddySprite`, the pure rasterizer that draws it.
- `Ledger/` — token counting: reading transcripts for output tokens, and formatting the totals.
- `Bubbles/` — speech bubbles: when one is raised, the built-in line table, voice profiles, the optional AI-written bubbles (`BubbleText`, `BubbleVoice`, `ClaudeCliBubbleGenerator`), the bubble log, and the bubble window.
- `Sessions/` — what the sessions are doing: reading Claude Buddy's status files into snapshots, process liveness, turn signals, and turning those into moments, focus and placement.
- `Transcripts/` — reading Claude Code and Codex transcripts, and finding their config roots and the `claude` binary.
- `UI/` — the pet window, its card, the sprite control, the tray, and the settings window.
- `Assets/` — icons. `tests/` — the three test suites and `Tests.sln`.
