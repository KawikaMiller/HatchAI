using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;

namespace HatchAI
{
    // Which sessions exist, and what state each is in — read from the status
    // files Claude Buddy's hooks write, and handed to the buddy as snapshots.
    //
    // This replaces Claude Buddy's SessionManager for the one question the
    // buddy asks of it. SessionManager is four thousand lines because it also
    // draws orbs, links agent teams, asks the background-job daemon, repairs
    // transcripts, talks to gateways and writes back into the status folder;
    // none of that is the buddy's business. What is kept is the filter chain
    // that decides *which* sessions the buddy sees, because the buddy's
    // bubbles depend on exactly that set — a session that flickers out and
    // back is a fake "ended" and a fake "started", both said out loud. The
    // rules below are copied from SessionManager verbatim where they exist,
    // with their reasoning, and the places HatchAI deliberately differs are
    // named where they happen (the extraction plan's §2.3 has the table).
    //
    // **This class never writes, creates or deletes anything in the status
    // folder.** Claude Buddy writes there (an .auto-color marker, reset-to-idle
    // rewrites, sweeps of dead files, a settings error log); HatchAI is a second
    // reader of a folder another app owns, and the hooks are the only writers
    // it should ever have. Every file is opened read-only, and shared for
    // writing *and deleting* — see ReadFile for why the second one matters.
    //
    // A poll every two seconds, like SessionManager's own timer, and no
    // FileSystemWatcher: the buddy's contract is already that turns shorter
    // than a scan are not seen (BuddyMoments' header), and a watcher would add
    // debounce machinery to report the same thing sooner.
    internal sealed class StatusReader : IDisposable
    {
        internal static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(2);

        // How long a file that names no process may sit untouched before it is
        // taken for a leftover. Claude Buddy's default orb lifetime; see
        // JudgeLiveness for why only pid-less files are subject to it here.
        internal static readonly TimeSpan PidlessLifetime = TimeSpan.FromMinutes(5);

        // The prefix Claude Buddy gave a remote-control relay's scratch
        // directory. Relays are real Claude Code sessions whose hooks fire like
        // anyone's, and leftover ones can run for days; see
        // LooksLikeALeftoverRelay.
        private const string RelayPrefix = "claude-buddy-rc-";

        // The scratch folder Claude Buddy's macOS Grok usage probe runs in
        // (GrokUsageRefresh.ScratchDirectory). It starts a real `grok` session
        // whose hook writes a status file, and Claude Buddy does not filter it
        // — so without this HatchAI would greet a probe every twenty minutes.
        internal const string GrokRefreshLeaf = "claude-buddy-grok-refresh";

        // Lenient about numbers written as strings, so a hook that quotes a pid
        // costs nothing rather than the whole file.
        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        private readonly string _statusDir;
        private readonly Func<int, bool> _isRunning;
        private readonly Func<string, bool> _transcriptExists;
        private readonly Func<DateTime> _clock;
        private readonly SessionSnapshotTracker _tracker = new();
        private readonly TickGate _gate = new();
        private DispatcherTimer? _timer;

        // Wired by App to BuddyController.OnSnapshots. Always invoked on the UI
        // thread.
        internal Action<IReadOnlyList<SessionSnapshot>>? SnapshotsPublished { get; set; }

        // Every seam is optional: the app passes none, and a test passes a
        // scratch folder, a liveness answer and a clock.
        internal StatusReader(
            string? statusDir = null,
            Func<int, bool>? isRunning = null,
            Func<string, bool>? transcriptExists = null,
            Func<DateTime>? clock = null)
        {
            _statusDir = statusDir ?? StatusDirectory.Path();
            _isRunning = isRunning ?? ProcessLiveness.IsRunning;
            _transcriptExists = transcriptExists ?? File.Exists;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        internal string StatusDir => _statusDir;

        [ExcludeFromCodeCoverage(Justification = "Timer wiring; ScanAsync, which the tick calls, is covered.")]
        internal void Start()
        {
            _timer = new DispatcherTimer { Interval = ScanEvery };
            _timer.Tick += (_, _) => _ = ScanAsync();
            _timer.Start();
            _ = ScanAsync();
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer = null;
        }

        // One scan: the disk half off the UI thread, then the snapshots built
        // and published back on it. TickGate so a slow disk cannot let two
        // scans overlap and publish out of order.
        internal async Task ScanAsync()
        {
            if (!_gate.TryEnter()) return;
            try
            {
                var now = _clock();
                var kept = await Task.Run(() => Scan(_statusDir, now, _isRunning, _transcriptExists));
                Publish(kept, now);
            }
            catch (Exception ex)
            {
                // Nothing here is expected to throw — every file read already
                // survives on its own — so anything that does is recorded and
                // the next tick simply tries again.
                CrashLog.Record("StatusReader.Scan", ex);
            }
            finally
            {
                _gate.Exit();
            }
        }

        // The UI-thread half: snapshots from what survived the rules, handed to
        // whoever is listening. Separate so a test can drive a scan without a
        // dispatcher.
        internal IReadOnlyList<SessionSnapshot> Publish(IReadOnlyList<ScanEntry> kept, DateTime now)
        {
            var snapshots = _tracker.Build(
                kept.Select(e => (e.SessionId, e.Status)),
                new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)));
            SnapshotsPublished?.Invoke(snapshots);
            return snapshots;
        }

