using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;

namespace HatchAI
{
    // The file half of counting: discovery, reads and BuddyLedger.Read. The
    // controller reaches it only through this, so it can be driven with a fake
    // and never touches a disk in a test. Called off the UI thread, and answers
    // with E2's delta (LedgerScanResult) so the credit is applied — and
    // evolution noticed — in one place, on the UI thread, against the state as
    // it is *now* rather than as it was when the round started.
    internal interface IBuddyLedgerSource
    {
        // `livePaths` are the transcripts of sessions on screen (cheap, every
        // round); `discover` asks for the slower walk over the config roots.
        LedgerScanResult Scan(BuddyState state, IReadOnlyList<string> livePaths, bool discover);
    }

    // The production source: BuddyLedgerScanner's two cadences behind the one
    // call. One scanner for the life of the app, since its size-and-mtime
    // memory is what keeps the 60 s walk cheap; the controller's TickGate is
    // what keeps it to one scan at a time, which that memory needs.
    internal sealed class BuddyLedgerSource : IBuddyLedgerSource
    {
        private readonly BuddyLedgerScanner _scanner;

        internal BuddyLedgerSource(BuddyLedgerScanner? scanner = null) => _scanner = scanner ?? new();

        public LedgerScanResult Scan(BuddyState state, IReadOnlyList<string> livePaths, bool discover) =>
            discover ? _scanner.ScanAll(state) : _scanner.ScanPaths(state, livePaths);
    }

    // The creature engine's functions as values, so the controller can be
    // driven with a deterministic one: what a controller test asserts is which
    // bubble was raised and when, not which of eighteen species a uuid hashes
    // to, and the second is E1's golden tests' business. Production passes
    // Default, which is the real thing.
    internal sealed record BuddyRules(
        Func<string, int, BuddyGenome> Roll,
        Func<BuddyState, long, BuddyState> Credit,
        Func<long, BuddyStage> Stage,
        Func<long, int> Stars,
        Func<BuddyState, BuddyGenome, DateTimeOffset, BuddyState> Rebirth,
        Func<BuddyMoment, BuddyGenome, int, string> Pick)
    {
        internal static readonly BuddyRules Default = new(
            BuddyHatch.Roll,
            BuddyProgress.Credit,
            BuddyProgress.StageFor,
            BuddyProgress.StarsFor,
            BuddyProgress.Rebirth,
            BubbleLines.Pick);
    }

    // The one object that holds a clock, a store and a view at once (CB-195).
    // It turns session snapshots into bubbles, turns transcript tokens into
    // progress, and owns the two timers that drive them. Everything it decides
    // is delegated to a pure function (BuddyMoments, BuddyFocus, BubblePolicy,
    // BuddyProgress, BubbleLines); what is left here is sequencing, which is
    // exactly what a fake view, store and ledger can observe.
    //
    // UI thread only, except the ledger scan, which is handed a copy of the
    // state and hands one back, and a generated line (CB-202), whose process
    // work the generator does off the UI thread by itself — the controller
    // only builds the request here and gets the answer back here. The timer
    // tick handlers are single lines that call the covered methods below, for
    // the same reason TurnSounds' are.
    internal sealed class BuddyController : IDisposable
    {
        // The running app's controller, so the tray and the settings window can
        // say "the preference changed" without holding a reference of their
        // own. Null outside the app, which is what makes those calls no-ops in
        // a test.
        internal static BuddyController? Instance { get; set; }

        // The controller the running app uses: the real store, the real
        // scanner, the real CLI voice and the three settings, with `view`
        // (E3's BuddyWindow) handed in because it is a window and this class
        // is not. Registers itself as Instance; the caller wires the session
        // snapshots and calls Start().
        //
        // The generator is always constructed and costs nothing until asked:
        // whether it is ever asked is the opt-in, read fresh at every bubble,
        // so switching it on or off in Settings needs no rewiring.
        internal static BuddyController CreateForApp(IBuddyView view)
        {
            var controller = new BuddyController(
                view, new BuddyStore(), new BuddyLedgerSource(),
                () => HatchAISettings.BuddyEnabled,
                () => HatchAISettings.BuddyBubblesEnabled,
                generator: new ClaudeCliBubbleGenerator(),
                aiEnabled: () => HatchAISettings.BuddyAiBubblesEnabled,
                idleEnabled: () => HatchAISettings.BuddyIdleBubblesEnabled,
                log: new BubbleLogFile(),
                logEnabled: () => HatchAISettings.BuddyBubbleLogEnabled);
            controller._ownsLog = true;
            Instance = controller;
            return controller;
        }

