using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace HatchAI.Tests;

// A log the test reads back, or one that throws on every entry.
internal sealed class FakeBubbleLog : IBubbleLog
{
    public readonly List<BubbleLogEntry> Entries = new();
    public Exception? Throw;

    public void Log(BubbleLogEntry entry)
    {
        if (Throw is not null) throw Throw;
        Entries.Add(entry);
    }
}

// A generator that can say why it answered null, as ClaudeCliBubbleGenerator
// does through IBubbleGeneratorDiagnostics.
internal sealed class DiagnosingBubbleGenerator : IBubbleGenerator, IBubbleGeneratorDiagnostics
{
    public readonly FakeBubbleGenerator Inner = new();
    public BubbleFailure? LastFailure { get; set; }

    public Task<string?> GenerateAsync(BubbleRequest request, CancellationToken ct) =>
        Inner.GenerateAsync(request, ct);
}

// The bubble log's side of BuddyController (CB-202, after live use): exactly
// one entry per decision, with the right outcome, reason, source, latency and
// text, for every path a bubble can take — and the idle-bubbles gate, which
// is decided in the same place. Same deterministic rules as the other
// controller tests, so a table line reads "<Moment>:<draw>".
[Collection("Settings")]
public class BuddyControllerLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public readonly FakeBuddyView View = new();
        public readonly FakeLedgerSource Ledger = new();
        public readonly FakeBubbleLog Log = new();
        public readonly DiagnosingBubbleGenerator Voice = new();
        public bool BuddyOn = true;
        public bool BubblesOn = true;
        public bool AiOn;
        public bool IdleOn;
        public bool LogOn = true;
        public DateTimeOffset Now = T0;
        public readonly BuddyController Controller;

        public Rig(bool ai = false, bool withGenerator = true, IBubbleGenerator? generator = null)
        {
            AiOn = ai;
            Controller = new BuddyController(
                View, new FakeBuddyStore(), Ledger, () => BuddyOn, () => BubblesOn, () => Now,
                BuddyControllerTests.FakeRules,
                generator ?? (withGenerator ? Voice : null),
                () => AiOn, () => IdleOn, Log, () => LogOn);
            Controller.Reapply();
        }

        public FakeBubbleGenerator Generator => Voice.Inner;
        public void Scan(params SessionSnapshot[] sessions) => Controller.OnSnapshots(sessions);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

        public void RespondedOnA()
        {
            Scan(S("a", "generating", Now));
            Advance(2);
            Scan(S("a", "idle", Now));
        }

        // a idle past the long-idle threshold: a LongIdle on a.
        public void LongIdleOnA()
        {
            var since = Now;
            Scan(S("a", "idle", since));
            Advance(BuddyMoments.LongIdleAfter.TotalSeconds);
            Scan(S("a", "idle", since));
        }
    }

    private static SessionSnapshot S(string id, string state, DateTimeOffset since, string path = "") =>
        new(id, state, since, SessionSource.ClaudeCode, id, "/w/" + id, path, "");

    private static BubbleLogEntry Only(Rig rig) => Assert.Single(rig.Log.Entries);

    private static void Is(BubbleLogEntry e, BuddyMoment moment, BubbleOutcome outcome, string? reason,
        string? source, long? latency, string? text, bool ai)
    {
        Assert.Equal(moment, e.Moment);
        Assert.Equal(outcome, e.Outcome);
        Assert.Equal(reason, e.Reason);
        Assert.Equal(source, e.Source);
        Assert.Equal(latency, e.LatencyMs);
        Assert.Equal(text, e.Text);
        Assert.Equal(ai, e.AiEnabled);
    }

    // --- shown --------------------------------------------------------------

    [AvaloniaFact]
    public void ATableLineIsLoggedAsShownWithItsTextAndTime()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        var e = Only(rig);
        Is(e, BuddyMoment.Responded, BubbleOutcome.ShownTable, null, "table", null, "Responded:0", false);
        Assert.Equal(T0.AddSeconds(2), e.At);
    }

    [AvaloniaFact]
    public void AGeneratedLineIsLoggedAsShownAiWithItsLatency()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();
        Assert.Empty(rig.Log.Entries);          // nothing decided yet while it is written

        rig.Advance(2.5);
        rig.Generator.Last.Tcs.SetResult("Nice work.");
        Dispatcher.UIThread.RunJobs();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.ShownAi, null, "ai", 2500, "Nice work.", true);
    }

    // AI on but no voice wired: the table, and the log does not claim AI was
    // available.
    [AvaloniaFact]
    public void WithNoGeneratorAiIsLoggedAsOff()
    {
        var rig = new Rig(ai: true, withGenerator: false);
        rig.RespondedOnA();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.ShownTable, null, "table", null, "Responded:0", false);
    }

    // --- fallback, with why --------------------------------------------------

    [AvaloniaTheory]
    [InlineData(nameof(BubbleFailure.NoCli), "no-cli")]
    [InlineData(nameof(BubbleFailure.StartFailed), "start-failed")]
    [InlineData(nameof(BubbleFailure.NonZeroExit), "non-zero-exit")]
    [InlineData(nameof(BubbleFailure.ProcessFailed), "process-failed")]
    [InlineData(nameof(BubbleFailure.BadOutput), "bad-output")]
    [InlineData(nameof(BubbleFailure.RejectedByValidator), "rejected-by-validator")]
    public void ANullAnswerIsLoggedWithTheGeneratorsReason(string failure, string reason)
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();

        rig.Advance(1.2);
        rig.Voice.LastFailure = Enum.Parse<BubbleFailure>(failure);
        rig.Generator.Last.Tcs.SetResult(null);
        Dispatcher.UIThread.RunJobs();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Fallback, reason, "table", 1200, "Responded:0", true);
    }

    // A generator with no diagnostics can only say "null".
    [AvaloniaFact]
    public void ANullAnswerFromAGeneratorWithNoDiagnosticsIsUnknown()
    {
        var plain = new FakeBubbleGenerator();
        var rig = new Rig(ai: true, generator: plain);
        rig.RespondedOnA();

        plain.Last.Tcs.SetResult(null);
        Dispatcher.UIThread.RunJobs();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Fallback, "unknown", "table", 0, "Responded:0", true);
    }

    [AvaloniaFact]
    public void AFaultedCallIsLoggedAsThrew()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();

        rig.Generator.Last.Tcs.SetException(new InvalidOperationException("boom"));
        Dispatcher.UIThread.RunJobs();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Fallback, "threw", "table", 0, "Responded:0", true);
    }

    [AvaloniaFact]
    public void AGeneratorThatThrowsOutrightIsLoggedAsThrew()
    {
        var rig = new Rig(ai: true);
        rig.Generator.ThrowAtOnce = new InvalidOperationException("boom");
        rig.RespondedOnA();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Fallback, "threw", "table", 0, "Responded:0", true);
    }

    [AvaloniaFact]
    public void ATimeoutIsLoggedAsTimedOutAtSixSeconds()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        rig.Advance(6);
        rig.Controller.Pump();

        // A late answer adds nothing.
        call.Tcs.SetResult("Too late.");
        Dispatcher.UIThread.RunJobs();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Fallback, "timed-out", "table", 6000, "Responded:0", true);
    }

    [AvaloniaFact]
    public void AiSwitchedOffMidCallIsLoggedAsAiOffAtThePump()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();

        rig.AiOn = false;
        rig.Advance(1);
        rig.Controller.Pump();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Fallback, "ai-off", "table", 1000, "Responded:0", false);
    }

    [AvaloniaFact]
    public void AiSwitchedOffBeforeTheAnswerLandsIsLoggedAsAiOff()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();

        rig.AiOn = false;
        rig.Generator.Last.Tcs.SetResult("Generated anyway.");
        Dispatcher.UIThread.RunJobs();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Fallback, "ai-off", "table", 0, "Responded:0", false);
    }

    // --- dropped, with why ---------------------------------------------------

    [AvaloniaFact]
    public void FocusMovingIsLoggedAsDroppedFocusMoved()
    {
        var rig = new Rig(ai: true);
        rig.Scan(S("a", "generating", T0), S("b", "idle", T0.AddSeconds(-100)));
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now), S("b", "idle", T0.AddSeconds(-100)));
        rig.Advance(2);
        rig.Scan(S("a", "idle", T0.AddSeconds(2)), S("b", "waiting", rig.Now));

        // The drop, then b's own NeedsAttention held behind the gap.
        Assert.Equal(2, rig.Log.Entries.Count);
        Is(rig.Log.Entries[0], BuddyMoment.Responded, BubbleOutcome.Dropped, "focus-moved", "ai", 2000, null, true);
        Is(rig.Log.Entries[1], BuddyMoment.NeedsAttention, BubbleOutcome.Deferred, null, null, null, null, true);
    }

    [AvaloniaFact]
    public void TheSessionMovingOnIsLoggedAsDroppedStateChanged()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();
        rig.Advance(1);
        rig.Scan(S("a", "generating", rig.Now));

        // The drop, then the UserResponded it raised, held behind the gap.
        Is(rig.Log.Entries[0], BuddyMoment.Responded, BubbleOutcome.Dropped, "state-changed", "ai", 1000, null, true);
        Is(rig.Log.Entries[1], BuddyMoment.UserResponded, BubbleOutcome.Deferred, null, null, null, null, true);
        Assert.Equal(2, rig.Log.Entries.Count);
    }

    [AvaloniaFact]
    public void HidingTheBuddyIsLoggedAsDroppedBuddyHidden()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        rig.BuddyOn = false;
        rig.Controller.Reapply();
        call.Tcs.SetResult("Nobody hears this.");
        Dispatcher.UIThread.RunJobs();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Dropped, "buddy-hidden", "ai", 0, null, true);
    }

    [AvaloniaFact]
    public void BubblesOffAtThePumpIsLoggedAsDroppedBubblesOff()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();

        rig.BubblesOn = false;
        rig.Advance(1);
        rig.Controller.Pump();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Dropped, "bubbles-off", "ai", 1000, null, true);
    }

    [AvaloniaFact]
    public void BubblesOffWhenTheAnswerLandsIsLoggedAsDroppedBubblesOff()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();

        rig.BubblesOn = false;
        rig.Generator.Last.Tcs.SetResult("Unwanted.");
        Dispatcher.UIThread.RunJobs();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Dropped, "bubbles-off", "ai", 0, null, true);
    }

    [AvaloniaFact]
    public void ASupersededCallIsLoggedAsDroppedSuperseded()
    {
        var rig = new Rig(ai: true);
        rig.Scan(S("a", "idle", T0));
        rig.Advance(2);
        rig.Scan(S("a", "generating", rig.Now));
        var since = rig.Now;
        rig.Advance(1);
        rig.Scan(S("a", "generating", since));
        rig.Advance(20);
        rig.Scan(S("a", "generating", since));      // Thinking replaces the UserResponded call

        Is(Only(rig), BuddyMoment.UserResponded, BubbleOutcome.Dropped, "superseded", "ai", 21000, null, true);
        rig.Generator.Last.Tcs.SetResult("Still at it.");
        Dispatcher.UIThread.RunJobs();
        Is(rig.Log.Entries[1], BuddyMoment.Thinking, BubbleOutcome.ShownAi, null, "ai", 0, "Still at it.", true);
    }

    [AvaloniaFact]
    public void DisposingIsLoggedAsDroppedDisposed()
    {
        var rig = new Rig(ai: true);
        rig.RespondedOnA();
        rig.Advance(0.5);

        rig.Controller.Dispose();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Dropped, "disposed", "ai", 500, null, true);
    }

    // --- held moments ----------------------------------------------------------

    // Deferred once, when first held, and then its own outcome when the gap
    // opens: two entries for two decisions, no more.
    [AvaloniaFact]
    public void ADeferredMomentIsLoggedOnceAndThenAsShown()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        rig.Advance(2);
        rig.Scan(S("a", "waiting", rig.Now));
        for (var i = 0; i < 17; i++)
        {
            rig.Advance(1);
            rig.Controller.Pump();
        }
        Assert.Equal(2, rig.Log.Entries.Count);

        rig.Advance(1);
        rig.Controller.Pump();

        Assert.Equal(new[] { BubbleOutcome.ShownTable, BubbleOutcome.Deferred, BubbleOutcome.ShownTable },
            rig.Log.Entries.Select(e => e.Outcome));
        Is(rig.Log.Entries[1], BuddyMoment.NeedsAttention, BubbleOutcome.Deferred, null, null, null, null, false);
        Is(rig.Log.Entries[2], BuddyMoment.NeedsAttention, BubbleOutcome.ShownTable, null, "table", null, "NeedsAttention:1", false);
    }

    // A more important moment replaces the one held: the old one is dropped
    // as outranked, and one no more important than the held one is not held
    // at all.
    [AvaloniaFact]
    public void OutrankingIsLoggedOnBothSides()
    {
        var rig = new Rig();
        rig.RespondedOnA();                                   // shown
        rig.Advance(1);
        rig.Scan(S("a", "generating", rig.Now));              // UserResponded (4): held
        rig.Advance(1);
        rig.Scan(S("a", "waiting", rig.Now));                 // NeedsAttention (7): replaces it
        rig.Advance(1);
        rig.Scan(S("a", "generating", rig.Now));              // UserResponded (4): outranked

        Assert.Equal(5, rig.Log.Entries.Count);
        Is(rig.Log.Entries[1], BuddyMoment.UserResponded, BubbleOutcome.Deferred, null, null, null, null, false);
        Is(rig.Log.Entries[2], BuddyMoment.UserResponded, BubbleOutcome.Dropped, "outranked", null, null, null, false);
        Is(rig.Log.Entries[3], BuddyMoment.NeedsAttention, BubbleOutcome.Deferred, null, null, null, null, false);
        Is(rig.Log.Entries[4], BuddyMoment.UserResponded, BubbleOutcome.Suppressed, "outranked", null, null, null, false);
    }

    [AvaloniaFact]
    public void AHeldMomentIsLoggedAsDroppedWhenTheBuddyHides()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        rig.Advance(2);
        rig.Scan(S("a", "waiting", rig.Now));

        rig.BuddyOn = false;
        rig.Controller.Reapply();

        Is(rig.Log.Entries[^1], BuddyMoment.NeedsAttention, BubbleOutcome.Dropped, "buddy-hidden", null, null, null, false);
        Assert.Equal(3, rig.Log.Entries.Count);
    }

    [AvaloniaFact]
    public void AHeldMomentIsLoggedAsDroppedOnDispose()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        rig.Advance(2);
        rig.Scan(S("a", "waiting", rig.Now));

        rig.Controller.Dispose();
        rig.Controller.Dispose();                             // twice: logged once

        Is(rig.Log.Entries[^1], BuddyMoment.NeedsAttention, BubbleOutcome.Dropped, "disposed", null, null, null, false);
        Assert.Equal(3, rig.Log.Entries.Count);
    }

    // --- suppressed --------------------------------------------------------------

    [AvaloniaFact]
    public void BubblesOffIsLoggedAsSuppressed()
    {
        var rig = new Rig { BubblesOn = false };
        rig.RespondedOnA();

        Is(Only(rig), BuddyMoment.Responded, BubbleOutcome.Suppressed, "bubbles-off", null, null, null, false);
        Assert.Empty(rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void ACooldownIsLoggedAsSuppressed()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        rig.Advance(21);
        rig.RespondedOnA();                                   // 23 s later: inside Responded's 45 s

        // The second pair: its UserResponded is shown, its Responded is not.
        Assert.Equal(new[] { BubbleOutcome.ShownTable, BubbleOutcome.ShownTable, BubbleOutcome.Suppressed },
            rig.Log.Entries.Select(e => e.Outcome));
        Is(rig.Log.Entries[^1], BuddyMoment.Responded, BubbleOutcome.Suppressed, "cooldown", null, null, null, false);
    }

    // --- idle bubbles --------------------------------------------------------------

    // Off (the default): nothing said, nothing spent, the policy never asked.
    // So a welcome ten seconds later is not held behind a gap, and a second
    // long idle thirty seconds after that — inside LongIdle's ten-minute
    // cooldown — still speaks once the switch is on: neither was used up.
    [AvaloniaFact]
    public void IdleOffSaysNothingAndUsesNeitherTheGapNorTheCooldown()
    {
        var rig = new Rig(ai: true);
        var aSince = T0.AddSeconds(-800);
        var bSince = T0.AddSeconds(-785);
        rig.Scan(S("a", "idle", aSince));
        rig.Advance(100);
        rig.Scan(S("a", "idle", aSince));                     // a crosses 15 min

        Is(Only(rig), BuddyMoment.LongIdle, BubbleOutcome.Suppressed, "idle-off", null, null, null, true);
        Assert.Empty(rig.View.Bubbles);
        Assert.Empty(rig.Generator.Calls);

        rig.Advance(10);
        rig.Scan(S("a", "idle", aSince), S("b", "idle", bSince)); // b arrives: shown at once
        Assert.Equal(new[] { "SessionStarted:0" }, rig.View.Bubbles);

        rig.IdleOn = true;
        rig.Advance(20);
        rig.Scan(S("a", "idle", aSince), S("b", "idle", bSince)); // b crosses 15 min
        Assert.Equal(new[] { "SessionStarted:0", "LongIdle:1" }, rig.View.Bubbles);
        Is(rig.Log.Entries[^1], BuddyMoment.LongIdle, BubbleOutcome.ShownTable, null, "table", null, "LongIdle:1", true);
        Assert.Equal(3, rig.Log.Entries.Count);
        Assert.Empty(rig.Generator.Calls);
    }

    [AvaloniaFact]
    public void IdleOnShowsATableLineEvenWithAiOn()
    {
        var rig = new Rig(ai: true) { IdleOn = true };
        rig.LongIdleOnA();

        Is(Only(rig), BuddyMoment.LongIdle, BubbleOutcome.ShownTable, null, "table", null, "LongIdle:0", true);
        Assert.Empty(rig.Generator.Calls);
    }

    // The default, for a controller given no idle switch at all, is off.
    [AvaloniaFact]
    public void AnOmittedIdleSwitchIsOff()
    {
        var view = new FakeBuddyView();
        var now = T0;
        var controller = new BuddyController(
            view, new FakeBuddyStore(), new FakeLedgerSource(), () => true, () => true, () => now,
            BuddyControllerTests.FakeRules);
        controller.Reapply();
        controller.OnSnapshots(new[] { S("a", "idle", T0) });
        now = now.Add(BuddyMoments.LongIdleAfter);
        controller.OnSnapshots(new[] { S("a", "idle", T0) });

        Assert.Empty(view.Bubbles);
        Assert.False(controller.IdleEnabled);
    }

    // --- the switch, and a broken log ------------------------------------------------

    [AvaloniaFact]
    public void LoggingSwitchedOffWritesNothing()
    {
        var rig = new Rig(ai: true) { LogOn = false };
        rig.RespondedOnA();
        rig.Generator.Last.Tcs.SetResult("Quiet.");
        Dispatcher.UIThread.RunJobs();
        rig.LongIdleOnA();

        Assert.Empty(rig.Log.Entries);
        Assert.Equal(new[] { "Quiet." }, rig.View.Bubbles);
    }

    // Read fresh at every decision.
    [AvaloniaFact]
    public void TheSwitchIsReadAtEachDecision()
    {
        var rig = new Rig { LogOn = false };
        rig.RespondedOnA();
        rig.LogOn = true;
        rig.Advance(50);
        rig.RespondedOnA();

        // Nothing from the first pair; both decisions of the second.
        Assert.Equal(new[] { BubbleOutcome.ShownTable, BubbleOutcome.Deferred }, rig.Log.Entries.Select(e => e.Outcome));
        Assert.Equal("UserResponded:1", rig.Log.Entries[0].Text);
    }

    [AvaloniaFact]
    public void ALogThatThrowsNeverAffectsTheBubble()
    {
        var rig = new Rig(ai: true);
        rig.Log.Throw = new IOException("disk full");

        rig.RespondedOnA();
        rig.Generator.Last.Tcs.SetResult("Still said.");
        Dispatcher.UIThread.RunJobs();
        rig.Advance(21);
        rig.Scan(S("a", "waiting", rig.Now));
        rig.Advance(6);
        rig.Controller.Pump();

        Assert.Equal(new[] { "Still said.", "NeedsAttention:1" }, rig.View.Bubbles);
    }

    // A controller with no log at all is today's behaviour, and says so by
    // doing nothing.
    [AvaloniaFact]
    public void NoLogAtAllIsFine()
    {
        var view = new FakeBuddyView();
        var now = T0;
        var controller = new BuddyController(
            view, new FakeBuddyStore(), new FakeLedgerSource(), () => true, () => true, () => now,
            BuddyControllerTests.FakeRules);
        controller.Reapply();
        controller.OnSnapshots(new[] { S("a", "generating", T0) });
        now = now.AddSeconds(2);
        controller.OnSnapshots(new[] { S("a", "idle", now) });

        Assert.Null(controller.BubbleLog);
        Assert.True(controller.LogEnabled);
        Assert.Equal(new[] { "Responded:0" }, view.Bubbles);
    }
}

