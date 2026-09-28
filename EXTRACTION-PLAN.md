# HatchAI extraction plan

**NOTE (2026-09-27): "HatchAI" is the current working name, picked so naming exploration doesn't block the build. It may still change later** — every identifier below (`HatchAISettings`, `HATCHAI_SETTINGS_DIR`, the mutex name, the folder names) would need the same mechanical find-and-replace this note itself just went through. The plan itself (files, architecture, risks, decisions) does not change with the name.

**SUPERSEDED IN PART (2026-09-27, later the same day): HatchAI now installs and owns its own hooks.** Everywhere below that says HatchAI reads status files *Claude Buddy's hooks* write, never installs a hook, or does not port the hook scripts and installers (§0, §1d, §2.1), describes the original piggyback design, which is no longer current. What remains true, and is why those sections are kept rather than rewritten: the status-file *format* and *folder* (§2.1, §2.2) are exactly what HatchAI's own hook now writes, because that hook is a verbatim fork of Claude Buddy's. §9 records the change and its reasoning.

**Both open owner decisions from the plan are now settled** (2026-09-27): §2.4 uses rule (B) — a session with a live process never expires from idling alone. §7.5's coexistence detection is skipped for v1 — the owner is fine with a temporary period where both Claude Buddy's in-app buddy and this app run at once, since the plan is to remove the in-app buddy from Claude Buddy entirely once this app is proven.

---

# HatchAI — extraction and design plan

**Source:** `K:\Programming\personalProjects\Claude-Buddy-buddy`, branch `feature/buddy-companion`, HEAD `5bed380dd9654519442d63828ac168e75d9f16b4` ("Pin the stats at the process seam, over stdin and never as an argument").

**Evidence labels used throughout:**
- **[READ f:l]** — I read it in the source at that file and line.
- **[SRC-CLAIM]** — the source's own comments or docs say it was measured, but I did not verify it.
- **[ASSUMED]** — my inference. Every macOS point is this, because I am on Windows with no Mac.

## 0. Summary

- **What ports:** 26 buddy files, as close to verbatim as possible. Nearly every edit is mechanical: the namespace `ClaudeBuddy` becomes `HatchAI`, and `ClaudeBuddySettings.` becomes a new slim `HatchAISettings.` with the same member names.
- **Supporting files:** about 12 small or pure supporting files are ported whole or trimmed.
- **Four new pieces replace Claude Buddy's big infrastructure:**
  - `StatusReader` (about 250 lines), replacing SessionManager.
  - `HatchAISettings` (about 250 lines), replacing ClaudeBuddySettings.
  - A minimal `App`/`Program`/tray (about 200 lines).
  - A small `SettingsWindow` (about 250 lines).
- **Two string literals must NOT be renamed:**
  - The hatch salt `"claude-buddy/hatch/v1"` (BuddyHatch.cs:25). Changing it re-rolls every buddy and breaks the golden tests.
  - `BubbleVoice.WorkDirLeaf = "claudebuddy-bubble-voice"` (BubbleVoice.cs:59). It is now a contract *between the two apps* (§7.3).
- **Status folder name** stays `claude_buddy`, because that is where the hooks write. *(Still true now that HatchAI has its own hook — see §9.3.)*
- **State and settings** go in HatchAI's own file, never Claude Buddy's `settings.json`.

## 1. File inventory

I traced this from each buddy file's actual type references, not just grep. The complete set of outside types they reach is: ChatPanelPlacement, ChatRole, ChatTurn, ChatPrompt, ChatTranscript, ClaudeBinary, ClaudeBuddySettings, ClaudeConfigRoots, CodexTranscript, CodexUsageAccounts, CrashLog, InternalSessions, SessionSource, SessionStatus, TickGate, TranscriptReader, TrayController, TurnSignals, and the MacOSWindowExtensions extension methods [READ, via a non-comment grep over Bubble*/Buddy*/TokenFormat/ClaudeCliBubbleGenerator/SessionSnapshot].

Two dependencies the task list did not name:
- **`TurnSignals.cs`**, used by BuddyMoments.cs:115.
- **`BubbleLines.cs` and `BubbleVoiceProfiles.cs`**, both buddy files.

### 1a. Port verbatim (namespace rename only; axaml files also change `x:Class` and `clr-namespace`)

| File | Lines | Notes |
|---|---|---|
| BuddyTaxonomy.cs | 94 | enum order is part of the hatch; append-only |
| BuddyGenome.cs | 33 | |
| BuddyHatch.cs | 132 | **keep the Salt literal** (:25) |
| BuddyProgress.cs | 121 | |
| BuddyState.cs | 93 | |
| BuddyLedger.cs | 316 | |
| BuddyMoments.cs | 166 | depends on TurnSignals |
| BuddyFocus.cs | 59 | |
| BubblePolicy.cs | 85 | |
| BubbleLines.cs | 156 | |
| BubbleVoiceProfiles.cs | 70 | |
| BubbleText.cs | 511 | LatestUserPrompt (:432-455) needs ChatTranscript/CodexTranscript/SessionSource |
| BubbleVoice.cs | 102 | **keep WorkDirLeaf** (:59) |
| BuddySeams.cs | 53 | the comments mention settings.json; doc edit only |
| BuddySprite.cs | 1494 | pure rasterizer |
| BuddySpriteControl.cs | 111 | |
| BuddyPlacement.cs | 129 | calls ChatPanelPlacement.ClampSavedPosition (:57) |
| TokenFormat.cs | 58 | |
| BubbleWindow.axaml / .axaml.cs | 57 / 125 | change `Title="Claude Buddy"` |
| BuddyCard.axaml / .axaml.cs | 142 / 330 | change Title |
| TurnSignals.cs | 144 | pure |
| TickGate.cs | 47 | pure |
| InternalSessions.cs | 68 | still useful in-process (its own reader drops its own pids) |
| ProcessLiveness.cs | 65 | needed for the dead-pid rule |
| MacOSWindowExtensions.cs | 171 | ShowOnAllSpaces and AcceptFirstClick are used at BuddyWindow.axaml.cs:77-81, BubbleWindow.axaml.cs:37-38 and BuddyCard.axaml.cs:42-43. The `WaitForOwnActivation` method is unused by the buddy and can be deleted. |
| ClaudeBinary.cs | 154 | used by ClaudeCliBubbleGenerator.cs:86 and by the AI settings row |
| CodexTranscript.cs | 408 | needs `ChatTranscript.Row` and ChatTurn |