        // ---- the disk half ---------------------------------------------------

        // Read the folder, then judge what was read. Pure apart from the
        // reads, and static so a test can point it at a scratch folder with
        // its own liveness answer.
        internal static List<ScanEntry> Scan(
            string statusDir, DateTime now, Func<int, bool> isRunning, Func<string, bool>? transcriptExists = null) =>
            Judge(ReadDirectory(statusDir), now, isRunning, transcriptExists ?? File.Exists);

        // Every *.txt in the folder that parses and is not somebody's own
        // plumbing. Only *.txt: Claude Buddy's .auto-color marker and its
        // settings-errors.log live in the same folder and are ignored by the
        // pattern alone.
        internal static List<ScanEntry> ReadDirectory(string statusDir)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(statusDir, "*.txt").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No folder yet (no hook has ever run) or not readable: no
                // sessions, which is the truth as far as anyone can tell.
                return new List<ScanEntry>();
            }

            var found = new List<ScanEntry>();
            foreach (var file in files)
            {
                if (ReadFile(file) is not { } entry) continue;
                if (IsOwnPlumbing(entry.Status)) continue;
                found.Add(entry);
            }

            return found;
        }

        // One status file, or null when it cannot be read this tick — mid-write
        // (the hooks' writes are not atomic: bash truncates, PowerShell's
        // WriteAllText does too), vanished between the listing and the open,
        // or not a status object at all. Skipped, not remembered: the next
        // tick reads it again. That is the same hazard Claude Buddy has had
        // all along, and a torn read is rare enough there that it is not worth
        // a cache that could keep a genuinely deleted file alive.
        //
        // **FileShare.Delete is the one thing this does that Claude Buddy's
        // reader does not, and it is the reason a second reader is safe.** On
        // Windows a handle opened without it makes the hook's Remove-Item fail
        // while this app holds the file — and the hook runs with
        // SilentlyContinue, so the failure is silent and the session's file
        // outlives the session. Claude Buddy opens with ReadWrite only, which
        // it can afford because it is also the process that sweeps; HatchAI
        // never sweeps, so it must never be the reader that leaves a ghost.
        internal static ScanEntry? ReadFile(string path)
        {
            StatusFile? status;
            DateTime written;
            try
            {
                written = File.GetLastWriteTimeUtc(path);
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                status = JsonSerializer.Deserialize<StatusFile>(stream, ReadOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }

            if (status is null) return null;

            status.Source = SourceOf(status);
            return new ScanEntry(Path.GetFileNameWithoutExtension(path), status, written);
        }

        // Rules 3 to 6 of the plan's table: sessions that are real Claude Code
        // (or Grok) processes with real hooks, but that some app started for
        // its own purposes rather than the user. Dropped at the read, as Claude
        // Buddy drops them, so nothing downstream — the pid grouping included —
        // behaves as though they were running.
        internal static bool IsOwnPlumbing(StatusFile status) =>
            // Claude Buddy's leftover remote-control relays: a Claude Code
            // session per account in a tmux pane, possibly running for days
            // after the upgrade that removed the relay.
            LooksLikeALeftoverRelay(status.Cwd)
            // This app's own AI-bubble call, by pid. In-process only — see
            // InternalSessions' header.
            || InternalSessions.IsInternal(status.SessionPid)
            // The same call and, critically, Claude Buddy's spoken-summary
            // `claude -p`, which runs *with hooks enabled* from the same
            // folder. HatchAI cannot see Claude Buddy's pids, so the folder is
            // the only thing that identifies it here.
            || BubbleVoice.IsOwnWorkDir(status.Cwd)
            // Claude Buddy's macOS Grok usage probe.
            || LeafOf(status.Cwd).Equals(GrokRefreshLeaf, StringComparison.OrdinalIgnoreCase);

        // Copied from Claude Buddy's MachineNames.LooksLikeALeftoverRelay, with
        // TerminalScripts.LeafOf replaced by the same leaf split
        // BubbleVoice.IsOwnWorkDir uses. Claude Buddy's comment: delete this
        // when a release has been out long enough that no relay from before it
        // is plausibly still running, and not before.
        internal static bool LooksLikeALeftoverRelay(string? cwd) =>
            !string.IsNullOrEmpty(cwd)
            && LeafOf(cwd).StartsWith(RelayPrefix, StringComparison.OrdinalIgnoreCase);

        // The last segment of a path on either separator, trailing separators
        // ignored — a WSL session's cwd is a POSIX path whichever OS this runs
        // on.
        internal static string LeafOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var trimmed = path.TrimEnd('/', '\\');
            return trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        }

        // Verbatim from SessionManager: absent means Claude Code rather than
        // unknown, so every hook older than the `cli` field reads correctly.
        internal static SessionSource SourceOf(StatusFile status)
        {
            if (string.Equals(status.Cli, "grok", StringComparison.OrdinalIgnoreCase))
                return SessionSource.Grok;
            if (string.Equals(status.Cli, "codex", StringComparison.OrdinalIgnoreCase))
                return SessionSource.Codex;
            return SessionSource.ClaudeCode;
        }

        // ---- the rules -------------------------------------------------------

        // What survives, in the order the files were read. Superseded is
        // computed over everything read first, because whether a file is live
        // can depend on the *other* files.
        internal static List<ScanEntry> Judge(
            List<ScanEntry> found, DateTime now, Func<int, bool> isRunning, Func<string, bool> transcriptExists)
        {
            // No daemon to ask: see the plan's rule 7. Extra Agent View
            // sessions that share one pid therefore lose their bubbles; their
            // tokens are still counted by the ledger's 60 s walk.
            var superseded = Superseded(found, isLiveJob: _ => false);

            var kept = new List<ScanEntry>();
            foreach (var entry in found)
            {
                if (JudgeLiveness(entry.SessionId, entry.Status, entry.Written, now, superseded, isRunning)
                    != ScanVerdict.Keep) continue;
                if (JudgeReachability(entry.Status, transcriptExists) != ScanVerdict.Keep) continue;
                kept.Add(entry);
            }

            return kept;
        }

        // Session ids one process has already moved on from.
        //
        // Verbatim from SessionManager, whose comment explains it: a Claude
        // Code process mints a new session id on every /clear, resume or new
        // conversation, the hook writes a new <id>.txt for each, and nothing
        // deletes the old ones while the process lives — so one terminal
        // accumulates several files that all name a live pid, the stale ones
        // frozen at whatever state they were last written with. Within one
        // process only the newest file is the live session.
        //
        // `isLiveJob` is how Claude Buddy tells a stale /clear id from a live
        // Agent View background session sharing the pid: it asks
        // `claude agents --json`. HatchAI passes `_ => false` rather than
        // shelling out every two seconds, which is the partial-parity choice
        // the plan names.
        //
        // Keyed by pid *and* CLI: a Codex file written by `codex exec` running
        // as a Claude Code Bash tool can record the Claude Code session's pid,
        // and being newer would otherwise supersede the session doing the
        // work. A pid of 0 (a hook older than the field) is left alone.
        internal static HashSet<string> Superseded(List<ScanEntry> found, Func<string, bool> isLiveJob)
        {
            var newest = new Dictionary<(int Pid, SessionSource Source), ScanEntry>();

            foreach (var entry in found)
            {
                var pid = (entry.Status.SessionPid, entry.Status.Source);
                if (pid.SessionPid <= 0) continue;

                // The ordinal tie-break only matters if two files somehow share an
                // mtime, and exists so the choice doesn't depend on the order the
                // directory happened to enumerate in.
                if (!newest.TryGetValue(pid, out var best)
                    || entry.Written > best.Written
                    || (entry.Written == best.Written
                        && string.CompareOrdinal(entry.SessionId, best.SessionId) > 0))
                {
                    newest[pid] = entry;
                }
            }

            var stale = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in found)
            {
                var pid = (entry.Status.SessionPid, entry.Status.Source);
                if (pid.SessionPid <= 0) continue;

                if (newest.TryGetValue(pid, out var best) && best.SessionId != entry.SessionId
                    && !isLiveJob(entry.SessionId))
                {
                    stale.Add(entry.SessionId);
                }
            }

            return stale;
        }

        // Superseded, a dead process, and expiry.
        //
        // **Expiry is where HatchAI deliberately differs from Claude Buddy**
        // (owner decision on the plan's §2.4, rule B). Claude Buddy expires any
        // quiet file after its orb lifetime — five minutes by default — unless
        // it is waiting. For an orb that is harmless. For the buddy it is not:
        // an idle session whose process is alive, with its file's mtime frozen
        // because nothing is happening, disappears after five minutes, and the
        // buddy says "session ended"; the next prompt brings it back and the
        // buddy says "session started". And the long-idle moment (15 minutes)
        // can then never fire at all.
        //
        // So here **a file whose recorded process is alive never expires from
        // idling alone** — it goes when the process goes (ProcessGone) or when
        // the hook deletes it (SessionEnd). Only a file that names no process
        // — an older hook, a subagent leftover, which Claude Buddy would check
        // against the daemon and HatchAI cannot — expires after
        // PidlessLifetime, with Claude Buddy's exemption for "waiting", whose
        // mtime freezes precisely while it matters most.
        //
        // No backgrounded-husk rule (the plan's rule 9): a turn handed to a
        // background job can leave a file frozen at "generating" until its pid
        // dies. A follow-up if it is ever seen.
        internal static ScanVerdict JudgeLiveness(
            string sessionId, StatusFile status, DateTime written, DateTime now,
            ISet<string> superseded, Func<int, bool> isRunning)
        {
            if (superseded.Contains(sessionId)) return ScanVerdict.Superseded;

            // Gone is gone — the Ctrl+C case, which fires no SessionEnd and so
            // leaves the file behind. Applies to "waiting" as well.
            if (status.SessionPid > 0 && !isRunning(status.SessionPid)) return ScanVerdict.ProcessGone;

            if (status.SessionPid <= 0
                && status.State != "waiting"
                && now - written > PidlessLifetime)
            {
                return ScanVerdict.Expired;
            }

            return ScanVerdict.Keep;
        }

        // The pure part of SessionManager.JudgeReachability, with the job
        // phase fixed at Unknown because there is no daemon listing to read
        // (and no agent teams, so no lead is exempt). With the phase Unknown
        // Claude Buddy's own rules reduce to exactly these three, so what is
        // below is what SessionManager would decide for a session the daemon
        // could not be asked about.
        internal static ScanVerdict JudgeReachability(StatusFile status, Func<string, bool> transcriptExists)
        {
            // No terminal to jump to and no transcript to read: in Claude Buddy
            // an orb whose click goes nowhere and whose chat opens blank —
            // measured there as an unprompted background job, and as daemon
            // workers parked under a pty host. For the buddy it is a session
            // with no conversation in it, which has nothing to say. An *empty*
            // transcript path keeps the session: not knowing where the
            // conversation is is not knowing there is none.
            if (status.IsLocalCli
                && !KnowsATerminal(status)
                && !string.IsNullOrEmpty(status.TranscriptPath)
                && !transcriptExists(status.TranscriptPath))
            {
                return ScanVerdict.NothingToShow;
            }

            // A Codex or Grok session with a live process and no terminal of
            // any kind: a headless or bridged invocation. Claude Code is exempt
            // while the job phase is unknown, because a background job has no
            // terminal by nature and nothing here can rule one out.
            if (status.IsLocalCli
                && string.IsNullOrEmpty(status.Tty)
                && string.IsNullOrEmpty(status.TermProgram)
                && string.IsNullOrEmpty(status.TmuxPane)
                && status.TermPid == 0
                && status.Source != SessionSource.ClaudeCode
                && status.SessionPid > 0)
            {
                return ScanVerdict.NoTerminal;
            }

            // A Codex or Grok file naming no process is a session that ended
            // without clearing up: neither CLI has a background job to attach
            // to. (Claude Code's pid-less files would be checked against the
            // daemon in Claude Buddy; here expiry covers them instead.)
            if ((status.Source == SessionSource.Codex || status.Source == SessionSource.Grok)
                && status.SessionPid <= 0)
            {
                return ScanVerdict.NotALiveJob;
            }

            return ScanVerdict.Keep;
        }

        // Verbatim from SessionManager: whether a status file says anything
        // about where its session can be seen. A tty alone doesn't count: it
        // is the one field the walk always fills in, and on its own it can
        // name a tmux pane's pty or a daemon worker's pty host.
        internal static bool KnowsATerminal(StatusFile status) =>
            !string.IsNullOrEmpty(status.TmuxPane)
            || !string.IsNullOrEmpty(status.TermProgram)
            || !string.IsNullOrEmpty(status.TermId)
            || status.TermPid != 0;
    }

    // One file as read: its session id (the filename), what it says, and when
    // it was last written (UTC).
    internal sealed record ScanEntry(string SessionId, StatusFile Status, DateTime Written);

    // Why a file was or was not kept. The names are Claude Buddy's, for the
    // subset HatchAI can reach.
    internal enum ScanVerdict
    {
        Keep,
        Superseded,
        ProcessGone,
        Expired,
        NoTerminal,
        NotALiveJob,
        NothingToShow,
    }
}