        internal static readonly TimeSpan LedgerEvery = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan DiscoverEvery = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan PumpEvery = TimeSpan.FromSeconds(1);
        internal static readonly TimeSpan BubbleFor = TimeSpan.FromSeconds(6);

        // How often counting may write settings.json — the whole file,
        // synchronously, on the UI thread. A live session moves its cursor
        // every ledger round, so without this it was every ten seconds for as
        // long as anything was typing (QA on CB-195). Evolution, rebirth,
        // hiding and quitting still write at once.
        //
        // Delaying a save does not weaken the crash story, because what is
        // delayed is the whole state — counts, cursors and recent ids
        // together, as ever. A crash inside the window loses all three at
        // once, the next run starts from the cursors that match the counts on
        // disk, and it reads those bytes again: re-counted, never lost and
        // never counted twice.
        internal static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);

        private readonly IBuddyView _view;
        private readonly IBuddyStore _store;
        private readonly IBuddyLedgerSource _ledger;
        private readonly Func<bool> _buddyEnabled;
        private readonly Func<bool> _bubblesEnabled;
        private readonly Func<DateTimeOffset> _clock;
        private readonly BuddyRules _rules;

        // CB-202: the generated-line source and its opt-in. A null generator
        // or an aiEnabled that answers false is exactly today's behaviour: the
        // table, and no process. Both are consulted only at the moment a
        // bubble is about to be shown — after BubblePolicy.Decide has said
        // yes — so the gap, the cooldowns and deferral bound how often a call
        // can be made, and a deferred moment spends nothing until it is shown.
        private readonly IBubbleGenerator? _generator;
        private readonly Func<bool> _aiEnabled;
        private readonly TickGate _gate = new();

        // CB-202, after live use. Whether the long-idle moment may speak at
        // all: checked before the policy is even asked, so with it off an idle
        // stretch spends no cooldown and holds no gap — it simply did not
        // happen, as far as the buddy's voice is concerned.
        private readonly Func<bool> _idleEnabled;

        // Where every decision is written, and whether to. Null writes
        // nothing; the switch is read fresh at every decision. The factory's
        // log is this controller's to flush on quit; a test's is the test's.
        private readonly IBubbleLog? _log;
        private readonly Func<bool> _logEnabled;
        private bool _ownsLog;

        private bool _shown;
        private BuddyState? _state;
        private BuddyGenome? _genome;

        // The latest scan and the one before it. The one before is kept only
        // so a moment about a session that has just gone (SessionEnded) can
        // still name its project.
        private IReadOnlyList<SessionSnapshot>? _previous;
        private IReadOnlyList<SessionSnapshot>? _before;
        private DateTimeOffset? _previousAt;
        private string? _focus;
        private IReadOnlyList<string> _livePaths = Array.Empty<string>();

        private BubblePolicyState _policy = BubblePolicyState.Empty;
        private BuddyMoment? _pending;
        private string? _pendingSession;
        private DateTimeOffset _pendingAt;

        // The one generation call in flight, or null. Everything needed to
        // decide, when it answers or when Pump looks at it, whether its moment
        // is still the one to say — and, if it is not going to answer, what
        // the table would have said in its place.
        //
        // A class, not a record: a flight is only ever compared by identity
        // (is this answer for the call still in flight?), and value equality
        // would be a second, wrong answer to that question.
        private sealed class Flight
        {
            internal required CancellationTokenSource Cts { get; init; }
            internal required BuddyMoment Moment { get; init; }
            internal required string? SessionId { get; init; }
            // The session's state and the buddy's focus when the call was
            // made; either moving on makes the call stale.
            internal required string? StateAtRequest { get; init; }
            internal required string? FocusAtRequest { get; init; }
            // The draw the table line would have used, taken at the decision
            // so a fallback says exactly what the table would have.
            internal required int Draw { get; init; }
            internal required DateTimeOffset Deadline { get; init; }
            // When the line was asked for, for the log's latency.
            internal required DateTimeOffset AskedAt { get; init; }
        }