### 1b. Port with small edits

- **BuddyController.cs (871 lines).** In `CreateForApp` (:85-99), replace `ClaudeBuddySettings.*` with `HatchAISettings.*`. Nothing else changes. The `OnSnapshots` comment says "Called by SessionManager" (:335); update that comment only.
- **BuddyStore.cs (266 lines).** Lines :53 and :64 become `HatchAISettings.BuddyObject()` and `.UpdateBuddy(...)`. The JSON shape and the in-place `Write` discipline stay exactly as they are.
- **BuddyLedgerScanner.cs (413 lines).** Line :144 uses `HatchAISettings.CodexHomes`.
- **BuddyWindow.axaml(.cs) (71 / 408 lines).**
  - Change the settings class at :164, :182, :240, :288, :351-352 and :359.
  - `PositionKey="buddy"` can stay if the slim settings keeps an `orbPositions`-style map. Simpler is to add `BuddyPosition` get/set; either way it is one line each.
  - Change the Title.
- **ClaudeCliBubbleGenerator.cs (413 lines).** Unchanged in code. The comments at :339-344 describe Claude Buddy's SessionManager as a guard layer; rewrite them to describe the cross-app reality (§7.3).
- **BubbleLog.cs (345 lines).** Line :216, `DefaultDirectory`, becomes `HatchAISettings.Directory`.
- **SessionSnapshot.cs (78 lines).** The record (:13-25) is verbatim. For `SessionSnapshotTracker.Build`:
  - It currently takes `(string Id, SessionStatus Status)` and calls `TrayController.DisplayName` (:65).
  - Change it to take the new slim status DTO.
  - Replace DisplayName with a local 5-line version: title, else the cwd leaf, else the id. The agent-team name no longer exists.
  - The buddy never reads `Label` or `Lead` (non-comment grep over the buddy files found no `.Label` or `.Lead`). Pass `Lead=""`.
- **ClaudeConfigRoots.cs (50 lines).** Line :29 reads `HatchAISettings.ClaudeCodeProfileDirs`.
- **StatusDirectory.cs (102 lines).** Verbatim logic. Rename the test seam from `CLAUDE_BUDDY_STATUS_ROOT` to `HATCHAI_STATUS_ROOT` (:71). **Keep `FolderName = "claude_buddy"`** (:29).
- **CrashLog.cs (322 lines).**
  - Directory names `"ClaudeBuddy"` (:120, :126) become `"HatchAI"`.
  - Rename the env var `CLAUDE_BUDDY_LOG_DIR` (:50) to `HATCHAI_LOG_DIR`.
  - It has no other outside dependencies. I checked non-comment code; the only other reference is `Dispatcher` in Install.
- **SingleInstance.cs (234 lines).** `MutexName` becomes `"HatchAI_SingleInstance_Mutex"`.
- **ChatTranscript.cs (469 lines).** Port `Map`, `IsInteresting` and `IsNoise`. Delete `ParseDialog` (:380-437), which is terminal-dialog parsing the buddy never uses. That removes the ChatPrompt dependency.
- **New `ChatModel.cs` (about 30 lines), replacing RemoteChat.cs (563 lines).**
  - `enum ChatRole { User, Assistant, System }` (RemoteChat.cs:31).
  - A plain `ChatTurn` with `Role`, `Text`, `IsComplete`, `At` and `ImageBytes`. These are the only members ChatTranscript and CodexTranscript set; I checked the initializers at ChatTranscript.cs:102-108, 170-177, 241-247 and 272-279, and CodexTranscript :168 and :200.
  - Drop INotifyPropertyChanged, MediaConfidence and the OpenClaw fields.
- **TranscriptReader.cs (376 lines), slimmed to about 35 lines.** Keep only `TailLines(path)`, `TailLines(path, n)` and `ReadTail` (:293-332), verbatim. That is all ClaudeCliBubbleGenerator.cs:89 uses.
- **CodexUsage.cs (569 lines) → new `CodexHomes.cs` (about 20 lines).** Copy `CodexUsageAccounts.Homes` (:345-361) verbatim, either keeping the class name `CodexUsageAccounts` or renaming it with a one-token edit at BuddyLedgerScanner:144.
- **ChatPanelPlacement.cs (142 lines).** Copy `Clamp` and `ClampSavedPosition` (about :100-130) into BuddyPlacement or a small `Placement.cs`, and drop `Resolve` and the chat-panel logic.
- **MachineNames.cs.** Copy only `LooksLikeALeftoverRelay` and the `"claude-buddy-rc-"` prefix (:18, :223-228) into StatusReader. It calls `TerminalScripts.LeafOf`; replace that with the same leaf split BubbleVoice.IsOwnWorkDir already uses (BubbleVoice.cs:95-96).
- **SessionSource enum**, currently inside SessionManager.cs:246-270. Extract it verbatim with all six members; LatestUserPromptTests uses `SessionSource.OpenClaw`. HatchAI only ever produces ClaudeCode, Codex or Grok. `SourceOf` (SessionManager.cs:785-792) moves into StatusReader verbatim.

### 1c. Replace with new slim versions

| New | Replaces | Size | Section |
|---|---|---|---|
| StatusReader.cs + StatusFile.cs | SessionManager.cs (4,268 lines) | ~250 | §2 |
| HatchAISettings.cs | ClaudeBuddySettings.cs (2,590 lines) | ~250 | §3 |
| App.axaml(.cs), Program.cs, Tray.cs | App/Program/Startup/TrayController | ~200 | §4 |
| SettingsWindow.cs | SettingsWindow.cs (4,383 lines) | ~250 | §4 |

