using Xunit;

namespace HatchAI.Tests;

// The transition table behind the buddy's bubbles. Every arm is a row here,
// because the rule is exactly the kind of thing that fails quietly: a wrong
// arm is not an exception, it is a bubble that never appears or one that
// repeats every two seconds.
public class BuddyMomentsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    internal static SessionSnapshot Snap(
        string id, string state, DateTimeOffset? since = null, SessionSource source = SessionSource.ClaudeCode) =>
        new(id, state, since ?? T0, source, id, "/work/" + id, "", "");

    private static IReadOnlyList<BuddyMomentEvent> Run(
        SessionSnapshot? before, SessionSnapshot? after,
        DateTimeOffset? now = null, DateTimeOffset? previousNow = null)
    {
        var prev = before is null ? Array.Empty<SessionSnapshot>() : new[] { before };
        var cur = after is null ? Array.Empty<SessionSnapshot>() : new[] { after };
        return BuddyMoments.Classify(prev, cur, now ?? T0.AddSeconds(2), previousNow ?? T0);
    }

    [Fact]
    public void AFirstObservationYieldsNothing()
    {
        Assert.Empty(BuddyMoments.Classify(null, new[] { Snap("a", "generating") }, T0));
    }

    [Theory]
    [InlineData("generating", "idle", "Responded")]
    [InlineData("waiting", "idle", "Responded")]
    [InlineData("idle", "waiting", "NeedsAttention")]
    [InlineData("generating", "waiting", "NeedsAttention")]
    [InlineData("idle", "generating", "UserResponded")]
    [InlineData("waiting", "generating", "UserResponded")]
    public void StateTransitionsMapToTheirMoment(string from, string to, string expectedName)
    {
        var events = Run(Snap("a", from), Snap("a", to, T0.AddSeconds(1)));

        var only = Assert.Single(events);
        Assert.Equal(new BuddyMomentEvent("a", Enum.Parse<BuddyMoment>(expectedName)), only);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("waiting")]
    public void AStateThatDidNotChangeSaysNothingBelowTheThresholds(string state)
    {
        Assert.Empty(Run(Snap("a", state), Snap("a", state)));
    }

    [Fact]
    public void ANewSessionIsAStart()
    {
        var events = BuddyMoments.Classify(
            new[] { Snap("a", "idle") }, new[] { Snap("a", "idle"), Snap("b", "generating") }, T0);

        Assert.Equal(new[] { new BuddyMomentEvent("b", BuddyMoment.SessionStarted) }, events);
    }

    [Fact]
    public void ANewSessionAlreadyEndedIsNotAStart()
    {
        Assert.Empty(Run(null, Snap("a", "ended")));
    }

    [Fact]
    public void ASessionThatVanishesEnded()
    {
        var events = Run(Snap("a", "idle"), null);

        Assert.Equal(new[] { new BuddyMomentEvent("a", BuddyMoment.SessionEnded) }, events);
    }

    [Fact]
    public void AnEndedMarkerIsReportedOnceAndNeverAsAFinishedTurn()
    {
        var first = Run(Snap("a", "generating"), Snap("a", "ended"));
        Assert.Equal(new[] { new BuddyMomentEvent("a", BuddyMoment.SessionEnded) }, first);

        // Still ended on the next scan, and then gone: neither says anything
        // more, the end was already announced.
        Assert.Empty(Run(Snap("a", "ended"), Snap("a", "ended")));
        Assert.Empty(Run(Snap("a", "ended"), null));
    }

    [Fact]
    public void GeneratingCrossesTheThinkingThresholdOnce()
    {
        var since = T0;
        var before = Snap("a", "generating", since);

        // Just under: 7 s in at the previous scan and 7.9 s now.
        Assert.Empty(BuddyMoments.Classify(
            new[] { before }, new[] { before }, since.AddSeconds(7.9), since.AddSeconds(5.9)));

        // The scan that crosses it.
        var crossing = BuddyMoments.Classify(
            new[] { before }, new[] { before }, since.AddSeconds(8.5), since.AddSeconds(6.5));
        Assert.Equal(new[] { new BuddyMomentEvent("a", BuddyMoment.Thinking) }, crossing);

        // Exactly at the threshold counts.
        Assert.Single(BuddyMoments.Classify(
            new[] { before }, new[] { before }, since.AddSeconds(8), since.AddSeconds(6)));

        // The next scan is past it in both snapshots: already said.
        Assert.Empty(BuddyMoments.Classify(
            new[] { before }, new[] { before }, since.AddSeconds(10.5), since.AddSeconds(8.5)));
    }

    [Fact]
    public void IdleCrossesTheLongIdleThresholdOnce()
    {
        var idle = Snap("a", "idle", T0);
        var span = BuddyMoments.LongIdleAfter;

        Assert.Empty(BuddyMoments.Classify(
            new[] { idle }, new[] { idle }, T0 + span - TimeSpan.FromSeconds(1), T0 + span - TimeSpan.FromSeconds(3)));

        Assert.Equal(
            new[] { new BuddyMomentEvent("a", BuddyMoment.LongIdle) },
            BuddyMoments.Classify(new[] { idle }, new[] { idle }, T0 + span + TimeSpan.FromSeconds(1), T0 + span - TimeSpan.FromSeconds(1)));

        Assert.Empty(BuddyMoments.Classify(
            new[] { idle }, new[] { idle }, T0 + span + TimeSpan.FromSeconds(3), T0 + span + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WithoutAPreviousScanTimeTheThresholdMomentsStaySilent()
    {
        var gen = Snap("a", "generating", T0);
        var idle = Snap("b", "idle", T0);

        Assert.Empty(BuddyMoments.Classify(
            new[] { gen, idle }, new[] { gen, idle }, T0.AddHours(1)));
    }

    [Fact]
    public void WaitingNeverCountsAsThinkingOrLongIdle()
    {
        var waiting = Snap("a", "waiting", T0);

        Assert.Empty(BuddyMoments.Classify(
            new[] { waiting }, new[] { waiting }, T0.AddHours(1), T0.AddHours(1).AddSeconds(-2)));
    }

    [Fact]
    public void ThresholdsDoNotFireAcrossAStateChange()
    {
        // generating -> idle with a StateSince long ago: the transition wins
        // and nothing threshold-shaped is added on top.
        var events = Run(Snap("a", "generating", T0), Snap("a", "idle", T0.AddSeconds(-1000)),
            now: T0.AddSeconds(2000), previousNow: T0.AddSeconds(1998));

        Assert.Equal(new[] { new BuddyMomentEvent("a", BuddyMoment.Responded) }, events);
    }

    [Fact]
    public void SeveralSessionsAreClassifiedIndependently()
    {
        var before = new[] { Snap("a", "generating"), Snap("b", "idle"), Snap("c", "idle") };
        var after = new[] { Snap("a", "idle"), Snap("b", "waiting"), Snap("d", "idle") };

        var events = BuddyMoments.Classify(before, after, T0.AddSeconds(2), T0);

        Assert.Equal(
            new[]
            {
                new BuddyMomentEvent("a", BuddyMoment.Responded),
                new BuddyMomentEvent("b", BuddyMoment.NeedsAttention),
                new BuddyMomentEvent("d", BuddyMoment.SessionStarted),
                new BuddyMomentEvent("c", BuddyMoment.SessionEnded),
            },
            events);
    }

    [Fact]
    public void ClassifyNeverProducesEvolved()
    {
        var states = new[] { "idle", "generating", "waiting", "ended" };
        foreach (var from in states)
        foreach (var to in states)
        {
            foreach (var e in Run(Snap("a", from), Snap("a", to)))
                Assert.NotEqual(BuddyMoment.Evolved, e.Moment);
        }
    }
}
