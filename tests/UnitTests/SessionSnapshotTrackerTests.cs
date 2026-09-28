using Xunit;

namespace HatchAI.Tests;

public class SessionSnapshotTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static (string, StatusFile) Entry(string id, string state, string title = "", string cwd = "/w/proj") =>
        (id, new StatusFile
        {
            State = state, Title = title, Cwd = cwd, TranscriptPath = "/t/" + id + ".jsonl",
            Source = SessionSource.Codex,
        });

    [Fact]
    public void CopiesWhatTheBuddyNeedsAndNothingMutable()
    {
        var tracker = new SessionSnapshotTracker();
        var snap = Assert.Single(tracker.Build(new[] { Entry("a", "idle", "Nice title") }, T0));

        Assert.Equal("a", snap.SessionId);
        Assert.Equal("idle", snap.State);
        Assert.Equal(T0, snap.StateSince);
        Assert.Equal(SessionSource.Codex, snap.Source);
        Assert.Equal("Nice title", snap.Label);
        Assert.Equal("/w/proj", snap.Cwd);
        Assert.Equal("/t/a.jsonl", snap.TranscriptPath);
        // HatchAI cannot see agent teams, so there is never a lead.
        Assert.Equal("", snap.Lead);
    }

    [Fact]
    public void StateSinceIsCarriedWhileTheStateIsUnchangedAndResetWhenItChanges()
    {
        var tracker = new SessionSnapshotTracker();
        tracker.Build(new[] { Entry("a", "generating") }, T0);

        var same = tracker.Build(new[] { Entry("a", "generating") }, T0.AddSeconds(2)).Single();
        Assert.Equal(T0, same.StateSince);

        var changed = tracker.Build(new[] { Entry("a", "idle") }, T0.AddSeconds(4)).Single();
        Assert.Equal(T0.AddSeconds(4), changed.StateSince);

        var again = tracker.Build(new[] { Entry("a", "idle") }, T0.AddSeconds(6)).Single();
        Assert.Equal(T0.AddSeconds(4), again.StateSince);
    }

    [Fact]
    public void ASessionThatLeavesAndReturnsStartsAFreshStretch()
    {
        var tracker = new SessionSnapshotTracker();
        tracker.Build(new[] { Entry("a", "idle") }, T0);
        tracker.Build(Array.Empty<(string, StatusFile)>(), T0.AddSeconds(2));

        var back = tracker.Build(new[] { Entry("a", "idle") }, T0.AddSeconds(4)).Single();

        Assert.Equal(T0.AddSeconds(4), back.StateSince);
    }

    // Claude Buddy's tray DisplayName, minus the agent-team arm: title first,
    // then the cwd's last segment on either separator, then the id.
    [Theory]
    [InlineData("  Nice title ", "/w/proj", "Nice title")]
    [InlineData("", "/w/proj", "proj")]
    [InlineData("", @"C:\work\proj\", "proj")]
    [InlineData("   ", "", "the-id")]
    [InlineData("", "///", "the-id")]
    public void DisplayNamePrefersTitleThenFolderThenId(string title, string cwd, string expected)
    {
        var status = new StatusFile { Title = title, Cwd = cwd };
        Assert.Equal(expected, SessionSnapshotTracker.DisplayName("the-id", status));
    }
}