### 1d. Explicitly NOT ported

- **Session and orb infrastructure:** SessionManager (except the pure rules copied into StatusReader), OrbWindow, OrbArrangement, TeamLinks, AgentTeam, BackgroundJobs, SessionPresence, SessionPark, TranscriptHandoff, TranscriptHunts, TerminalFocuser.
- **Chat and personas:** ChatPanel*, persona files.
- **Other integrations:** OpenClaw*, Peer*, Remote*, ClaudeCloud*, AccountUsage and the pollers, GlobalHotkeys, TTS/Whisper, ClaudeDesktop*, HookInstaller, hook scripts and snippets. *(Superseded for the hook scripts and installers: forked into `Hooks/` — see §9. The README snippets and Claude Buddy's HookInstaller.cs are still not ported; `App/HookSetup.cs` is HatchAI's own, smaller equivalent.)*
- **Also:** WslIntegration. The ledger does not use it; a WSL transcript path simply fails to open and costs itself (BuddyLedgerScanner.cs:325-338).
- **Deferred:** MacOSScreenLock and ScreenLockWait (macOS follow-up, §5).
- **GrokTranscript is not needed.** The ledger ignores Grok (BuddyLedgerScanner.FormatOf :161; BuddyController.cs:342 filters live paths to ClaudeCode or Codex), and LatestUserPrompt returns null for Grok (BubbleText.cs:441).

## 2. The status-file reader (replaces SessionManager)

### 2.1 Path — must be identical to what the hooks write

- **Hook, bash (macOS/Linux)** [READ ClaudeBuddyHook.sh:78-79, :95]: `DIR="${TMPDIR:-/tmp/}"`, then `${DIR%/}/claude_buddy`, then `$DIR/$SESSION_ID.txt`.
- **Hook, PowerShell (Windows)** [READ ClaudeBuddyHook.ps1:100-106]: `$TempDir` if it was passed, else `[IO.Path]::GetTempPath()`, then `\claude_buddy\<id>.txt`. The installer bakes `-TempDir` as `GetTempPath().TrimEnd('\')` computed at install time [READ tools/install-windows-hooks.ps1:291-297]. WSL hooks go through Windows `powershell.exe` with the same baked TempDir [READ :614], so WSL sessions land in the same Windows folder.
- **App side** [READ StatusDirectory.cs:60-74, :100]: `CLAUDE_BUDDY_STATUS_ROOT`, else `TMPDIR`, else (macOS only) `confstr(_CS_DARWIN_USER_TEMP_DIR = 65537)`, else `Path.GetTempPath()`; then `/claude_buddy`.
- **Windows:** TMPDIR is normally unset, so the result is `%TEMP%`, matching the baked TempDir unless the user's `%TEMP%` changed after installing [ASSUMED low risk].
- **macOS:** a launchd or Login-Item start has no TMPDIR, and the confstr fallback is the fix for that [SRC-CLAIM StatusDirectory.cs:10-26, measured on "the mini"]. Whether it works for HatchAI is **[ASSUMED]**.
- **Decision:** port StatusDirectory verbatim and keep the folder name.

### 2.2 File shape and lifecycle

The bash hook writes [READ sh:578-580]: `state`, `cli`, `cwd`, `title`, `color`, `term_program`, `term_id`, `tty`, `tmux_socket`, `tmux_pane`, `tmux_bin`, `session_pid` (a number) and `transcript_path`. The PowerShell hook writes [READ ps1:385-400] `state`, `cli`, `cwd`, `title`, `color`, `term_program`, `term_id`, `term_pid`, `session_pid` and `transcript_path`, as UTF-8 without a BOM (:407).

**Lifecycle.** The event-to-state mapping is SessionStart→idle, UserPromptSubmit/PreToolUse→generating, Notification→waiting, Stop→idle, SessionEnd→ended [READ claude-hooks-snippet-windows.json]. For `ended`, the hook **deletes** the file and never writes the state (sh:97-100, ps1:108-111). So a session "ends" by disappearing, and BuddyMoments turns a disappearance into SessionEnded (BuddyMoments.cs:99-103). Ctrl+C fires no SessionEnd, so the file lingers until the dead-pid rule removes it.

**Which CLI wrote the file.** `cli` is `claude`, `codex` or `grok`, and an absent value means Claude Code (SessionManager.cs:56-75, :785-792). The buddy needs the CLI because:
- ledger live paths include ClaudeCode and Codex only (BuddyController.cs:342);
- the AI-prompt reader maps transcripts by source (BubbleText.cs:437-441);
- Superseded groups files by (pid, source) (SessionManager.cs:853).

**Fields the buddy needs:** the id (the filename), `state`, `cli`, `cwd` (ProjectName, BuddyController.cs:466), `transcript_path` (live ledger paths and the AI prompt) and `session_pid` (liveness and superseded). `title` is used only for Label, which the buddy never reads. `term_program`, `tty`, `tmux_pane` and `term_pid` are needed only if the NoTerminal rule is ported (§2.3).

**Fields not needed:** color, term_id, tmux_socket and tmux_bin (all orb or terminal-focus concerns), and every `[JsonIgnore]` app-derived field (Lead, Agent, Kind, Shape, Presence, Url, ContextPercent and so on; SessionManager.cs:32-232).

**`StateSince`** is computed by the ported SessionSnapshotTracker as "first observed in this state" [READ SessionSnapshot.cs:30-76].

### 2.3 Filter chain

The buddy's bubbles depend on exactly which sessions appear, so this matters. SessionManager applies these filters before `PublishSnapshots` (:2666, :2688-2695); `SnapshotsPublished` is at :487.