        private enum FlightVerdict { Keep, Drop, Table }

        private Flight? _flight;
        private DateTimeOffset? _bubbleHideAt;
        private int _draw;
        private DateTimeOffset? _lastDiscover;

        // When the state on disk last matched _state, and whether it has
        // moved on since.
        private DateTimeOffset _lastSave;
        private bool _unsaved;

        // Bumped by Reload. A ledger round that started before the buddy on
        // disk was replaced read against the old buddy's cursors, so its answer
        // must not be applied to the new one.
        private int _epoch;

        private DispatcherTimer? _ledgerTimer;
        private DispatcherTimer? _pumpTimer;

        internal BuddyController(
            IBuddyView view,
            IBuddyStore store,
            IBuddyLedgerSource ledger,
            Func<bool> buddyEnabled,
            Func<bool> bubblesEnabled,
            Func<DateTimeOffset>? clock = null,
            BuddyRules? rules = null,
            IBubbleGenerator? generator = null,
            Func<bool>? aiEnabled = null,
            Func<bool>? idleEnabled = null,
            IBubbleLog? log = null,
            Func<bool>? logEnabled = null)
        {
            _rules = rules ?? BuddyRules.Default;
            _view = view;
            _store = store;
            _ledger = ledger;
            _buddyEnabled = buddyEnabled;
            _bubblesEnabled = bubblesEnabled;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _generator = generator;
            _aiEnabled = aiEnabled ?? (() => false);
            // Off unless asked for, like the setting it stands for.
            _idleEnabled = idleEnabled ?? (() => false);
            _log = log;
            _logEnabled = logEnabled ?? (() => true);
            _view.RebirthRequested += OnRebirthRequested;
            _view.HideRequested += Reapply;
        }

        // The state as last known, for the settings section and tests.
        internal BuddyState? State => _state;
        internal string? Focus => _focus;
        internal bool Shown => _shown;

        // Whether a generation call is waiting, and the factory's wiring, for
        // tests — the second so CreateForApp can be checked without a call
        // ever reaching a real CLI.
        internal bool Generating => _flight is not null;
        internal IBubbleGenerator? Generator => _generator;
        internal bool AiEnabled => _aiEnabled();
        internal bool IdleEnabled => _idleEnabled();
        internal bool LogEnabled => _logEnabled();
        internal IBubbleLog? BubbleLog => _log;

        // Wires the timers and applies the current preference. Separate from
        // the constructor so a test can build a controller and drive it by
        // hand without a dispatcher timer firing underneath it.
        [ExcludeFromCodeCoverage(Justification = "Timer tick lambdas; the methods they call are covered.")]
        internal void Start()
        {
            _ledgerTimer = new DispatcherTimer { Interval = LedgerEvery };
            _ledgerTimer.Tick += (_, _) => _ = LedgerRoundAsync();
            _ledgerTimer.Start();

            _pumpTimer = new DispatcherTimer { Interval = PumpEvery };
            _pumpTimer.Tick += (_, _) => Pump();
            _pumpTimer.Start();

            Reapply();
            _ = LedgerRoundAsync();
        }

        // The preference changed (settings window, tray item, the buddy's own
        // "Hide buddy" through HideRequested): make the view agree with it.
        // Cheap and idempotent, so callers never need to check whether
        // anything actually flipped.
        internal void Reapply()
        {
            var want = _buddyEnabled();
            if (want == _shown) return;
            _shown = want;

            if (want)
            {
                EnsureBuddy();
                _view.Show(_genome!, _state!);
                return;
            }

            // A line still being generated has nowhere to go: dropped, and
            // its process killed, rather than said by a buddy nobody can see.
            if (_flight is { } flight)
            {
                Cancel(flight);
                RecordFlight(flight, BubbleOutcome.Dropped, BubbleLogReason.BuddyHidden);
            }
            if (_pending is { } held) Record(held, BubbleOutcome.Dropped, BubbleLogReason.BuddyHidden);

            _view.HideBubble();
            _view.Hide();

            // Nothing counts while hidden, so nothing would come along to
            // write what the last rounds earned; write it now.
            Flush();

            // Forget what was on screen: on re-enable the next scan is a
            // baseline, so sessions already running are not announced as new.
            _previous = null;
            _before = null;
            _previousAt = null;
            _pending = null;
            _pendingSession = null;
            _bubbleHideAt = null;
        }

