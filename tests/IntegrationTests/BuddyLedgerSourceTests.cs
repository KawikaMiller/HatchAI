using Xunit;

namespace HatchAI.Tests;

// The production ledger source over a real temp tree: the two cadences the
// controller asks for reach the two scanner entry points, and only the walk
// finds a transcript nobody has on screen.
public class BuddyLedgerSourceTests
{
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2020-01-01T00:00:00Z");

    private static string Row(string id, long output) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"usage\":{\"output_tokens\":" + output
        + "}},\"timestamp\":\"2026-09-26T00:00:00Z\"}\n";

    [Fact]
    public void TheWalkFindsEveryTranscriptAndTheLivePassOnlyTheOnesGiven()
    {
        var home = Path.Combine(Path.GetTempPath(), "cb-ledgersrc-" + Guid.NewGuid());
        var project = Path.Combine(home, ".claude", "projects", "repo");
        Directory.CreateDirectory(project);
        var onScreen = Path.Combine(project, "a.jsonl");
        var background = Path.Combine(project, "b.jsonl");
        File.WriteAllText(onScreen, Row("m1", 30));
        File.WriteAllText(background, Row("m2", 500));

        BuddyLedgerSource Source() => new(new BuddyLedgerScanner(
            home, new[] { Path.Combine(home, ".claude") }, new[] { Path.Combine(home, ".codex") }));
        var state = BuddyState.Hatch("u", Since);

        var live = Source().Scan(state, new[] { onScreen }, discover: false);
        Assert.Equal(30, live.OutputTokens);

        var walk = Source().Scan(state, new[] { onScreen }, discover: true);
        Assert.Equal(530, walk.OutputTokens);
    }
}