| # | Rule | Source | HatchAI |
|---|---|---|---|
| 1 | Unparseable or vanished file: skip this tick | :1847-1858 | Keep. Also: open with `FileShare.ReadWrite \| FileShare.Delete` (§7.2). Optional improvement: keep the last good parse while the file still exists, which stops a torn read from producing a phantom Ended then Started. This is a deliberate deviation; the owner decides. |
| 2 | `EnabledFor(source)` | :658-664, :1895 | Drop. All CLIs are on. |
| 3 | Leftover relay cwd (`claude-buddy-rc-*`) | :1915, MachineNames :223 | **Keep**, since Claude Buddy's relays are real Claude Code sessions. |
| 4 | `InternalSessions.IsInternal(pid)` | :1927 | Keep. It now covers **only HatchAI's own** pids (§7.3). |
| 5 | `BubbleVoice.IsOwnWorkDir(cwd)` | :1943 | **Keep. This is critical cross-app:** it hides Claude Buddy's spoken-summary `claude -p`, which runs *with hooks enabled* from the same folder (§7.3). |
| 6 | New: cwd leaf `claude-buddy-grok-refresh` | GrokUsageRefresh.cs:171-172 | Add. This is Claude Buddy's macOS Grok usage probe, which Claude Buddy itself does not filter [READ; no other references]. |
| 7 | `Superseded(found, isLiveJob)` | :838-886 | Port verbatim. Pass `isLiveJob = _ => false`, because the real one shells out to `claude agents --json` via BackgroundJobs (538 lines). Consequence: extra Agent View sessions that share one pid lose their bubbles. Tokens are still counted by the 60 s walk. |
| 8 | ProcessGone: `pid>0 && !ProcessLiveness.IsRunning` | :1101 | Keep. |
| 9 | Backgrounded husk (SessionPark/TranscriptHandoff) | :1110, :2418-2421 | Drop for v1. A frozen husk can hold "generating" until its pid dies. Follow-up if it is seen. |
| 10 | Expired (orb lifetime) | :1139-1146, default **5 min** (ClaudeBuddySettings.cs:217) | **Owner decision, §2.4.** |
| 11 | JudgeReachability | :1445-1596 | Port the pure parts with `phase = JobPhase.Unknown`. Codex/Grok with pid≤0 are dropped (:1589-1593). NoTerminal applies to Codex/Grok only when phase is Unknown (:1542-1543 exempts ClaudeCode). NotALiveJob for ClaudeCode needs the daemon listing, so skip it and let rule 10 cover pid-less Claude files. Note: Claude Buddy *does* consult the daemon whenever any Claude session has no pid, no terminal or a shared pid (:2244, SessionPresence.cs:167-170), so this is a partial-parity choice. |
| 12 | Transcript repair and title identity | :1876-1886 | Drop. Only Label and chat use them; the 60 s discovery covers ledger gaps. |

### 2.4 Owner decision: session expiry

This is a real behaviour issue found in the source.

With Claude Buddy's default 5-minute orb lifetime, an idle session whose pid is alive (its file mtime frozen) is **Expired** after 5 minutes and disappears from the snapshots [READ :1139-1146]. The buddy then says **SessionEnded**, and says **SessionStarted** at the next prompt. **LongIdle (15 min, BuddyMoments.cs:46) can never fire** unless the user set the lifetime to 15 minutes or more, or Forever.

- **(A) Parity:** expire everything after 5 minutes, with the "waiting" exemption, exactly as today.
- **(B) Recommended:** a session whose recorded pid is alive never expires. Pid-less files (older hooks, subagent leftovers, since there is no daemon check) expire after 5 minutes unless they are "waiting". This fixes LongIdle and stops the fake end/start pair.

**DECIDED (owner, 2026-09-27): (B).** Confirmed the real setting first: the owner's `orbLifetimeMinutes` is `5`, the default, so under (A) LongIdle (15 min) is unreachable *today*, not just in theory. Implement (B).

### 2.5 Shape

- **`StatusFile`**, a JSON DTO for the fields in §2.2.
- **`StatusReader.ReadDirectory(dir)`** (disk): `Directory.EnumerateFiles(dir, "*.txt")`, then mtime, then deserialize. It runs off the UI thread.
- **`StatusReader.Judge(entries, now, isRunning, ...)`**: pure rules 3-11, copied verbatim where they exist (SourceOf, Superseded, JudgeLiveness, the pure part of JudgeReachability, KnowsATerminal).
- **`SessionSnapshotTracker.Build`**, ported.
- **Timer:** a DispatcherTimer every 2 s, matching SessionManager.cs:497. The FileSystemWatcher and debounce (:496-498) are optional; polling alone matches the "turns shorter than 2 s are not seen" contract (BuddyMoments.cs:4-6).
- **Output:** a `SnapshotsPublished` Action, which App wires to `buddy.OnSnapshots` exactly as App.axaml.cs:120-121 does today.
- **The reader never writes, deletes or creates anything in the status folder.** Claude Buddy writes `.auto-color` (SessionManager.cs:529-530), rewrites files on reset (:3812-3834) and sweeps dead files (:2737ff, :3908); HatchAI must not.

## 3. Settings and persistence

**Today** [READ]:
- The buddy lives in a top-level `"buddy"` object in `%APPDATA%\ClaudeBuddy\settings.json` (ClaudeBuddySettings.cs:170-177, :130, :892-915; BuddyStore.cs:6-31).
- The five preferences are top-level keys with these defaults: `buddyEnabled` true, `buddyBubblesEnabled` true, `buddyAiBubblesEnabled` false, `buddyIdleBubblesEnabled` false, `buddyBubbleLogEnabled` true (:706-728, :1882-1886).
- The window position is `orbPositions["buddy"]` (BuddyWindow.axaml.cs:31, :240, :288; docs/buddy-design.md:92).
- The ledger also reads `claudeCodeProfileDirs` and `codexHomes` (ClaudeConfigRoots.cs:29; BuddyLedgerScanner.cs:144).

