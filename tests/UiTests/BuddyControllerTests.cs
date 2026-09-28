using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace HatchAI.Tests;

// BuddyController driven end to end with fakes: hand-built snapshots in, the
// bubbles a FakeBuddyView was asked to show out. The creature engine is a
// deterministic stand-in (BuddyRules), so what is asserted is the wiring —
// which moment reached the view and when — and not what E1's tables say.
internal sealed class FakeBuddyView : IBuddyView
{
    public readonly List<string> Calls = new();
    public readonly List<string> Bubbles = new();
    // Whether each ShowBubble ran on the UI thread: the view is not thread
    // safe, and a test that only looks at what was shown cannot tell.
    public readonly List<bool> BubbleOnUiThread = new();
    public BuddyState? LastState;

    public event Action? RebirthRequested;
    public event Action? HideRequested;

    public void Show(BuddyGenome genome, BuddyState state) { Calls.Add("show"); LastState = state; }
    public void UpdateState(BuddyGenome genome, BuddyState state) { Calls.Add("update"); LastState = state; }
    public void Hide() => Calls.Add("hide");
    public void ShowBubble(string text)
    {
        Calls.Add("bubble");
        Bubbles.Add(text);
        BubbleOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
    }
    public void HideBubble() => Calls.Add("hidebubble");

    public void RaiseRebirth() => RebirthRequested?.Invoke();
    public bool HasRebirthSubscriber => RebirthRequested is not null;
    public bool HasHideSubscriber => HideRequested is not null;
}

internal sealed class FakeBuddyStore : IBuddyStore
{
    public BuddyState? Stored;
    public readonly List<BuddyState> Saved = new();

    public BuddyState? Load() => Stored;
    public void Save(BuddyState state) { Saved.Add(state); Stored = state; }
}

internal sealed class FakeLedgerSource : IBuddyLedgerSource
{
    public long Tokens;
    public bool AdvanceCursors;
    public Exception? Throw;
    public ManualResetEventSlim? Block;
    public ManualResetEventSlim Entered = new();
    public int Calls;
    public readonly List<bool> Discovers = new();
    public IReadOnlyList<string> LastPaths = Array.Empty<string>();

    public LedgerScanResult Scan(BuddyState state, IReadOnlyList<string> livePaths, bool discover)
    {
        Interlocked.Increment(ref Calls);
        lock (Discovers) Discovers.Add(discover);
        LastPaths = livePaths;
        Entered.Set();
        Block?.Wait(TimeSpan.FromSeconds(10));
        if (Throw is not null) throw Throw;

        var cursors = AdvanceCursors
            ? new Dictionary<string, LedgerCursor> { ["f"] = new(10, null, null, 0, 0) }
            : new Dictionary<string, LedgerCursor>();
        return new LedgerScanResult(cursors, Array.Empty<string>(), Tokens, Array.Empty<string>());
    }
}