        // First run creates the buddy: a random uuid, once, saved at once so a
        // crash before the next save cannot hatch a different one.
        private void EnsureBuddy()
        {
            if (_state is null)
            {
                var loaded = _store.Load();
                if (loaded is null)
                {
                    loaded = BuddyState.Hatch(Guid.NewGuid().ToString(), _clock());
                    _store.Save(loaded);
                }
                _state = loaded;
                _lastSave = _clock();
            }

            _genome = _rules.Roll(_state.Uuid, _state.Rebirths);
        }

        // Called by StatusReader (through App's wiring) after each scan, on the
        // UI thread.
        internal void OnSnapshots(IReadOnlyList<SessionSnapshot> current)
        {
            if (!_shown) return;

            var now = _clock();
            _livePaths = current
                .Where(s => s.Source is SessionSource.ClaudeCode or SessionSource.Codex
                            && s.TranscriptPath.Length > 0)
                .Select(s => s.TranscriptPath)
                .ToList();

            var events = BuddyMoments.Classify(_previous, current, now, _previousAt);
            var oldFocus = _focus;
            _focus = BuddyFocus.Choose(current, oldFocus, now);
            _before = _previous;
            _previous = current;
            _previousAt = now;

            // A line being generated for something this scan has overtaken —
            // focus moved on, or its session did — is dropped before anything
            // new is said.
            CheckFlight(now);

            // One companion, one thing to say: only the focused session (or
            // the one it just left) gets a bubble, and of what it did this
            // scan only the most important thing is said. A session starting
            // is the exception — focus is sticky, so a newcomer usually is not
            // the focus yet, and a welcome that waits for it would never come.
            // The winning event is kept whole, not just its moment, because a
            // generated line needs to know whose project it is about.
            BuddyMomentEvent? best = null;
            foreach (var e in events)
            {
                if (e.Moment != BuddyMoment.SessionStarted
                    && e.SessionId != _focus && e.SessionId != oldFocus) continue;
                if (best is null || BubblePolicy.Priority(e.Moment) > BubblePolicy.Priority(best.Value.Moment))
                    best = e;
            }

            if (best is { } winner) Raise(winner.Moment, winner.SessionId, now);
        }

        // Asks the policy; shows, drops or holds the moment for later.
        private void Raise(BuddyMoment moment, string? sessionId, DateTimeOffset now)
        {
            // Idle bubbles off: the long-idle moment says nothing and, since
            // the policy is never asked, uses up nothing either.
            if (moment == BuddyMoment.LongIdle && !_idleEnabled())
            {
                Record(moment, BubbleOutcome.Suppressed, BubbleLogReason.IdleOff);
                return;
            }

            var enabled = _bubblesEnabled();
            var decision = BubblePolicy.Decide(moment, _policy, enabled, now);
            _policy = decision.Next;

            if (decision.Show)
            {
                Say(moment, sessionId, now);
                return;
            }

            if (decision.RetryAt is not { } at)
            {
                Record(moment, BubbleOutcome.Suppressed,
                    enabled ? BubbleLogReason.Cooldown : BubbleLogReason.BubblesOff);
                return;
            }

            // Deferred, not dropped — but only the most important thing
            // waiting: a later moment replaces an earlier one only if it
            // outranks it, so a chatty low-priority one cannot push out a
            // needs-attention that is still owed. Whichever loses is gone for
            // good, and the log says so.
            if (_pending is { } waiting
                && BubblePolicy.Priority(moment) <= BubblePolicy.Priority(waiting))
            {
                Record(moment, BubbleOutcome.Suppressed, BubbleLogReason.Outranked);
                return;
            }

            if (_pending is { } replaced) Record(replaced, BubbleOutcome.Dropped, BubbleLogReason.Outranked);
            _pending = moment;
            _pendingSession = sessionId;
            _pendingAt = at;
            // Logged once: Pump retries a held moment only once the gap has
            // opened, so a retry is shown or suppressed, never deferred again.
            Record(moment, BubbleOutcome.Deferred);
        }