**Decision: HatchAI uses its own file, never Claude Buddy's.**
- Windows: `%APPDATA%\HatchAI\settings.json`.
- macOS: `~/Library/Application Support/HatchAI/settings.json`, spelled out per platform.
- **Why not share:** both apps would hold their own in-memory model and rewrite the whole file (ClaudeBuddySettings.cs:2455-2464). The last writer would win on the whole `buddy` object, so cursors would move backwards (recounts) or tokens would be lost, silently.
- **Why spell out the macOS path:** Claude Buddy's comment says `SpecialFolder.ApplicationData` is `~/Library/Application Support` on macOS (:161-163). My recollection is that .NET 8 moved *LocalApplicationData*, not ApplicationData, to that path, and ApplicationData is `~/.config` **[ASSUMED/UNVERIFIED]**. An explicit path removes the doubt. The *import* code must still use Claude Buddy's exact expression (`ApplicationData` + `"ClaudeBuddy"`) to find its file, whatever that resolves to.

**File shape:**
```json
{ "version": 1,
  "buddyEnabled": true, "buddyBubblesEnabled": true, "buddyIdleBubblesEnabled": false,
  "buddyAiBubblesEnabled": false, "buddyBubbleLogEnabled": true,
  "orbPositions": { "buddy": { "x": 0, "y": 0 } },
  "claudeCodeProfileDirs": [], "codexHomes": [],
  "buddy": { ...exactly BuddyStore's shape... } }
```

**Implementation:**
- Hold the whole root as a `JsonObject` in memory and mutate only the known keys. Unknown keys round-trip at every depth for free; no `_unknownKeys` machinery is needed.
- `BuddyObject()` returns a deep clone and `UpdateBuddy(edit)` edits in place, keeping the BuddyStore contract (BuddySeams.cs:10-13).
- Write atomically to `.tmp` then `File.Move(overwrite)`, as UTF-8 without a BOM (:2455-2464).
- Set `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` on the write options. The source reports that trimmed single-file publishes threw without it (:132-152) [SRC-CLAIM].
- The test seam is `HATCHAI_SETTINGS_DIR`.
- The bubble log file sits in the same folder (BubbleLog.cs:216).
- The save cadence is already throttled by BuddyController (`SaveEvery` 30 s, :118).

**Optional one-time import** (recommended: an explicit button in Settings, not automatic). Read Claude Buddy's `settings.json` read-only and copy its `buddy` object, `orbPositions.buddy`, `claudeCodeProfileDirs` and `codexHomes`. Never write to it. Explain in the UI that both apps would then carry the same pet unless Claude Buddy's buddy is turned off (§7.5).

## 4. Minimal app shell

**Packages** [READ ClaudeBuddy.csproj:5, :113-115]: net10.0, `Avalonia`, `Avalonia.Desktop` and `Avalonia.Themes.Fluent` 12.1.1.

Drop ColorPicker, BouncyCastle, PvRecorder, System.Management, Whisper.* and Devolutions.AvaloniaTheme.MacOS. The last one crashes on its ToggleSwitch template (SettingsWindow.cs:225-263). Recommendation: **Fluent on both platforms**. That is a deliberate visual change for BuddyCard's buttons on macOS; the alternative is porting the Devolutions theme plus `BorrowFluentToggleSwitch`.

Port App.axaml's `ToolTip` style (:12-22) for parity with the buddy's string tooltip, then check how it looks, since that style strips the tooltip chrome.

**Program.Main:**
1. `CrashLog.Install`.
2. `SingleInstance.Claim("HatchAI_SingleInstance_Mutex")`. **This is needed:** two instances would both count the same tokens and the last Save would win, losing or recounting.
3. `BuildAvaloniaApp()` with `.With(new MacOSPlatformOptions { ShowInDock = false })` (Program.cs:175-180).

Skip PeerSessions, OpenClaw and the cloud (Program.cs:107-144). The macOS screen-lock wait (:146-170) is a documented follow-up.

**App.OnFrameworkInitializationCompleted:**
- Set `ShutdownMode.OnExplicitShutdown`.
- `var buddy = BuddyController.CreateForApp(new BuddyWindow());`
- `reader.SnapshotsPublished = buddy.OnSnapshots;`, then `reader.Start()` and `buddy.Start()`.
- On `Exit`: `buddy.Dispose()`, stop the reader, flush settings.
- Keep the `--settings` dev switch (App.axaml.cs:174-177).

**Tray (Avalonia `TrayIcon` + `NativeMenu`).** Claude Buddy's tray contributes exactly one buddy item today: "Show buddy" (TrayController.cs:238-244, :302-306). The new menu:
- **Show buddy** (checkbox; `ToggleBuddyVisible` ported).
- **Open card.**
- **Rebirth…**, enabled only when `BuddyProgress.CanRebirth(state.Tokens)`. This is *new*; route it through `BuddyWindow.OpenCard(offerRebirth: true)` so the card's confirmation stays the only path. Disabled while the buddy is hidden.
- **Speech bubbles** (optional).
- **Settings…**
- **Quit HatchAI.**

The tray is essential, because after "Hide buddy" (BuddyWindow.axaml.cs:357-362) it is the only way back apart from Settings. For the icon, copy `Assets/tray-idle.png` and `appicon-1024.png` / `ClaudeBuddy.ico` for v1 (same owner), or render a sprite frame later.

**SettingsWindow (small).** One window with the rows from `BuddyRows` (SettingsWindow.cs:763-784):
- Show buddy (calls `Reapply`).
- Speech bubbles.
- Idle bubbles.
- `AiBubblesRow` (:903-933), including the no-CLI disable via `ClaudeBinary.Locate()` (:896-897).
- `BubbleLogRow` (:817-850), including the "Open folder" action (ClaudeDesktopManager.OpenFolder uses `explorer.exe` or `/usr/bin/open`, about 15 lines).

Copy the description constants verbatim (:788-796, :869-888). Copy `Row` (:3458-3497) and `Switch` (:3619-3631) as plain helpers without SettingsRow, SearchText or Card chrome. Add a coexistence banner (§7.5) and the optional Import button.

**Must NOT depend on:** SessionManager, ClaudeBuddySettings, TrayController, OrbWindow and orbs, GlobalHotkeys, ChimePlayer, NeuralSpeech/TTS, ClaudeDesktopUrlRouter, PeerSessions, OpenClawSessions, ClaudeCloudSessions, MacOSScreenLock (v1), HookInstaller, and any hook script.

## 5. Packaging and build