// The app's own wiring for the log and the idle switch: the file log beside
// settings.json behind its setting, flushed when the controller is disposed.
[Collection("Settings")]
public class BuddyControllerLogFactoryTests
{
    [AvaloniaFact]
    public void TheAppFactoryWiresTheFileLogAndBothSwitches()
    {
        var idleWas = HatchAISettings.BuddyIdleBubblesEnabled;
        var logWas = HatchAISettings.BuddyBubbleLogEnabled;
        var buddyWas = HatchAISettings.BuddyEnabled;
        var bubblesWas = HatchAISettings.BuddyBubblesEnabled;
        var aiWas = HatchAISettings.BuddyAiBubblesEnabled;
        if (File.Exists(BubbleLogFile.DefaultPath)) File.Delete(BubbleLogFile.DefaultPath);

        var controller = BuddyController.CreateForApp(new FakeBuddyView());
        try
        {
            Assert.IsType<BubbleLogFile>(controller.BubbleLog);

            HatchAISettings.BuddyIdleBubblesEnabled = true;
            Assert.True(controller.IdleEnabled);
            HatchAISettings.BuddyIdleBubblesEnabled = false;
            Assert.False(controller.IdleEnabled);
            HatchAISettings.BuddyBubbleLogEnabled = false;
            Assert.False(controller.LogEnabled);
            HatchAISettings.BuddyBubbleLogEnabled = true;
            Assert.True(controller.LogEnabled);

            // One real decision, on the real clock, into the real file.
            HatchAISettings.BuddyEnabled = true;
            HatchAISettings.BuddyBubblesEnabled = true;
            HatchAISettings.BuddyAiBubblesEnabled = false;
            controller.Reapply();
            var now = DateTimeOffset.UtcNow;
            controller.OnSnapshots(new[] { new SessionSnapshot("f", "generating", now, SessionSource.ClaudeCode, "f", "/w/f", "", "") });
            controller.OnSnapshots(new[] { new SessionSnapshot("f", "idle", now, SessionSource.ClaudeCode, "f", "/w/f", "", "") });
        }
        finally
        {
            controller.Dispose();
            HatchAISettings.BuddyIdleBubblesEnabled = idleWas;
            HatchAISettings.BuddyBubbleLogEnabled = logWas;
            HatchAISettings.BuddyEnabled = buddyWas;
            HatchAISettings.BuddyBubblesEnabled = bubblesWas;
            HatchAISettings.BuddyAiBubblesEnabled = aiWas;
        }

        // Flushed by Dispose, so it is on disk now.
        var line = Assert.Single(File.ReadAllLines(BubbleLogFile.DefaultPath));
        Assert.Contains("\"moment\":\"Responded\"", line);
        Assert.Contains("\"outcome\":\"shown-table\"", line);
        File.Delete(BubbleLogFile.DefaultPath);
    }
}