        // The policy has said yes. Without a generator, with the opt-in off,
        // or for a moment that is not about the user's current work
        // (BuddyMoments.MayUseAi), that is the table line at once — no
        // process. Otherwise a call is started and the bubble waits for it
        // (at most GenerationTimeout, checked in Pump).
        //
        // The policy recorded LastShown at the decision, not at the show, so
        // its 20 s gap is what keeps calls apart: a Show can only come while
        // another call is still out if the pump has not run for longer than
        // the timeout. Should that happen the newer moment wins and the older
        // call is dropped, which is what keeps it to one call at a time.
        private void Say(BuddyMoment moment, string? sessionId, DateTimeOffset now)
        {
            var draw = _draw++;
            if (_generator is null || !BuddyMoments.MayUseAi(moment) || !_aiEnabled())
            {
                var line = _rules.Pick(moment, _genome!, draw);
                ShowNow(line, now);
                Record(moment, BubbleOutcome.ShownTable, source: BubbleLogEntry.TableSource, text: line);
                return;
            }

            if (_flight is { } superseded)
            {
                Cancel(superseded);
                RecordFlight(superseded, BubbleOutcome.Dropped, BubbleLogReason.Superseded);
            }

            // Built here, on the UI thread, from the copies this class already
            // holds. The transcript is not read here: the request says where
            // the prompt lives and the generator reads it off this thread.
            var session = Find(sessionId);
            var request = new BubbleRequest(
                moment,
                _genome!.Primary,
                _genome.Secondary,
                _genome.Rarity,
                _genome.Species,
                _genome.Stats,
                ProjectName(session?.Cwd),
                Prompt: null,
                TranscriptPath: session is { TranscriptPath.Length: > 0 } ? session.TranscriptPath : null,
                Source: session?.Source ?? SessionSource.ClaudeCode);

            var flight = new Flight
            {
                Cts = new CancellationTokenSource(),
                Moment = moment,
                SessionId = sessionId,
                StateAtRequest = StateOf(sessionId),
                FocusAtRequest = _focus,
                Draw = draw,
                Deadline = now + BubbleVoice.GenerationTimeout,
                AskedAt = now,
            };
            _flight = flight;
            _ = FlyAsync(flight, request);
        }

        // Awaits the generator and lands its answer. The await normally
        // returns to the UI thread through the context it captured, and a
        // generator that answers synchronously lands it before Say returns.
        // But a captured context is only there if one was current where the
        // await began, and when it is not the continuation runs on whatever
        // thread completed the call — a pool thread — where the view must
        // not be touched. So the thread is checked rather than assumed, and
        // an answer that arrives elsewhere is posted back (a flake found in
        // AnAnswerFinishedOffTheUiThreadIsShownOnIt, 3 runs in 15).
        private async Task FlyAsync(Flight flight, BubbleRequest request)
        {
            string? text;
            BubbleFailure? failure = null;
            try
            {
                text = await _generator!.GenerateAsync(request, flight.Cts.Token);

                // Read here, straight after the answer and before any post,
                // while nothing else can have asked the generator for anything.
                if (text is null) failure = (_generator as IBubbleGeneratorDiagnostics)?.LastFailure;
            }
            catch (Exception)
            {
                // The contract is null rather than a throw, but a generator
                // that throws anyway gets the same answer: the table. Only the
                // category is logged, since what went wrong may carry the
                // user's prompt.
                text = null;
                failure = BubbleFailure.Threw;
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => Land(flight, text, failure));
                return;
            }