**csproj** (mirroring ClaudeBuddy.csproj:4-9, :29, :37-39):
- `WinExe`, net10.0, Nullable, ImplicitUsings.
- `AssemblyName` and `RootNamespace` = HatchAI; `Product` HatchAI.
- `RollForward LatestMajor`, `PublishSingleFile`, `SelfContained`, `IncludeNativeLibrariesForSelfExtract`.
- `ApplicationIcon`; `AvaloniaResource Include="Assets\**"`.
- `Compile Remove="tests\**"`, and `InternalsVisibleTo` for the test assemblies.

**Windows publish:** `dotnet publish -c Release -r win-x64` gives a single `HatchAI.exe`.

**v1 scope:** a zip or loose exe and manual start. Follow-ups: an Inno Setup installer and start-at-login (Claude Buddy uses `tools/ClaudeBuddy.iss`).

**macOS is a documented follow-up, not v1. Everything here is [ASSUMED]; I cannot test it.**
- A `build-macos-app.sh` equivalent: a bundle with `LSUIElement` (tools/build-macos-app.sh:146) and a new bundle id.
- Entitlements: `allow-jit`, `allow-unsigned-executable-memory` and `disable-library-validation` are probably still needed for a hardened .NET single-file build. Drop `automation.apple-events` and `device.audio-input`, since the buddy drives no apps and records nothing (tools/ClaudeBuddy.entitlements).
- Ad-hoc signing for local use; Developer ID plus notarization for distribution.
- Port MacOSScreenLock and ScreenLockWait (529 lines) if it will run as a Login Item: the source says starting on a locked screen crashes with CVDisplayLink -6661 (Program.cs:146-170) [SRC-CLAIM].

**Why defer macOS:** it cannot be verified from here, and a clean first milestone is "Windows working and its tests green". The code stays cross-platform (StatusDirectory, MacOSWindowExtensions and ProcessLiveness are ported) so `dotnet run` on a Mac should work as a first smoke test.

## 6. Test strategy

**Frameworks** (from the source csprojs): UnitTests and IntegrationTests use xunit 2.9.3. UiTests uses xunit.v3 3.2.2 with `Avalonia.Headless.XUnit` 12.1.1. Screenshots add `Avalonia.Skia`. New `TestBootstrap`/`TestAppBuilder` files set `HATCHAI_SETTINGS_DIR`, `_STATUS_ROOT` and `_LOG_DIR` to scratch folders. **No test may make a live model call;** the existing ones use a fake CLI (ClaudeCliBubbleEndToEndTests.cs:25-40).

**Port as-is** (namespace edit only):
- **Unit:** BubbleCliOutput, BubbleLines, BubbleLineValidator, BubblePolicy, BubblePromptRedactor, BubblePrompt, BubbleVoiceProfiles, BuddyFocus, BuddyHatch, BuddyLedger, BuddyMoments, BuddyPlacement, BuddyProgress, BuddySpriteGoldens + BuddySpriteTests (these pin appearance), BuddyStore (Parse/Write), ClaudeCliBubbleGenerator, …Flow, …Diagnostics, LatestUserPrompt, TokenFormat, TurnSignals, TickGate, CodexTranscriptItem, ChatTranscriptEdge (uses only `ImageBytes`, which stays), StatusDirectory (env rename), SingleInstance (name), CrashLogFormat (env rename).
- **Integration:** BuddyLedgerSource, ClaudeCliBubbleProcess, ClaudeCliBubbleEndToEnd, CrashLogFile, SingleInstance.
- **UI:** BuddyCard.
- **Console suite:** TranscriptTests/LedgerSuite (compile it into UnitTests).