[Collection("Settings")]
public class BuddyControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    internal static readonly BuddyGenome Genome = new(
        BuddySpecies.Duck, BuddyFamily.Birds, BuddyRarity.Common, false, BuddyEyes.Dot, BuddyHat.None,
        new BuddyStats(1, 2, 3, 4, 5), BuddyPersonality.Cheerful, BuddyPersonality.Zen, "Quackers");

    // The same shape as the real rules since CB-195's egg, written out
    // rather than borrowed: an egg to 2.5k, a stage every 10k after that up
    // to the third at 32.5k, one star from 60k, rebirth allowed from 42.5k.
    internal static readonly BuddyRules FakeRules = new(
        (_, _) => Genome,
        (s, t) => s with { Tokens = s.Tokens + t, LifetimeTokens = s.LifetimeTokens + t },
        t => t < 2_500 ? BuddyStage.Egg : (BuddyStage)Math.Min(4, 1 + (int)((t - 2_500) / 10_000)),
        t => t >= 60_000 ? 1 : 0,
        (s, _, _) => s.Tokens < 42_500 ? s : s with { Rebirths = s.Rebirths + 1, Tokens = 0 },
        (m, _, draw) => $"{m}:{draw}");

    private sealed class Rig
    {
        public readonly FakeBuddyView View = new();
        public readonly FakeBuddyStore Store = new();
        public readonly FakeLedgerSource Ledger = new();
        public bool BuddyOn = true;
        public bool BubblesOn = true;
        public DateTimeOffset Now = T0;
        public readonly BuddyController Controller;

        public Rig()
        {
            Controller = new BuddyController(
                View, Store, Ledger, () => BuddyOn, () => BubblesOn, () => Now, FakeRules);
        }

        public Rig Enabled()
        {
            Controller.Reapply();
            return this;
        }

        public void Scan(params SessionSnapshot[] sessions) => Controller.OnSnapshots(sessions);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static SessionSnapshot S(string id, string state, DateTimeOffset since,
        SessionSource source = SessionSource.ClaudeCode, string path = "") =>
        new(id, state, since, source, id, "/w/" + id, path, "");

    // --- enabling and hatching ---------------------------------------------

    [AvaloniaFact]
    public void FirstEnableHatchesSavesAndShows()
    {
        var rig = new Rig();
        rig.Controller.Reapply();

        var saved = Assert.Single(rig.Store.Saved);
        Assert.True(Guid.TryParse(saved.Uuid, out _));
        Assert.Equal(T0, saved.CountingSince);
        Assert.Equal(new[] { "show" }, rig.View.Calls);
        Assert.True(rig.Controller.Shown);
        Assert.Same(saved, rig.Controller.State);
    }

    [AvaloniaFact]
    public void AnExistingBuddyIsLoadedNotReplaced()
    {
        var rig = new Rig();
        rig.Store.Stored = BuddyState.Hatch("abc", T0.AddDays(-1)) with { Tokens = 5 };
        rig.Controller.Reapply();

        Assert.Empty(rig.Store.Saved);
        Assert.Equal("abc", rig.Controller.State!.Uuid);
        Assert.Equal(5, rig.View.LastState!.Tokens);
    }

    [AvaloniaFact]
    public void ReapplyIsIdempotentAndHidingClearsWhatWasOnScreen()
    {
        var rig = new Rig().Enabled();
        rig.Controller.Reapply();
        Assert.Equal(new[] { "show" }, rig.View.Calls);

        rig.BuddyOn = false;
        rig.Controller.Reapply();
        Assert.Equal(new[] { "show", "hidebubble", "hide" }, rig.View.Calls);
        Assert.False(rig.Controller.Shown);

        // Hidden again with nothing changed is a no-op.
        rig.Controller.Reapply();
        Assert.Equal(3, rig.View.Calls.Count);
    }

    [AvaloniaFact]
    public void DisabledFromTheStartTouchesNoStoreAndShowsNothing()
    {
        var rig = new Rig { BuddyOn = false };
        rig.Controller.Reapply();
        rig.Scan(S("a", "idle", T0));

        Assert.Empty(rig.View.Calls);
        Assert.Empty(rig.Store.Saved);
        Assert.Null(rig.Controller.State);
    }

    [AvaloniaFact]
    public void ReEnablingBaselinesAgainSoRunningSessionsAreNotAnnounced()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0));
        rig.BuddyOn = false;
        rig.Controller.Reapply();
        rig.BuddyOn = true;
        rig.Controller.Reapply();
        rig.View.Calls.Clear();

        // A scan that would be a Responded transition against the old
        // baseline is a first observation now.
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));

        Assert.Empty(rig.View.Bubbles);
    }

    // --- bubbles from snapshots --------------------------------------------

    [AvaloniaFact]
    public void AFirstScanIsABaselineAndASubsequentTurnEndingIsABubble()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0));
        Assert.Empty(rig.View.Bubbles);
        Assert.Equal("a", rig.Controller.Focus);

        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));

        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void WaitingRaisesNeedsAttentionAndTheDrawAdvances()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0));
        rig.Advance(2);
        rig.Scan(S("a", "waiting", rig.Now));
        rig.Advance(60);
        rig.Scan(S("a", "generating", rig.Now));

        Assert.Equal(new[] { "NeedsAttention:0", "UserResponded:1" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void ANewSessionIsAnnouncedAndSoIsItsEnd()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "idle", T0));
        rig.Advance(2);
        rig.Scan(S("a", "idle", T0), S("b", "idle", rig.Now));
        Assert.Equal(new[] { "SessionStarted:0" }, rig.View.Bubbles);

        // Past the gap and the cooldown; a was the focus, and is gone.
        rig.Advance(200);
        rig.Scan(S("b", "idle", T0.AddSeconds(2)));
        Assert.Equal(new[] { "SessionStarted:0", "SessionEnded:1" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void ThinkingFiresOnceWhenGeneratingCrossesEightSeconds()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0));
        rig.Advance(6);
        rig.Scan(S("a", "generating", T0));
        Assert.Empty(rig.View.Bubbles);

        rig.Advance(2);
        rig.Scan(S("a", "generating", T0));
        Assert.Equal(new[] { "Thinking:0" }, rig.View.Bubbles);

        rig.Advance(2);
        rig.Scan(S("a", "generating", T0));
        Assert.Single(rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void OnlyTheFocusedSessionsMomentsGetABubble()
    {
        var rig = new Rig().Enabled();
        // b changed 3 s ago and is the focus; a is older.
        rig.Scan(S("a", "generating", T0.AddSeconds(-100)), S("b", "idle", T0.AddSeconds(-3)));
        Assert.Equal("b", rig.Controller.Focus);

        // a finishes, but b is still sticky: a's moment is not the buddy's.
        rig.Scan(S("a", "idle", T0), S("b", "idle", T0.AddSeconds(-3)));

        Assert.Empty(rig.View.Bubbles);
        Assert.Equal("b", rig.Controller.Focus);
    }

    [AvaloniaFact]
    public void OfSeveralMomentsInOneScanTheMostImportantIsSaid()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0.AddSeconds(-100)), S("b", "idle", T0.AddSeconds(-3)));

        // b (the old focus) ends, a (the new focus) finishes its turn:
        // Responded outranks SessionEnded, so it is the one said. The other
        // ordering — the lesser first — is covered by the reverse below.
        rig.Scan(S("a", "idle", T0));
        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void ALesserMomentListedFirstDoesNotHideAGreaterOne()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "idle", T0.AddSeconds(-100)), S("b", "generating", T0.AddSeconds(-3)));

        // b is removed (SessionEnded, old focus) and a starts generating
        // (UserResponded): UserResponded outranks SessionEnded.
        rig.Scan(S("a", "generating", T0));
        Assert.Equal(new[] { "UserResponded:0" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void BubblesOffSaysNothing()
    {
        var rig = new Rig { BubblesOn = false }.Enabled();
        rig.Scan(S("a", "generating", T0));
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));

        Assert.Empty(rig.View.Bubbles);
    }

    // --- rate limiting, deferral, hiding -----------------------------------

    [AvaloniaFact]
    public void ABubbleIsTakenDownAfterItsTime()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0));
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));
        rig.View.Calls.Clear();

        rig.Advance(5);
        rig.Controller.Pump();
        Assert.Empty(rig.View.Calls);

        rig.Advance(1.5);
        rig.Controller.Pump();
        Assert.Equal(new[] { "hidebubble" }, rig.View.Calls);

        rig.Controller.Pump();
        Assert.Single(rig.View.Calls);
    }

    [AvaloniaFact]
    public void AMomentInsideTheGapIsHeldAndSaidWhenItOpens()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0));
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));               // Responded, shown at T0+2
        rig.Advance(2);
        rig.Scan(S("a", "waiting", rig.Now));            // NeedsAttention, inside the gap
        Assert.Single(rig.View.Bubbles);

        rig.Advance(10);
        rig.Controller.Pump();
        Assert.Single(rig.View.Bubbles);                 // still inside 20 s

        rig.Advance(8);
        rig.Controller.Pump();
        Assert.Equal(new[] { "Responded:0", "NeedsAttention:1" }, rig.View.Bubbles);

        // Delivered once, not on every later pump.
        rig.Advance(30);
        rig.Controller.Pump();
        Assert.Equal(2, rig.View.Bubbles.Count);
    }

    [AvaloniaFact]
    public void OnlyTheMostImportantHeldMomentSurvives()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0));
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));               // Responded shown
        rig.Advance(2);
        rig.Scan(S("a", "generating", rig.Now));         // UserResponded held
        rig.Advance(2);
        rig.Scan(S("a", "waiting", rig.Now));            // NeedsAttention replaces it
        rig.Advance(2);
        rig.Scan(S("a", "generating", rig.Now));         // UserResponded again: cannot replace

        rig.Advance(30);
        rig.Controller.Pump();

        Assert.Equal(new[] { "Responded:0", "NeedsAttention:1" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void ACooldownDropIsNotHeld()
    {
        var rig = new Rig().Enabled();
        rig.Scan(S("a", "generating", T0));
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));               // Responded
        rig.Advance(21);
        rig.Scan(S("a", "generating", rig.Now));         // UserResponded (past the gap)
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));               // Responded again: gap says wait
        rig.Advance(30);
        rig.Controller.Pump();

        // 23 s after the first Responded: the gap has opened and its own 45 s
        // cooldown has not, so it is dropped rather than said late.
        Assert.Equal(new[] { "Responded:0", "UserResponded:1" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void PumpDoesNothingWhileHidden()
    {
        var rig = new Rig();
        rig.Controller.Pump();

        Assert.Empty(rig.View.Calls);
    }

    // --- the ledger --------------------------------------------------------

    [AvaloniaFact]
    public async Task ARoundCreditsSavesAndPassesTheLiveTranscripts()
    {
        var rig = new Rig().Enabled();
        rig.Store.Saved.Clear();
        // Still an egg after it (the egg hatches at 2,500).
        rig.Ledger.Tokens = 2_000;
        rig.Scan(
            S("a", "idle", T0, SessionSource.ClaudeCode, "/t/a.jsonl"),
            S("b", "idle", T0, SessionSource.Codex, "/t/b.jsonl"),
            S("c", "idle", T0, SessionSource.Grok, "/t/c.jsonl"),
            S("d", "idle", T0, SessionSource.ClaudeCode, ""),
            S("e", "idle", T0, SessionSource.OpenClaw, "/t/e.jsonl"));

        await rig.Controller.LedgerRoundAsync();

        Assert.Equal(new[] { "/t/a.jsonl", "/t/b.jsonl" }, rig.Ledger.LastPaths);
        Assert.Equal(2_000, rig.Controller.State!.Tokens);
        Assert.Equal(2_000, rig.Controller.State.LifetimeTokens);
        // Same stage: no bubble, but the view is told the new token count so
        // an open card does not go stale.
        Assert.Empty(rig.View.Bubbles);
        Assert.Equal(new[] { "show", "update" }, rig.View.Calls);

        // Not yet written — the hatch save was a moment ago — but written by
        // the pump once SaveEvery has passed, with the credit in it.
        Assert.Empty(rig.Store.Saved);
        rig.Advance(29);
        rig.Controller.Pump();
        Assert.Empty(rig.Store.Saved);
        rig.Advance(1);
        rig.Controller.Pump();
        Assert.Equal(2_000, Assert.Single(rig.Store.Saved).Tokens);

        // And not again while nothing has changed.
        rig.Advance(60);
        rig.Controller.Pump();
        Assert.Single(rig.Store.Saved);
    }

    [AvaloniaFact]
    public async Task AnEvolutionIsSavedAtOnceWhateverTheThrottleSays()
    {
        var rig = new Rig().Enabled();
        rig.Store.Saved.Clear();
        // Exactly the hatch.
        rig.Ledger.Tokens = 2_500;

        await rig.Controller.LedgerRoundAsync();

        Assert.Equal(2_500, Assert.Single(rig.Store.Saved).Tokens);
    }

    [AvaloniaFact]
    public async Task HidingAndQuittingWriteWhatIsWaiting()
    {
        var rig = new Rig().Enabled();
        rig.Store.Saved.Clear();
        rig.Ledger.Tokens = 100;
        await rig.Controller.LedgerRoundAsync();
        Assert.Empty(rig.Store.Saved);

        rig.BuddyOn = false;
        rig.Controller.Reapply();
        Assert.Equal(100, Assert.Single(rig.Store.Saved).Tokens);

        // Hiding again, or quitting, with nothing waiting writes nothing.
        rig.Controller.Reapply();
        rig.Controller.Dispose();
        Assert.Single(rig.Store.Saved);

        var quit = new Rig().Enabled();
        quit.Store.Saved.Clear();
        quit.Ledger.Tokens = 7;
        await quit.Controller.LedgerRoundAsync();
        quit.Controller.Dispose();
        Assert.Equal(7, Assert.Single(quit.Store.Saved).Tokens);
    }

    [AvaloniaFact]
    public async Task ARebirthWritesAnyCreditStillWaitingWithIt()
    {
        var rig = new Rig();
        rig.Store.Stored = BuddyState.Hatch("u", T0) with { Tokens = 42_700, LifetimeTokens = 42_700 };
        rig.Controller.Reapply();
        rig.Ledger.Tokens = 200;
        await rig.Controller.LedgerRoundAsync();
        Assert.Empty(rig.Store.Saved);

        rig.View.RaiseRebirth();

        var saved = Assert.Single(rig.Store.Saved);
        Assert.Equal(1, saved.Rebirths);
        Assert.Equal(42_900, saved.LifetimeTokens);

        // Nothing left waiting for quit to write.
        rig.Controller.Dispose();
        Assert.Single(rig.Store.Saved);
    }

    // A rebirth that lands while a round is reading: that round's tokens go
    // to the new buddy, and nothing is lost or doubled. Pinned rather than
    // fixed — BuddyController.ApplyScan's comment has the reasoning.
    [AvaloniaFact]
    public async Task ARebirthDuringARoundCreditsThatRoundToTheNewBuddy()
    {
        var rig = new Rig();
        rig.Store.Stored = BuddyState.Hatch("u", T0) with { Tokens = 43_000, LifetimeTokens = 43_000 };
        rig.Controller.Reapply();
        rig.Ledger.Tokens = 400;
        rig.Ledger.AdvanceCursors = true;
        rig.Ledger.Block = new ManualResetEventSlim();

        var round = rig.Controller.LedgerRoundAsync();
        Assert.True(rig.Ledger.Entered.Wait(TimeSpan.FromSeconds(10)));
        rig.View.RaiseRebirth();
        rig.Ledger.Block.Set();
        await round;

        var state = rig.Controller.State!;
        Assert.Equal(1, state.Rebirths);
        Assert.Equal(400, state.Tokens);
        Assert.Equal(43_400, state.LifetimeTokens);
        Assert.Equal(10, state.Cursors["f"].Offset);
    }

    [AvaloniaFact]
    public async Task CrossingAnEvolutionRedrawsAndSaysSo()
    {
        // The egg hatching is the first evolution a buddy has, and it is
        // announced the same way as every later one (CB-195: no new moment).
        var rig = new Rig().Enabled();
        rig.Ledger.Tokens = 2_600;

        await rig.Controller.LedgerRoundAsync();

        Assert.Equal(new[] { "show", "update", "bubble" }, rig.View.Calls);
        Assert.Equal(new[] { "Evolved:0" }, rig.View.Bubbles);
        Assert.Equal(2_600, rig.View.LastState!.Tokens);
    }

    // Past the hatch, a later evolution still crosses the same way.
    [AvaloniaFact]
    public async Task CrossingALaterEvolutionSaysSoToo()
    {
        var rig = new Rig();
        rig.Store.Stored = BuddyState.Hatch("u", T0) with { Tokens = 12_000 };
        rig.Controller.Reapply();
        rig.Ledger.Tokens = 600;

        await rig.Controller.LedgerRoundAsync();

        Assert.Equal(new[] { "Evolved:0" }, rig.View.Bubbles);
    }

    // A rebirth takes the count from Third back to zero, which is the egg:
    // a change of stage, but not an evolution, and not announced as one.
    // Driven with the real progress rules, since the question is whether
    // the controller's before-and-after comparison ever spans the reset.
    [AvaloniaFact]
    public async Task ARebirthBackToAnEggIsNotAnnouncedAsAnEvolution()
    {
        var real = BuddyRules.Default with { Roll = (_, _) => Genome, Pick = (m, _, draw) => $"{m}:{draw}" };
        var view = new FakeBuddyView();
        var store = new FakeBuddyStore
        {
            Stored = BuddyState.Hatch("u", T0) with { Tokens = 50_000, LifetimeTokens = 50_000 },
        };
        var ledger = new FakeLedgerSource();
        using var controller = new BuddyController(view, store, ledger, () => true, () => true, () => T0, real);
        controller.Reapply();

        view.RaiseRebirth();
        Assert.Equal(1, controller.State!.Rebirths);
        Assert.Equal(BuddyStage.Egg, BuddyProgress.StageFor(controller.State.Tokens));

        // The first round after it: tokens that keep the new buddy an egg.
        ledger.Tokens = 1_000;
        await controller.LedgerRoundAsync();
        Assert.Empty(view.Bubbles);

        // And the new buddy's own hatch is still announced.
        ledger.Tokens = 1_500;
        await controller.LedgerRoundAsync();
        Assert.Equal(new[] { "Evolved:0" }, view.Bubbles);
    }

    [AvaloniaFact]
    public async Task ANewStarCountsAsAnEvolutionToo()
    {
        var rig = new Rig();
        // Past the third stage already, so only the star changes.
        rig.Store.Stored = BuddyState.Hatch("u", T0) with { Tokens = 59_000 };
        rig.Controller.Reapply();
        rig.Ledger.Tokens = 2_000;

        await rig.Controller.LedgerRoundAsync();

        Assert.Equal(new[] { "Evolved:0" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public async Task ARoundThatFoundNothingSavesNothing()
    {
        var rig = new Rig().Enabled();
        rig.Store.Saved.Clear();

        await rig.Controller.LedgerRoundAsync();

        Assert.Empty(rig.Store.Saved);
        Assert.Equal(1, rig.Ledger.Calls);
    }

    [AvaloniaFact]
    public async Task AdvancedCursorsAreSavedEvenWithNoTokens()
    {
        var rig = new Rig().Enabled();
        rig.Store.Saved.Clear();
        rig.Ledger.AdvanceCursors = true;

        await rig.Controller.LedgerRoundAsync();
        rig.Advance(30);
        rig.Controller.Pump();

        var saved = Assert.Single(rig.Store.Saved);
        Assert.Equal(10, saved.Cursors["f"].Offset);
        Assert.Equal(0, saved.Tokens);
    }

    [AvaloniaFact]
    public async Task DiscoveryRunsOnTheFirstRoundAndThenOncePerMinute()
    {
        var rig = new Rig().Enabled();

        await rig.Controller.LedgerRoundAsync();
        rig.Advance(10);
        await rig.Controller.LedgerRoundAsync();
        rig.Advance(49);
        await rig.Controller.LedgerRoundAsync();
        rig.Advance(2);
        await rig.Controller.LedgerRoundAsync();

        Assert.Equal(new[] { true, false, false, true }, rig.Ledger.Discovers);
    }

    [AvaloniaFact]
    public async Task ARoundThatThrowsIsSurvivedAndTheNextOneRuns()
    {
        var rig = new Rig().Enabled();
        rig.Ledger.Throw = new IOException("locked");
        using (CrashLog.ScopeForTests(Path.Combine(Path.GetTempPath(), "buddy-crash-" + Guid.NewGuid())))
        {
            await rig.Controller.LedgerRoundAsync();
        }

        rig.Ledger.Throw = null;
        rig.Ledger.Tokens = 100;
        await rig.Controller.LedgerRoundAsync();

        Assert.Equal(2, rig.Ledger.Calls);
        Assert.Equal(100, rig.Controller.State!.Tokens);
    }

    [AvaloniaFact]
    public async Task RoundsNeverOverlap()
    {
        var rig = new Rig().Enabled();
        rig.Ledger.Block = new ManualResetEventSlim();

        var first = rig.Controller.LedgerRoundAsync();
        Assert.True(rig.Ledger.Entered.Wait(TimeSpan.FromSeconds(10)));

        await rig.Controller.LedgerRoundAsync();      // gate held: returns at once
        Assert.Equal(1, rig.Ledger.Calls);

        rig.Ledger.Block.Set();
        await first;

        await rig.Controller.LedgerRoundAsync();      // gate free again
        Assert.Equal(2, rig.Ledger.Calls);
    }

    [AvaloniaFact]
    public async Task ARoundFinishingAfterTheBuddyWasHiddenIsDiscarded()
    {
        var rig = new Rig().Enabled();
        rig.Ledger.Tokens = 5_000;
        rig.Ledger.Block = new ManualResetEventSlim();
        rig.Store.Saved.Clear();

        var round = rig.Controller.LedgerRoundAsync();
        Assert.True(rig.Ledger.Entered.Wait(TimeSpan.FromSeconds(10)));
        rig.BuddyOn = false;
        rig.Controller.Reapply();
        rig.Ledger.Block.Set();
        await round;

        Assert.Empty(rig.Store.Saved);
        Assert.Equal(0, rig.Controller.State!.Tokens);
    }

    [AvaloniaFact]
    public async Task NothingRunsWhileHidden()
    {
        var rig = new Rig();

        await rig.Controller.LedgerRoundAsync();

        Assert.Equal(0, rig.Ledger.Calls);
    }

    // --- rebirth and lifetime ----------------------------------------------

    [AvaloniaFact]
    public void RebirthBeforeAnyBuddyExistsIsIgnored()
    {
        var rig = new Rig();
        rig.View.RaiseRebirth();

        Assert.Empty(rig.View.Calls);
    }

    [AvaloniaFact]
    public void RebirthThatProgressRefusesChangesNothing()
    {
        var rig = new Rig().Enabled();
        rig.Store.Saved.Clear();
        rig.View.Calls.Clear();

        rig.View.RaiseRebirth();

        Assert.Empty(rig.Store.Saved);
        Assert.Empty(rig.View.Calls);
    }

    [AvaloniaFact]
    public void AcceptedRebirthIsSavedAndTheNewBuddyShown()
    {
        var rig = new Rig();
        rig.Store.Stored = BuddyState.Hatch("u", T0) with { Tokens = 43_000, LifetimeTokens = 43_000 };
        rig.Controller.Reapply();
        rig.View.Calls.Clear();

        rig.View.RaiseRebirth();

        var saved = Assert.Single(rig.Store.Saved);
        Assert.Equal(1, saved.Rebirths);
        Assert.Equal(0, saved.Tokens);
        Assert.Equal(43_000, saved.LifetimeTokens);
        // Rebirth draws the new buddy; it says nothing about evolving.
        Assert.Empty(rig.View.Bubbles);
        Assert.Equal(new[] { "show" }, rig.View.Calls);
        Assert.Same(saved, rig.View.LastState);
    }

    // --- lifetime ----------------------------------------------------------

    [AvaloniaFact]
    public void DisposeStopsListeningAndReleasesTheInstance()
    {
        var rig = new Rig();
        BuddyController.Instance = rig.Controller;
        Assert.True(rig.View.HasRebirthSubscriber);
        Assert.True(rig.View.HasHideSubscriber);

        rig.Controller.Dispose();

        Assert.False(rig.View.HasRebirthSubscriber);
        Assert.False(rig.View.HasHideSubscriber);
        Assert.Null(BuddyController.Instance);

        // Disposing one that is not the running instance leaves the instance be.
        var other = new Rig();
        BuddyController.Instance = other.Controller;
        rig.Controller.Dispose();
        Assert.Same(other.Controller, BuddyController.Instance);
        BuddyController.Instance = null;
    }

    // --- the buddy's own "Hide buddy" item (QA on CB-195) --------------------

    private static BuddyWindow RealWindow()
    {
        HatchAISettings.ClearOrbPosition(BuddyWindow.PositionKey);
        return new BuddyWindow(() => T0, animate: false)
        {
            WorkAreaAt = _ => new Avalonia.PixelRect(0, 0, 1920, 1040)
        };
    }

    private static void ClickHide(BuddyWindow window) =>
        window.HideMenuItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));

    // The real window, the real factory and the real tray switch, in the
    // order a person would use them. Each piece was already tested; what was
    // not was the wire between the window's menu and the controller, and it
    // was missing: the controller never heard the buddy had been put away,
    // so Reapply saw "wanted on, already shown" and the tray could not bring
    // it back.
    [AvaloniaFact]
    public void HidingFromTheBuddysOwnMenuLetsTheTrayBringItBack()
    {
        var buddyWas = HatchAISettings.BuddyEnabled;
        HatchAISettings.BuddyEnabled = true;
        var window = RealWindow();
        var controller = BuddyController.CreateForApp(window);
        try
        {
            controller.Reapply();
            Assert.True(window.IsVisible);

            ClickHide(window);
            Assert.False(window.IsVisible);
            Assert.False(HatchAISettings.BuddyEnabled);
            Assert.False(controller.Shown);

            Tray.ToggleBuddyVisible();
            Assert.True(HatchAISettings.BuddyEnabled);
            Assert.True(controller.Shown);
            Assert.True(window.IsVisible);

            // And once more round, through the settings window's path, which
            // is the same preference and the same Reapply.
            ClickHide(window);
            HatchAISettings.BuddyEnabled = true;
            BuddyController.Instance?.Reapply();
            Assert.True(window.IsVisible);
        }
        finally
        {
            controller.Dispose();
            window.Close();
            HatchAISettings.BuddyEnabled = buddyWas;
        }
    }

    // Hidden from its own menu, the buddy is off in every sense: no ledger
    // round reaches the files, nothing is saved, no bubble is raised.
    [AvaloniaFact]
    public async Task HidingFromTheBuddysOwnMenuStopsTheCounting()
    {
        var buddyWas = HatchAISettings.BuddyEnabled;
        HatchAISettings.BuddyEnabled = true;
        var window = RealWindow();
        var store = new FakeBuddyStore();
        var ledger = new FakeLedgerSource { Tokens = 500 };
        var now = T0;
        var controller = new BuddyController(window, store, ledger,
            () => HatchAISettings.BuddyEnabled, () => true, () => now, FakeRules);
        try
        {
            controller.Reapply();
            store.Saved.Clear();

            ClickHide(window);
            await controller.LedgerRoundAsync();
            controller.OnSnapshots(new[] { S("a", "generating", T0) });
            now = now.AddSeconds(2);
            controller.OnSnapshots(new[] { S("a", "idle", now) });

            Assert.Equal(0, ledger.Calls);
            Assert.Empty(store.Saved);
            Assert.Null(window.Bubble);
        }
        finally
        {
            controller.Dispose();
            window.Close();
            HatchAISettings.BuddyEnabled = buddyWas;
        }
    }

    // --- how often the store is written (QA on CB-195) ----------------------

    // A live session moves its transcript's cursor every round, so every
    // round used to rewrite the whole settings.json on the UI thread — every
    // ten seconds for as long as anything was being typed. At most once per
    // SaveEvery now, with nothing lost: the state in memory is current, and
    // what is not yet on disk is written by the next due round, the pump,
    // hiding, or quitting.
    [AvaloniaFact]
    public async Task CursorOnlyRoundsAreSavedAtMostEveryThirtySeconds()
    {
        var rig = new Rig().Enabled();          // the hatch save, at T0
        rig.Store.Saved.Clear();
        rig.Ledger.AdvanceCursors = true;

        for (var i = 0; i < 6; i++)
        {
            await rig.Controller.LedgerRoundAsync();
            rig.Advance(10);
        }

        // Rounds at 0, 10, 20, 30, 40, 50 s: the one at 30 s is the first due.
        var saved = Assert.Single(rig.Store.Saved);
        Assert.Equal(10, saved.Cursors["f"].Offset);
    }

    // The whole production stack minus the window: the real store (a real
    // settings.json in the suite's temp directory), the real engine and the
    // real scanner class, hatching a buddy and drawing a bubble for it.
    [AvaloniaFact]
    public void TheAppFactoryWiresTheRealStoreEngineAndSettings()
    {
        var buddyWas = HatchAISettings.BuddyEnabled;
        var bubblesWas = HatchAISettings.BuddyBubblesEnabled;
        var view = new FakeBuddyView();
        var controller = BuddyController.CreateForApp(view);
        try
        {
            Assert.Same(controller, BuddyController.Instance);

            HatchAISettings.BuddyEnabled = true;
            HatchAISettings.BuddyBubblesEnabled = true;
            controller.Reapply();
            Assert.Equal(new[] { "show" }, view.Calls);
            Assert.NotNull(new BuddyStore().Load());

            controller.OnSnapshots(new[] { S("a", "generating", T0) });
            controller.OnSnapshots(new[] { S("a", "idle", T0.AddSeconds(2)) });
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(view.Bubbles)));

            // Turning the preference off through the same settings the
            // factory closed over.
            HatchAISettings.BuddyEnabled = false;
            controller.Reapply();
            Assert.Contains("hide", view.Calls);
        }
        finally
        {
            controller.Dispose();
            HatchAISettings.BuddyEnabled = buddyWas;
            HatchAISettings.BuddyBubblesEnabled = bubblesWas;
        }
    }
}
