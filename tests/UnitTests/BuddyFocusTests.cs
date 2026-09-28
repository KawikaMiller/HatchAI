using Xunit;

namespace HatchAI.Tests;

public class BuddyFocusTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static SessionSnapshot S(string id, string state, double secondsAgo) =>
        BuddyMomentsTests.Snap(id, state, Now.AddSeconds(-secondsAgo));

    [Fact]
    public void NoSessionsMeansNoFocus()
    {
        Assert.Null(BuddyFocus.Choose(Array.Empty<SessionSnapshot>(), "a", Now));
    }

    [Fact]
    public void WithNoFocusTheMostRecentTransitionWins()
    {
        var sessions = new[] { S("a", "idle", 60), S("b", "generating", 5), S("c", "idle", 30) };

        Assert.Equal("b", BuddyFocus.Choose(sessions, null, Now));
    }

    [Fact]
    public void AFocusThatNoLongerExistsIsReplaced()
    {
        var sessions = new[] { S("a", "idle", 60), S("b", "idle", 5) };

        Assert.Equal("b", BuddyFocus.Choose(sessions, "gone", Now));
    }

    [Fact]
    public void WaitingBeatsARecentTransition()
    {
        var sessions = new[] { S("a", "generating", 1), S("b", "waiting", 500) };

        Assert.Equal("b", BuddyFocus.Choose(sessions, null, Now));
    }

    [Fact]
    public void TheNewestWaitingSessionWinsAmongWaiting()
    {
        var sessions = new[] { S("a", "waiting", 100), S("b", "waiting", 20) };

        Assert.Equal("b", BuddyFocus.Choose(sessions, null, Now));
    }

    [Fact]
    public void TiesAreBrokenByIdSoTheAnswerIsStable()
    {
        var sessions = new[] { S("b", "idle", 10), S("a", "idle", 10) };

        Assert.Equal("a", BuddyFocus.Choose(sessions, null, Now));
    }

    [Fact]
    public void ARecentFocusIsStickyAgainstAnotherBusySession()
    {
        // The focus changed 4 s ago; another session changed 1 s ago. Not
        // yet: hopping on every scan is reacting to nothing.
        var sessions = new[] { S("a", "generating", 4), S("b", "generating", 1) };

        Assert.Equal("a", BuddyFocus.Choose(sessions, "a", Now));
    }

    [Fact]
    public void AFocusQuietForTenSecondsYieldsToTheMostRecent()
    {
        var sessions = new[] { S("a", "idle", 10), S("b", "generating", 1) };

        Assert.Equal("b", BuddyFocus.Choose(sessions, "a", Now));
    }

    [Fact]
    public void AQuietFocusThatIsStillTheBestKeepsItself()
    {
        var sessions = new[] { S("a", "idle", 300), S("b", "idle", 900) };

        Assert.Equal("a", BuddyFocus.Choose(sessions, "a", Now));
    }

    [Fact]
    public void ASessionThatStartsWaitingPreemptsAStickyFocus()
    {
        var sessions = new[] { S("a", "generating", 2), S("b", "waiting", 1) };

        Assert.Equal("b", BuddyFocus.Choose(sessions, "a", Now));
    }

    [Fact]
    public void ANewerWaitingSessionPreemptsAnOlderWaitingFocus()
    {
        var sessions = new[] { S("a", "waiting", 3), S("b", "waiting", 1) };

        Assert.Equal("b", BuddyFocus.Choose(sessions, "a", Now));
    }

    [Fact]
    public void AnOlderWaitingSessionDoesNotStealFromAWaitingFocus()
    {
        var sessions = new[] { S("a", "waiting", 1), S("b", "waiting", 3) };

        Assert.Equal("a", BuddyFocus.Choose(sessions, "a", Now));
    }
}