            Land(flight, text, failure);
        }

        // The half of an answer that touches state and the view, always on
        // the UI thread.
        private void Land(Flight flight, string? text, BubbleFailure? failure)
        {
            // Cancelled, timed out, dropped or superseded: whatever it says
            // now belongs to a moment that has already been dealt with, and
            // that moment's log entry was written when it was.
            if (!ReferenceEquals(_flight, flight)) return;

            // A settings switch flipped between pumps is still honoured.
            var (verdict, reason) = Verdict(flight);
            Finish(flight);
            if (verdict == FlightVerdict.Drop)
            {
                RecordFlight(flight, BubbleOutcome.Dropped, reason);
                return;
            }

            if (verdict == FlightVerdict.Keep && text is not null)
            {
                ShowNow(text, _clock());
                RecordFlight(flight, BubbleOutcome.ShownAi, text: text);
                return;
            }

            // Either the voice was withdrawn (reason says so) or it answered
            // nothing, in which case the reason is the generator's own.
            Fallback(flight, reason ?? BubbleLogReason.Of(failure ?? BubbleFailure.Unknown), _clock());
        }

        // Whether the call in flight should still be said, and if not, why.
        // Bubbles switched off, focus moving off the call's session, or that
        // session's state changing all mean the moment has passed, and it is
        // dropped with nothing in its place (owner decision on CB-202). AI
        // bubbles switched off is different: the moment is still current,
        // only the voice was withdrawn, so the table says it instead.
        //
        // Every moment that can be in flight is about a session's current
        // work (BuddyMoments.MayUseAi), so both rules apply to all of them.
        // The exemptions a welcome and an evolution used to have went with
        // their generated lines.
        private (FlightVerdict Verdict, string? Reason) Verdict(Flight flight)
        {
            if (!_bubblesEnabled()) return (FlightVerdict.Drop, BubbleLogReason.BubblesOff);
            if (!_aiEnabled()) return (FlightVerdict.Table, BubbleLogReason.AiOff);
            if (_focus != flight.FocusAtRequest) return (FlightVerdict.Drop, BubbleLogReason.FocusMoved);
            return StateOf(flight.SessionId) == flight.StateAtRequest
                ? (FlightVerdict.Keep, null)
                : (FlightVerdict.Drop, BubbleLogReason.StateChanged);
        }

        // Applies Verdict to the call in flight, if any, without waiting for
        // it to answer.
        private void CheckFlight(DateTimeOffset now)
        {
            if (_flight is not { } flight) return;
            var (verdict, reason) = Verdict(flight);
            if (verdict == FlightVerdict.Keep) return;

            Cancel(flight);
            if (verdict == FlightVerdict.Table) Fallback(flight, reason!, now);
            else RecordFlight(flight, BubbleOutcome.Dropped, reason);
        }

        // The table says what the call would have: the same draw it would
        // have had without one.
        private void Fallback(Flight flight, string reason, DateTimeOffset now)
        {
            var line = TableLine(flight);
            ShowNow(line, now);
            RecordFlight(flight, BubbleOutcome.Fallback, reason, BubbleLogEntry.TableSource, line);
        }

        // One log entry, if there is a log and it is switched on. Never
        // throws: a log that fails, however it fails, costs the entry and
        // nothing else — the bubble has already been decided by the time
        // this runs, and nothing here can change it.
        private void Record(
            BuddyMoment moment, BubbleOutcome outcome, string? reason = null,
            string? source = null, DateTimeOffset? askedAt = null, string? text = null)
        {
            if (_log is null) return;
            try
            {
                if (!_logEnabled()) return;
                var now = _clock();
                long? latency = askedAt is { } at ? (long)Math.Round((now - at).TotalMilliseconds) : null;
                var ai = _generator is not null && _aiEnabled();
                _log.Log(new BubbleLogEntry(now, moment, outcome, reason, source, latency, text, ai));
            }
            catch
            {
                // See above.
            }
        }

        // An entry about a call that was made: its moment, and its latency
        // from the ask. Source is the voice that was asked unless the table
        // spoke in its place.
        private void RecordFlight(
            Flight flight, BubbleOutcome outcome, string? reason = null,
            string source = BubbleLogEntry.AiSource, string? text = null) =>
            Record(flight.Moment, outcome, reason, source, flight.AskedAt, text);

        // The hide clock starts here, when the bubble is actually shown, so a
        // generated line gets its full BubbleFor however long it took.
        private void ShowNow(string text, DateTimeOffset now)
        {
            _view.ShowBubble(text);
            _bubbleHideAt = now + BubbleFor;
        }

        private string TableLine(Flight flight) => _rules.Pick(flight.Moment, _genome!, flight.Draw);

        // Stops the call: the generator kills its process when the token
        // fires, and FlyAsync ignores whatever comes back.
        private void Cancel(Flight flight)
        {
            _flight = null;
            flight.Cts.Cancel();
            flight.Cts.Dispose();
        }

        private void Finish(Flight flight)
        {
            _flight = null;
            flight.Cts.Dispose();
        }

        // The session's state in the latest scan only — null once it has gone,
        // so a session's end is a state like any other: said about a session
        // that is gone, it stays current for as long as the session stays
        // gone.
        private string? StateOf(string? sessionId) =>
            _previous?.FirstOrDefault(s => s.SessionId == sessionId)?.State;

        // The session as last scanned, or as scanned just before that for one
        // that has just gone. Null for an evolution with nothing focused.
        private SessionSnapshot? Find(string? sessionId) =>
            sessionId is null
                ? null
                : _previous?.FirstOrDefault(s => s.SessionId == sessionId)
                  ?? _before?.FirstOrDefault(s => s.SessionId == sessionId);

        // The project folder's name — the last segment of the cwd, and never
        // more of the path (the owner-approved list on CB-202 allows the name
        // only). Either separator, since a WSL or peer session's cwd is a
        // POSIX path whichever OS this runs on. Null when there is none; the
        // label is deliberately not a fallback, since it can be a chat title.
        internal static string? ProjectName(string? cwd)
        {
            var leaf = cwd?.Split('/', '\\').LastOrDefault(p => p.Length > 0);
            return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
        }

        // Once a second: take down a bubble that has been up long enough, and
        // retry the held moment once the gap has opened.
        internal void Pump()
        {
            if (!_shown) return;
            var now = _clock();

            // A round that earned something but was not due to save, with no
            // round since to carry it: written here once its time comes.
            SaveIfDue(now);

            if (_bubbleHideAt is { } hide && now >= hide)
            {
                _bubbleHideAt = null;
                _view.HideBubble();
            }

            // The call in flight: dropped if a setting has overtaken it, and
            // otherwise given until its deadline, after which the table says
            // it. The one case where a failed generation delays a bubble, and
            // by at most GenerationTimeout. Against the injected clock, so no
            // real timer is involved and a test can step straight past it.
            CheckFlight(now);
            if (_flight is { } flight && now >= flight.Deadline)
            {
                Cancel(flight);
                Fallback(flight, BubbleLogReason.Of(BubbleFailure.TimedOut), now);
            }

            if (_pending is { } held && now >= _pendingAt)
            {
                _pending = null;
                Raise(held, _pendingSession, now);
            }
        }

        // One round of counting. TickGate so a slow disk cannot let two rounds
        // overlap and read the same bytes twice; the file work is off the UI
        // thread, the credit is applied back on it.
        internal async Task LedgerRoundAsync()
        {
            if (!_shown || _state is null) return;
            if (!_gate.TryEnter()) return;

            try
            {
                var now = _clock();
                var discover = _lastDiscover is null || now - _lastDiscover >= DiscoverEvery;
                var snapshot = _state;
                var paths = _livePaths;
                var epoch = _epoch;

                var scan = await Task.Run(() => _ledger.Scan(snapshot, paths, discover));
                if (discover) _lastDiscover = now;

                // The buddy may have been turned off, or reborn, while the
                // files were being read.
                if (_shown && _state is not null && epoch == _epoch) ApplyScan(scan, now);
            }
            catch (Exception ex)
            {
                // A locked or vanished transcript is the scanner's own to
                // survive; anything reaching here is unexpected, and the next
                // round starts from the same cursors, so nothing is lost.
                CrashLog.Record("BuddyController.LedgerRound", ex);
            }
            finally
            {
                _gate.Exit();
            }
        }

        private void ApplyScan(LedgerScanResult scan, DateTimeOffset now)
        {
            var current = _state!;

            // Applied to the state as it is now, not as it was when the round
            // started, so a rebirth that happened mid-read is not undone.
            //
            // Which means that read's tokens go to the *new* buddy, though
            // they were written while the old one was on screen. Left so on
            // purpose (QA on CB-195 asked). It is not a new kind of error:
            // with no race at all, whatever was written between the last
            // round and the click is credited to the newborn by the next
            // round in exactly the same way, because a rebirth does not scan
            // first. The race only widens that window from "up to one round"
            // to "up to one round plus one read". Nothing is lost or counted
            // twice either way — LifetimeTokens gets the tokens regardless,
            // and the cursors are the same ones — so the only thing at stake
            // is which of two buddies a few seconds of output is shown under.
            // Fixing it would mean scanning synchronously on the click or
            // carrying a rebirth generation through the scan, both more
            // moving parts than that is worth. BuddyControllerTests pins the
            // behaviour.
            var changed = scan.OutputTokens > 0 || scan.Cursors.Count > 0
                          || scan.Removed.Count > 0 || scan.CreditedMessageIds.Count > 0;
            if (!changed) return;

            var stageBefore = _rules.Stage(current.Tokens);
            var starsBefore = _rules.Stars(current.Tokens);

            var next = scan.ApplyLedger(current);
            if (scan.OutputTokens > 0) next = _rules.Credit(next, scan.OutputTokens);

            var evolved = _rules.Stage(next.Tokens) != stageBefore
                          || _rules.Stars(next.Tokens) != starsBefore;

            _state = next;
            _unsaved = true;

            // An evolution is written at once — it is the one credit a user
            // would notice going missing — and anything else when due.
            if (evolved) Save(now);
            else SaveIfDue(now);

            // UpdateState refreshes an already-visible view (name tag, tooltip,
            // an open card's token count) and never shows a hidden one, so it
            // is safe on every credit and an evolution is drawn before it is
            // announced.
            _view.UpdateState(_genome!, next);

            // Said in the context of whatever the buddy is looking at, which
            // is only the project a generated line may mention.
            if (evolved) Raise(BuddyMoment.Evolved, _focus, now);
        }

        private void Save(DateTimeOffset now)
        {
            _store.Save(_state!);
            _lastSave = now;
            _unsaved = false;
        }

        private void SaveIfDue(DateTimeOffset now)
        {
            if (_unsaved && now - _lastSave >= SaveEvery) Save(now);
        }

        // Writes whatever counting has earned and not yet saved. On hiding
        // and on quit (Dispose), the two moments after which nothing else
        // would.
        internal void Flush()
        {
            if (_unsaved) Save(_clock());
        }

        // The user accepted the rebirth offer. The view does not re-roll; this
        // does, and shows the new buddy.
        internal void OnRebirthRequested()
        {
            if (_state is null || _genome is null) return;

            var reborn = _rules.Rebirth(_state, _genome, _clock());

            // Progress refuses (returns the state unchanged) when the offer is
            // not yet earned; there is nothing to save or redraw then.
            if (reborn.Rebirths == _state.Rebirths) return;

            // _state already carries every credit applied so far, so this one
            // write also covers anything still waiting on SaveEvery.
            _state = reborn;
            Save(_clock());
            _genome = _rules.Roll(reborn.Uuid, reborn.Rebirths);
            _view.Show(_genome, reborn);
        }

        // The buddy on disk was replaced from outside the controller — Settings'
        // import from Claude Buddy (HatchAI, not in Claude Buddy's original). Forget
        // the buddy held in memory without saving it, so its next save cannot
        // write the old buddy straight back over the imported one, and load the
        // new one the way a launch would. A line in flight or held for later was
        // about the old buddy and is dropped; a ledger round still reading is
        // ignored when it lands (see _epoch). Whatever the old buddy earned and
        // had not saved yet is discarded on purpose: it is being replaced.
        internal void Reload()
        {
            _epoch++;
            if (_flight is { } flight)
            {
                Cancel(flight);
                RecordFlight(flight, BubbleOutcome.Dropped, BubbleLogReason.Superseded);
            }
            if (_pending is { } held)
            {
                _pending = null;
                Record(held, BubbleOutcome.Dropped, BubbleLogReason.Superseded);
            }

            _state = null;
            _genome = null;
            _unsaved = false;
            _bubbleHideAt = null;
            _view.HideBubble();

            if (!_shown) return;
            EnsureBuddy();
            _view.Show(_genome!, _state!);
        }

        // Quit: the last chance to write what the last rounds earned. A crash
        // skips this, which is the case SaveEvery's comment covers.
        public void Dispose()
        {
            if (_flight is { } flight)
            {
                Cancel(flight);
                RecordFlight(flight, BubbleOutcome.Dropped, BubbleLogReason.Disposed);
            }
            if (_pending is { } held)
            {
                _pending = null;
                Record(held, BubbleOutcome.Dropped, BubbleLogReason.Disposed);
            }
            Flush();
            _view.RebirthRequested -= OnRebirthRequested;
            _view.HideRequested -= Reapply;
            _ledgerTimer?.Stop();
            _pumpTimer?.Stop();
            if (ReferenceEquals(Instance, this)) Instance = null;

            // Last, so the entries above are in what it writes out.
            if (_ownsLog) (_log as IDisposable)?.Dispose();
        }
    }
}