**Adapt** (the settings class or a new seam):
- BubbleLog (settings dir).
- BuddyLedgerScanner and BuddyStoreFile (settings.json becomes HatchAI's file; drop the ClaudeBuddySettings unknown-key cases and add JsonObject round-trip cases).
- ClaudeConfigRoots.
- TranscriptReader (keep the TailLines cases only).
- CodexUsageParse (keep the `Homes` case at :115 only).
- ClaudeBinaryLocate (delete the nested `OpenClawMediaSafeNameTests`, :251ff).
- ChatPanelPlacement (keep the ClampSavedPosition cases only).
- InternalSessions (keep the pure half; its scan half becomes a StatusReader test).
- SessionSnapshotTracker (new DTO).
- UI: BuddyController, BuddyControllerAi, BuddyControllerLog, BuddyWindow; BuddySettingsUi (new window); BuddySnapshotScan (now drives StatusReader); BuddyScreenshots.

**New:** StatusReader tests.
- A scratch status folder with Claude, Codex and Grok files.
- A bash-style file and a ps1-style file (with `term_pid`).
- Deleted file → SessionEnded; dead pid dropped; superseded dropped.
- The bubble-voice, relay and grok-refresh cwds dropped.
- A torn file or a BOM skipped.
- The §2.4 expiry choice.
- **The reader never writes to the folder.**

Also a settings round-trip test and a single-instance test.

**Drop:** HeadlessSnapshot, SessionScan, SettingsSection*, ChatTranscriptImageRefusal, SpeechSummaryInvocation, TerminalScripts, hook-script subprocess tests, and anything about orbs, OpenClaw or remote.

**v1 "done":**
1. On Windows, the build and all ported unit, integration and headless-UI suites are green, including the sprite goldens.
2. A manual Windows smoke test with Claude Buddy running and its hooks installed:
   - the buddy appears;
   - Started, Responded and NeedsAttention bubbles fire;
   - tokens rise within about 10 s of a reply;
   - hide/show from the tray and from Settings works;
   - preferences and position survive a restart;
   - rebirth works through the card;
   - with AI bubbles on (the owner's own live call, not the agent's), **no orb appears in Claude Buddy** for the bubble calls, and the ledger does not count them;
   - the coexistence banner shows when Claude Buddy's own buddy is on.

**Follow-ups:** screenshots, macOS, and coverage gates.

## 7. Risks, gaps and unknowns

### 7.1 macOS (all [ASSUMED])

- **Status path:** the hooks use TMPDIR and the app uses the confstr fallback when launchd gives no TMPDIR. This is unverified here.
- **ShowOnAllSpaces and AcceptFirstClick:** they use objc runtime calls against Avalonia's NSWindow and swap `acceptsFirstMouse:` on Avalonia's view class for the whole process (MacOSWindowExtensions.cs:39-48, :145-169). Presumed to behave the same in a separate app.
- **Named Mutex** cross-process behaviour on macOS .NET.
- **The `SpecialFolder` mapping** (§3).
- **Tray/NSStatusItem with `ShowInDock=false`.**
- **Code signing and notarization.**
- **The screen-lock crash on login.**
- **The `claude` CLI location** (ClaudeBinary.cs:45-50, 82-87).
- **`disableAllHooks` on macOS:** docs/buddy-design.md:230 already marks it as assumed.

### 7.2 Second-process file hazards

- **Only hooks create status files.** SessionManager also writes and deletes: `.auto-color` (:529-530), reset-to-idle rewrites (:3812-3834, a plain `File.WriteAllText`, not atomic), sweeps (:3908), and `settings-errors.log` in the status folder (ClaudeBuddySettings.cs:2495-2498).
- **Torn reads:** the hooks' writes are not atomic either (bash `>` truncates, sh:578-580; ps1 WriteAllText, :407). A torn read is skipped. This is **the same hazard Claude Buddy already has** (the file is skipped, then the orb removed at :2584-2591); it is not new.
- **New with a second reader:** on Windows, a handle opened without `FileShare.Delete` makes the hook's `Remove-Item` fail silently (the ps1 runs with SilentlyContinue, :27, :109). Claude Buddy opens with `FileShare.ReadWrite` only (:1852). HatchAI should add `FileShare.Delete`, so it cannot be the reader that leaves a ghost file.
- The `.auto-color` marker and `settings-errors.log` are not `*.txt`, so the enumeration ignores them.

### 7.3 Own-work-folder exclusion across two processes

This was worked through against the actual mechanics.

**(a) HatchAI's AI bubble call as seen by Claude Buddy.** There are three layers today (ClaudeCliBubbleGenerator.cs:334-345; SessionManager.cs:1929-1943).
- **Layer 1: `--settings {"disableAllHooks":true,...}`** (ClaudeCliBubbleGenerator.cs:298, :304). The hook never runs, so **no status file exists**, so no orb in any app. The source reports this was confirmed with positive and negative controls on Windows (docs/buddy-design.md:216) [SRC-CLAIM]; macOS is unverified. A separate process changes nothing here, because the protection is on the child call itself.
  - *Gap:* if `claude` resolves to an npm `.cmd` shim, cmd.exe re-parses the arguments, and whether the `--settings` JSON survives is **unverified** (docs :232-235). If it does not survive, the hooks fire.
- **Layer 2: `IsOwnWorkDir(cwd)` in Claude Buddy's scan** (:1943, :1234). This works for HatchAI's call **only if HatchAI keeps the leaf `claudebuddy-bubble-voice`**, and **only in a Claude Buddy build that contains this check.** It was added on this branch (BubbleVoice.cs and the SessionManager.cs change are both in `git diff develop...HEAD`), so a released or develop Claude Buddy does **not** have it.
- **Layer 3: `InternalSessions.Remember(pid)`** (:345). It is **in-process only** (a static HashSet, InternalSessions.cs:37). Claude Buddy cannot see HatchAI's pids, so this layer is **gone** across apps.
- **Net:** for Claude Buddy on develop/release, protection rests on Layer 1 alone. **Recommendation:** when the buddy is later pulled out of Claude Buddy, *keep* the `IsOwnWorkDir` cwd drop in Claude Buddy's SessionManager, and keep `WorkDirLeaf` identical in both, as a written cross-app contract.

**(b) Claude Buddy's own `claude -p` calls as seen by HatchAI.**
- **SpeechSummary runs with hooks enabled** (SessionManager.cs:1940-1942 says the summariser "does not" disable hooks). On this branch its cwd is `BubbleVoice.WorkDir` (SpeechSummary.cs:298) with `--no-session-persistence` (:332). Its pid is hidden only by *Claude Buddy's* InternalSessions (:362). **HatchAI must filter it by cwd (rule 5)**; the pid is invisible to it.
- **On an older Claude Buddy,** the summariser runs from bare `%TEMP%` *without* `--no-session-persistence` (diff: the old `NeutralWorkingDirectory => Path.GetTempPath()`). HatchAI would then show bubbles for it, and its ledger would count those transcripts (small Haiku calls, and only if the user has spoken summaries on). Optional mitigation: drop sessions whose cwd equals the OS temp root exactly. It is a narrow heuristic that InternalSessions' header (:15-23) argues against for orbs, but a missed bubble costs little here. This is an owner decision.
- **UsagePoller** disables hooks (UsagePoller.cs:369-375), so it is invisible.
- **GrokUsageRefresh** (macOS only, cwd `claude-buddy-grok-refresh`, :171-172, :191) is unfiltered even in Claude Buddy, so add rule 6.
- **Relay leftovers:** keep rule 3.

**(c) The ledger.** `--no-session-persistence` means no transcript. The scanner also skips project folders ending `-claudebuddy-bubble-voice` (BuddyLedgerScanner.cs:128). This holds across processes because it keys on files, not pids.

### 7.4 Partial parity of the reader

Several simplifications are named in §2.3 and §2.4: no daemon or job listing, no husk detection, no transcript repair. Expect differences only around `claude agents` background sessions and backgrounded turns. Tokens are unaffected because of the 60 s discovery walk.

### 7.5 Both apps running

With Claude Buddy's `buddyEnabled` true (the default, ClaudeBuddySettings.cs:706) and HatchAI running, **two companions are drawn.** With separate state files, **tokens are not double-counted within one buddy.** Each buddy independently counts every output token once. If the user imported Claude Buddy's buddy, they will see the same pet twice, growing in parallel.

**DECIDED (owner, 2026-09-27): skip detection for v1.** The owner is fine with a temporary period of both running, since the in-app buddy is going to be removed from Claude Buddy entirely (CB-195/CB-202) once HatchAI is proven — at which point this problem stops existing rather than needing to be handled. No coexistence banner or cross-app running-check is built. **Keep the §7.3 own-work-folder contract regardless** (`WorkDirLeaf` identical in both, and Claude Buddy's `IsOwnWorkDir` scan guard kept when the in-app buddy is removed) — that protects against phantom orbs/double-counting independent of whether coexistence detection exists.

### 7.6 Other

- **The `.cmd` shim** (above).
- **The 6 s timeout** is unverified under a cold start (docs :238).
- **Codex `total_token_usage`** shape is assumed (BuddyState.cs:81-83; docs :113-115).
- **Idle-bubble behaviour** depends on the §2.4 decision.
- **The first scan is a baseline** (BuddyMoments.cs:53-56): sessions already running at launch are not announced. That is intended.

## 8. Implementation order

1. Scaffold the csproj, App and Program; port the pure files in §1a and the edits in §1b; port the unit tests. Get them green.
2. Build `HatchAISettings` and the BuddyStore/BubbleLog edits, with their integration tests.
3. Build `StatusReader` and its tests (§2).
4. Wire BuddyWindow, the controller, the tray and the settings window; adapt the UI tests.
5. Coexistence detection and the optional import.
6. Windows publish and the manual smoke test (§6).
7. Follow-ups: macOS packaging and verification, screenshots, and optionally BackgroundJobs parity.

### Critical files for implementation
- K:\Programming\personalProjects\Claude-Buddy-buddy\SessionManager.cs (the filter rules to copy: :785-1149, :1445-1596, :1830-1949, :2688-2695)
- K:\Programming\personalProjects\Claude-Buddy-buddy\BuddyController.cs
- K:\Programming\personalProjects\Claude-Buddy-buddy\ClaudeBuddySettings.cs (the buddy keys and the atomic-save pattern)
- K:\Programming\personalProjects\Claude-Buddy-buddy\StatusDirectory.cs, with ClaudeBuddyHook.sh and ClaudeBuddyHook.ps1 (the status file contract)
- K:\Programming\personalProjects\Claude-Buddy-buddy\BuddyWindow.axaml.cs and SettingsWindow.cs:763-933

## 9. Owning the hooks (2026-09-27)

Until this change HatchAI read status files that only Claude Buddy's hooks wrote, so it worked only on a machine that also had Claude Buddy installed. The owner asked for HatchAI to be fully standalone.

### 9.1 What was forked

- **The hook:** `Hooks/HatchAIHook.ps1` and `Hooks/HatchAIHook.sh`, from `ClaudeBuddyHook.ps1`/`.sh` at the source HEAD above, **verbatim below a new header**. The status file is the contract §2.2 describes and StatusReader was built against, so the fork writes the same keys to the same folder, and `Sessions/StatusReader.cs` did not change. That includes the auto-colour branch: it only runs when Claude Buddy's own `.auto-color` marker is present (HatchAI never writes it), and keeping it means both hooks write the same colour when it is.
- **The installers:** `install-windows-hooks.ps1` (Claude Code), `install-codex-hooks.ps1`, `install-grok-hooks.ps1`, and the all-CLIs entry point `install-hooks.ps1`, with their macOS `.sh` twins (ported, never run). The merge they share is in `hatchai-hooks-common.ps1`.
- **Not forked:** WSL distro wiring, Claude Buddy's crash keep-alive LaunchAgent, and its README JSON snippets.

### 9.2 Merging, not clobbering

The one rule that lets two apps share a settings file is the source's own: an entry is an app's if its command names that app's hook script by filename; strip your own, append fresh. The rename is load-bearing — `HatchAIHook.ps1` vs `ClaudeBuddyHook.ps1` — because it is what makes each installer blind to the other's entries.

The merge itself was *not* copied, because measurement showed the source's could change things that were not its own: its `ConvertTo-HashtableDeep` ended in `return @(...)`, which PowerShell unrolls, so `{"allow":["Bash(ls)"],"deny":[]}` came back as `{"allow":"Bash(ls)","deny":{}}` under 5.1 (`"deny":null` under pwsh). The fork preserves arrays, key order, and any group or event holding none of its entries; serialises at depth 100 rather than 20; writes via a temp file that is parsed back first; and writes nothing when the result is unchanged (which matters for Codex, whose hook trust is keyed on the file's hash). A second source bug, in the Windows Codex installer's untrimmed `-TempDir "...\Temp\"`, was confirmed by running the command line and fixed.

The test that pins this (`HookInstallerTests`) removes HatchAI's entries from the installed file and requires the remainder to deep-equal the original — against a fixture that carries Claude Buddy's real entries. With the source's unrolling put back, it fails on both engines.

### 9.3 Decision: the status folder stays `claude_buddy`

Kept, deliberately. It is an internal name no user sees. Renaming it would touch `StatusDirectory`, the reader's tests, and every hook script, for no user-visible benefit, and it would make a machine with both apps installed write the same session into two folders that could disagree. Keeping it means both apps' hooks write one identical file per session, which either app reads. No collision risk was found that would argue otherwise: the only writers of `*.txt` there are the two hooks, writing the same format.

The cost, named: with both apps' hooks installed, each event runs two hooks that write the same file, so a torn read (already the known hazard in §7.2) is somewhat more likely; the reader already skips a torn read and retries next tick.

### 9.4 The app side

`App/HookSetup.cs` carries the scripts embedded in the executable (HatchAI publishes as a single file), writes them to a temp folder per run, runs `install-hooks.ps1` under Windows PowerShell 5.1, and reports a per-CLI summary. Settings gained a *Sessions → Session hooks* row with the current state and an **Install hooks** button. No first-run prompt: installing edits other programs' settings, so it happens when asked, and on the only machine HatchAI had run on, Claude Buddy's hooks already served it — a prompt there would have been a false alarm. The row names that case explicitly.
