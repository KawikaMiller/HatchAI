using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace HatchAI.Tests;

// A generator the test answers by hand. Each call hands back an unfinished
// task; the test decides when it finishes and with what — or, with Answer
// set, it finishes at once, the way a very fast model would. Nothing here
// starts a process or reaches a model.
internal sealed class FakeBubbleGenerator : IBubbleGenerator
{
    internal sealed record Call(BubbleRequest Request, CancellationToken Token, TaskCompletionSource<string?> Tcs);

    public readonly List<Call> Calls = new();
    public Func<BubbleRequest, string?>? Answer;
    public Exception? ThrowAtOnce;

    public Task<string?> GenerateAsync(BubbleRequest request, CancellationToken ct)
    {
        if (ThrowAtOnce is not null) throw ThrowAtOnce;
        var tcs = new TaskCompletionSource<string?>();
        Calls.Add(new Call(request, ct, tcs));
        if (Answer is not null) tcs.SetResult(Answer(request));
        return tcs.Task;
    }

    public Call Last => Calls[^1];
}

// CB-202's state machine in BuddyController: when a generated line is asked
// for, what happens to it on every path, and that none of it changes what the
// policy decides. Same deterministic rules as BuddyControllerTests, so a table
// line reads "<Moment>:<draw>" and a generated one is whatever the test says.
[Collection("Settings")]
public class BuddyControllerAiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public readonly FakeBuddyView View = new();
        public readonly FakeBuddyStore Store = new();
        public readonly FakeLedgerSource Ledger = new();
        public readonly FakeBubbleGenerator Generator = new();
        public bool BuddyOn = true;
        public bool BubblesOn = true;
        public bool AiOn = true;
        public bool IdleOn;
        public int AiAsked;
        public DateTimeOffset Now = T0;
        public readonly BuddyController Controller;

        public Rig(bool withGenerator = true)
        {
            Controller = new BuddyController(
                View, Store, Ledger, () => BuddyOn, () => BubblesOn, () => Now,
                BuddyControllerTests.FakeRules,
                withGenerator ? Generator : null,
                () => { AiAsked++; return AiOn; },
                () => IdleOn);
            Controller.Reapply();
        }

        public void Scan(params SessionSnapshot[] sessions) => Controller.OnSnapshots(sessions);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

        // a generating, then idle two seconds later: a Responded on a.
        public void RespondedOnA(string path = "")
        {
            Scan(S("a", "generating", T0, path: path));
            Advance(2);
            Scan(S("a", "idle", Now, path: path));
        }
    }

    private static SessionSnapshot S(string id, string state, DateTimeOffset since,
        SessionSource source = SessionSource.ClaudeCode, string path = "") =>
        new(id, state, since, source, id, "/w/" + id, path, "");

    // --- the request ---------------------------------------------------------

    [AvaloniaFact]
    public void TheRequestCarriesTheGenomeTheMomentAndWhereThePromptLives()
    {
        var rig = new Rig();
        rig.Scan(S("a", "generating", T0, SessionSource.Codex, "/t/a.jsonl"));
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now, SessionSource.Codex, "/t/a.jsonl"));

        var request = Assert.Single(rig.Generator.Calls).Request;
        Assert.Equal(new BubbleRequest(
            BuddyMoment.Responded, BuddyPersonality.Cheerful, BuddyPersonality.Zen, BuddyRarity.Common, BuddySpecies.Duck,
            new BuddyStats(1, 2, 3, 4, 5), "a", null, "/t/a.jsonl", SessionSource.Codex), request);
        // Said on its own as well: the stats are the genome's, field for
        // field, not a default that happens to compare equal.
        Assert.Equal(BuddyControllerTests.Genome.Stats, request.Stats);

        // Nothing on screen while it is being written.
        Assert.Empty(rig.View.Bubbles);
        Assert.True(rig.Controller.Generating);
    }

    [AvaloniaFact]
    public void ASessionWithNoTranscriptSendsNoPath()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        Assert.Null(rig.Generator.Last.Request.TranscriptPath);
        Assert.Equal(SessionSource.ClaudeCode, rig.Generator.Last.Request.Source);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("/", null)]
    [InlineData("  ", null)]
    [InlineData("/home/someone/work/claude-buddy", "claude-buddy")]
    [InlineData("/home/someone/work/claude-buddy/", "claude-buddy")]
    [InlineData(@"C:\src\Claude-Buddy\", "Claude-Buddy")]
    [InlineData("proj", "proj")]
    public void TheProjectIsTheFolderNameAndNeverMoreOfThePath(string? cwd, string? expected) =>
        Assert.Equal(expected, BuddyController.ProjectName(cwd));

    // --- how it lands --------------------------------------------------------

    [AvaloniaFact]
    public void AValidLineIsShown()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        rig.Generator.Last.Tcs.SetResult("Nice work on that one.");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Nice work on that one." }, rig.View.Bubbles);
        Assert.False(rig.Controller.Generating);
    }

    [AvaloniaFact]
    public void ANullAnswerFallsBackToTheTableAtOnce()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        rig.Generator.Last.Tcs.SetResult(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
    }

    // The generator's own validator rejecting a line reaches the controller as
    // null — the contract has one fallback arm, not one per failure — so this
    // is that path end to end with the rejection's shape: an answer that was
    // produced and refused, landing as the table line, with the draw the
    // table would have used anyway.
    [AvaloniaFact]
    public void ARejectedLineFallsBackToTheTableWithTheDrawItWouldHaveHad()
    {
        var rig = new Rig { };
        rig.Generator.Answer = _ => null;
        rig.RespondedOnA();

        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
        Assert.Single(rig.Generator.Calls);
    }

    [AvaloniaFact]
    public void AFaultedCallFallsBackToTheTable()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        rig.Generator.Last.Tcs.SetException(new InvalidOperationException("boom"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void AGeneratorThatThrowsOutrightFallsBackToTheTable()
    {
        var rig = new Rig();
        rig.Generator.ThrowAtOnce = new InvalidOperationException("boom");
        rig.RespondedOnA();

        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
        Assert.False(rig.Controller.Generating);
    }

    [AvaloniaFact]
    public void TimingOutShowsTheTableLineAndCancelsTheCall()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        rig.Advance(5.9);
        rig.Controller.Pump();
        Assert.Empty(rig.View.Bubbles);
        Assert.False(call.Token.IsCancellationRequested);

        rig.Advance(0.1);
        rig.Controller.Pump();
        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
        Assert.True(call.Token.IsCancellationRequested);

        // A line arriving after the table has spoken is not said as well.
        call.Tcs.SetResult("Too late.");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
    }

    // The answer comes back on whatever thread the generator finished on; the
    // view is touched only on the UI thread. This used to flake (3 runs in
    // 15): with no synchronisation context current where the await begins,
    // its continuation runs on the completing thread, so the controller now
    // checks the thread itself and posts the answer back. Forcing the
    // no-context case makes the test deterministic instead of depending on
    // what the runner left set, and the view records where it was touched,
    // rather than the test reporting where it happened to be standing.
    [AvaloniaFact]
    public void AnAnswerFinishedOffTheUiThreadIsShownOnIt()
    {
        var rig = new Rig();

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { rig.RespondedOnA(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        rig.View.Calls.Clear();

        Task.Run(() => rig.Generator.Last.Tcs.SetResult("From elsewhere.")).Wait();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (rig.View.Bubbles.Count == 0 && DateTime.UtcNow < until)
            Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "From elsewhere." }, rig.View.Bubbles);
        Assert.Equal(new[] { true }, rig.View.BubbleOnUiThread);
    }

    // --- the opt-in ----------------------------------------------------------

    // Acceptance criterion 1: with AI bubbles off, the generator is never
    // asked for anything, and the table speaks exactly as before.
    [AvaloniaFact]
    public void WithAiOffTheGeneratorIsNeverCalled()
    {
        var rig = new Rig { };
        rig.AiOn = false;
        rig.RespondedOnA();
        rig.Advance(2);
        rig.Scan(S("a", "waiting", rig.Now));
        rig.Advance(30);
        rig.Controller.Pump();

        Assert.Empty(rig.Generator.Calls);
        Assert.Equal(new[] { "Responded:0", "NeedsAttention:1" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void WithNoGeneratorTheTableSpeaksAndTheOptInIsNotEvenAsked()
    {
        var rig = new Rig(withGenerator: false);
        rig.RespondedOnA();

        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
        Assert.Equal(0, rig.AiAsked);
    }

    // A controller given a generator and no opt-in at all treats the opt-in
    // as off: the default is today's behaviour, not a call.
    [AvaloniaFact]
    public void AnOmittedOptInIsOff()
    {
        var generator = new FakeBubbleGenerator();
        var view = new FakeBuddyView();
        var now = T0;
        var controller = new BuddyController(
            view, new FakeBuddyStore(), new FakeLedgerSource(), () => true, () => true, () => now,
            BuddyControllerTests.FakeRules, generator);
        controller.Reapply();
        controller.OnSnapshots(new[] { S("a", "generating", T0) });
        now = now.AddSeconds(2);
        controller.OnSnapshots(new[] { S("a", "idle", now) });

        Assert.Empty(generator.Calls);
        Assert.Equal(new[] { "Responded:0" }, view.Bubbles);
    }

    // --- stale calls: dropped, nothing shown ---------------------------------

    [AvaloniaFact]
    public void FocusMovingOffTheSessionDropsTheCall()
    {
        var rig = new Rig();
        rig.Scan(S("a", "generating", T0), S("b", "idle", T0.AddSeconds(-100)));
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now), S("b", "idle", T0.AddSeconds(-100)));
        var call = rig.Generator.Last;
        Assert.Equal("a", rig.Controller.Focus);

        // b starts waiting, which takes focus through a's stickiness; a
        // itself is unchanged, so only the focus rule can drop the call.
        rig.Advance(2);
        rig.Scan(S("a", "idle", T0.AddSeconds(2)), S("b", "waiting", rig.Now));
        Assert.Equal("b", rig.Controller.Focus);

        Assert.True(call.Token.IsCancellationRequested);
        call.Tcs.SetResult("About a.");
        Dispatcher.UIThread.RunJobs();
        rig.Advance(10);
        rig.Controller.Pump();

        Assert.Empty(rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void TheSessionsStateChangingDropsTheCall()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        // The user sent another prompt before the line came back.
        rig.Advance(1);
        rig.Scan(S("a", "generating", rig.Now));

        Assert.True(call.Token.IsCancellationRequested);
        Assert.False(rig.Controller.Generating);
        rig.Advance(10);
        rig.Controller.Pump();
        Assert.Empty(rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void AScanThatChangesNothingKeepsTheCall()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        rig.Advance(1);
        rig.Scan(S("a", "idle", T0.AddSeconds(2)));
        call.Tcs.SetResult("Still relevant.");
        Dispatcher.UIThread.RunJobs();

        Assert.False(call.Token.IsCancellationRequested);
        Assert.Equal(new[] { "Still relevant." }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void HidingTheBuddyDropsTheCall()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        rig.BuddyOn = false;
        rig.Controller.Reapply();
        Assert.True(call.Token.IsCancellationRequested);

        call.Tcs.SetResult("Nobody hears this.");
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void TurningBubblesOffDropsTheCallAtTheNextPump()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        rig.BubblesOn = false;
        rig.Advance(1);
        rig.Controller.Pump();

        Assert.True(call.Token.IsCancellationRequested);
        rig.Advance(10);
        rig.Controller.Pump();
        Assert.Empty(rig.View.Bubbles);
    }

    // Switched off between pumps and answered before the next one: the
    // answer is checked against the settings as they are now.
    [AvaloniaFact]
    public void TurningBubblesOffIsHonouredWhenTheAnswerLands()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        rig.BubblesOn = false;
        rig.Generator.Last.Tcs.SetResult("Unwanted.");
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(rig.View.Bubbles);
        Assert.False(rig.Controller.Generating);
    }

    [AvaloniaFact]
    public void DisposingDropsTheCall()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        rig.Controller.Dispose();
        Assert.True(call.Token.IsCancellationRequested);

        call.Tcs.SetResult("After quit.");
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(rig.View.Bubbles);
    }

    // --- AI switched off mid-flight: the moment still stands -----------------

    [AvaloniaFact]
    public void TurningAiOffMidCallCancelsItAndTheTableSpeaksAtTheNextPump()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        var call = rig.Generator.Last;

        rig.AiOn = false;
        rig.Advance(1);
        rig.Controller.Pump();

        Assert.True(call.Token.IsCancellationRequested);
        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void TurningAiOffBeforeTheAnswerLandsShowsTheTableNotTheAnswer()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        rig.AiOn = false;
        rig.Generator.Last.Tcs.SetResult("Generated anyway.");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Responded:0" }, rig.View.Bubbles);
    }

    // --- one call at a time, and the policy unchanged ------------------------

    [AvaloniaFact]
    public void ADeferredMomentSpendsNothingUntilItIsShown()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        rig.Generator.Last.Tcs.SetResult("First.");
        Dispatcher.UIThread.RunJobs();

        // Inside the gap: held, and no call made for it.
        rig.Advance(2);
        rig.Scan(S("a", "waiting", rig.Now, path: "/t/a.jsonl"));
        rig.Advance(10);
        rig.Controller.Pump();
        Assert.Single(rig.Generator.Calls);

        // The gap opens: now, and only now, it is asked for — about a.
        rig.Advance(8);
        rig.Controller.Pump();
        Assert.Equal(2, rig.Generator.Calls.Count);
        Assert.Equal(BuddyMoment.NeedsAttention, rig.Generator.Last.Request.Moment);
        Assert.Equal("a", rig.Generator.Last.Request.Project);
        Assert.Equal("/t/a.jsonl", rig.Generator.Last.Request.TranscriptPath);
    }

    [AvaloniaFact]
    public void OnlyOneCallIsEverInFlight()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        // More moments while the first call is out: the policy holds them,
        // so nothing else is asked for.
        rig.Advance(1);
        rig.Scan(S("a", "waiting", rig.Now));
        rig.Advance(1);
        rig.Scan(S("a", "generating", rig.Now));
        rig.Controller.Pump();

        Assert.Single(rig.Generator.Calls);
    }

    // The pump starved past the timeout and the gap (it runs every second in
    // the app, so this is a stall, not a schedule): the newer moment wins, and
    // the older call is dropped rather than a second one started beside it.
    [AvaloniaFact]
    public void ANewerBubbleSupersedesACallThePumpNeverTimedOut()
    {
        var rig = new Rig();

        // a starts generating: a UserResponded call. It stays generating, so
        // nothing about a's session changes and neither staleness rule is
        // what removes the first call — only the second one's arrival, a
        // Thinking crossing its 8 s threshold once the gap has opened.
        rig.Scan(S("a", "idle", T0));
        rig.Advance(2);
        rig.Scan(S("a", "generating", rig.Now));
        var first = rig.Generator.Last;
        Assert.Equal(BuddyMoment.UserResponded, first.Request.Moment);
        var since = rig.Now;

        rig.Advance(1);
        rig.Scan(S("a", "generating", since));
        rig.Advance(20);
        rig.Scan(S("a", "generating", since));
        var second = rig.Generator.Last;
        Assert.Equal(BuddyMoment.Thinking, second.Request.Moment);

        Assert.Equal(2, rig.Generator.Calls.Count);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);

        // The superseded call's answer is stale and changes nothing; the
        // current one's still lands.
        first.Tcs.SetResult("Old news.");
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(rig.View.Bubbles);
        Assert.True(rig.Controller.Generating);

        second.Tcs.SetResult("Back at it.");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "Back at it." }, rig.View.Bubbles);
    }

    // BuddyControllerTests.ACooldownDropIsNotHeld with a voice that answers
    // at once: the same moments are said at the same times, generated.
    [AvaloniaFact]
    public void TheGapAndCooldownsAreUnchangedWithAiOn()
    {
        var rig = new Rig();
        rig.Generator.Answer = r => "gen:" + r.Moment;
        rig.RespondedOnA();                               // Responded
        rig.Advance(21);
        rig.Scan(S("a", "generating", rig.Now));          // UserResponded
        rig.Advance(2);
        rig.Scan(S("a", "idle", rig.Now));                // Responded: gap says wait
        rig.Advance(30);
        rig.Controller.Pump();                            // ...and its cooldown drops it

        Assert.Equal(new[] { "gen:Responded", "gen:UserResponded" }, rig.View.Bubbles);
        Assert.Equal(2, rig.Generator.Calls.Count);
    }

    // --- which moments are generated (owner decision after live use) ---------

    // A welcome, a goodbye and an evolution have no fresh work to react to:
    // the table, with AI on, and the generator never asked.
    [AvaloniaFact]
    public void AWelcomeUsesTheTableEvenWithAiOn()
    {
        var rig = new Rig();
        rig.Scan(S("a", "idle", T0));
        rig.Advance(2);
        rig.Scan(S("a", "idle", T0), S("b", "idle", rig.Now, path: "/t/b.jsonl"));

        Assert.Empty(rig.Generator.Calls);
        Assert.Equal(new[] { "SessionStarted:0" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public void AnEndedSessionUsesTheTableEvenWithAiOn()
    {
        var rig = new Rig();
        rig.Scan(S("a", "idle", T0));
        rig.Advance(200);
        rig.Scan(Array.Empty<SessionSnapshot>());

        Assert.Empty(rig.Generator.Calls);
        Assert.Equal(new[] { "SessionEnded:0" }, rig.View.Bubbles);
    }

    [AvaloniaFact]
    public async Task AnEvolutionUsesTheTableEvenWithAiOn()
    {
        var rig = new Rig();
        rig.Scan(S("a", "idle", T0, path: "/t/a.jsonl"));
        // Across the egg's hatch, the first evolution a buddy has.
        rig.Ledger.Tokens = 2_600;

        await rig.Controller.LedgerRoundAsync();

        Assert.Empty(rig.Generator.Calls);
        Assert.Equal(new[] { "Evolved:0" }, rig.View.Bubbles);
    }

    // Idle bubbles on: said, and still from the table with AI on.
    [AvaloniaFact]
    public void ALongIdleUsesTheTableEvenWithAiOn()
    {
        var rig = new Rig { IdleOn = true };
        rig.Scan(S("a", "idle", T0));
        rig.Advance(BuddyMoments.LongIdleAfter.TotalSeconds);
        rig.Scan(S("a", "idle", T0));

        Assert.Empty(rig.Generator.Calls);
        Assert.Equal(new[] { "LongIdle:0" }, rig.View.Bubbles);
    }

    // The four work moments are the ones that ask, each once, each about its
    // own moment. Answered at once so the gap is the only thing between them.
    [AvaloniaFact]
    public void EachWorkMomentAsksTheGenerator()
    {
        var rig = new Rig();
        rig.Generator.Answer = r => "gen:" + r.Moment;

        rig.RespondedOnA();                                   // Responded
        rig.Advance(21);
        rig.Scan(S("a", "generating", rig.Now));              // UserResponded
        var since = rig.Now;
        rig.Advance(1);
        rig.Scan(S("a", "generating", since));
        rig.Advance(20);
        rig.Scan(S("a", "generating", since));                // Thinking
        rig.Advance(21);
        rig.Scan(S("a", "waiting", rig.Now));                 // NeedsAttention

        Assert.Equal(
            new[] { BuddyMoment.Responded, BuddyMoment.UserResponded, BuddyMoment.Thinking, BuddyMoment.NeedsAttention },
            rig.Generator.Calls.Select(c => c.Request.Moment));
        Assert.Equal(new[] { "gen:Responded", "gen:UserResponded", "gen:Thinking", "gen:NeedsAttention" },
            rig.View.Bubbles);
    }

    // --- the hide clock ------------------------------------------------------

    [AvaloniaFact]
    public void TheBubblesTimeStartsWhenItIsShownNotWhenItWasAskedFor()
    {
        var rig = new Rig();
        rig.RespondedOnA();

        rig.Advance(4);
        rig.Controller.Pump();
        rig.Generator.Last.Tcs.SetResult("Took a moment.");
        Dispatcher.UIThread.RunJobs();
        rig.View.Calls.Clear();

        // Ten seconds after the request, but only six after the show.
        rig.Advance(5.9);
        rig.Controller.Pump();
        Assert.Empty(rig.View.Calls);

        rig.Advance(0.1);
        rig.Controller.Pump();
        Assert.Equal(new[] { "hidebubble" }, rig.View.Calls);
    }

    [AvaloniaFact]
    public void ATimedOutBubbleGetsItsFullTimeToo()
    {
        var rig = new Rig();
        rig.RespondedOnA();
        rig.Advance(6);
        rig.Controller.Pump();                            // table line, at +6
        rig.View.Calls.Clear();

        rig.Advance(5.9);
        rig.Controller.Pump();
        Assert.Empty(rig.View.Calls);
        rig.Advance(0.1);
        rig.Controller.Pump();
        Assert.Equal(new[] { "hidebubble" }, rig.View.Calls);
    }
}

// The app's own wiring. Checked without letting a call reach the real CLI:
// the factory's generator is inspected, never asked.
[Collection("Settings")]
public class BuddyControllerAiFactoryTests
{
    [AvaloniaFact]
    public void TheAppFactoryWiresTheCliVoiceBehindTheOptIn()
    {
        var aiWas = HatchAISettings.BuddyAiBubblesEnabled;
        var controller = BuddyController.CreateForApp(new FakeBuddyView());
        try
        {
            Assert.IsType<ClaudeCliBubbleGenerator>(controller.Generator);

            HatchAISettings.BuddyAiBubblesEnabled = false;
            Assert.False(controller.AiEnabled);
            HatchAISettings.BuddyAiBubblesEnabled = true;
            Assert.True(controller.AiEnabled);
        }
        finally
        {
            HatchAISettings.BuddyAiBubblesEnabled = aiWas;
            controller.Dispose();
        }
    }
}
