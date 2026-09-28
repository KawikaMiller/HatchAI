using System.Text;
using Xunit;

namespace HatchAI.Tests;

// TranscriptReader's tail window against real files on disk — the only part of
// Claude Buddy's TranscriptReader HatchAI kept, because ClaudeCliBubbleGenerator
// reads the user's last prompt through it.
//
// Claude Buddy tested this window only through LatestAssistantText, which did
// not come across; these cases say the same things about TailLines directly.
// The row shapes are Claude Buddy's own fixtures, which were copied from real
// transcripts.
public class TranscriptReaderTests : IDisposable
{
    private const int TailBytes = 262144;

    private const string UserSaid =
        """{"type":"user","uuid":"u1","timestamp":"2026-08-16T10:00:00Z","message":{"role":"user","content":"fix the arrangement test"}}""";

    private const string AssistantSaid =
        """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the nested-team case."}]}}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hatchai-tail-" + Guid.NewGuid().ToString("N"));

    public TranscriptReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void ASmallFileComesBackWholeOneRowPerLine()
    {
        var path = Write(UserSaid + "\n" + AssistantSaid + "\n");

        Assert.Equal(new[] { UserSaid, AssistantSaid }, TranscriptReader.TailLines(path));
    }

    // Blank lines are not rows. A transcript ending in a newline, or with a
    // stray empty line, must not hand back an empty string for a parser to
    // choke on.
    [Fact]
    public void BlankLinesAreDropped()
    {
        var path = Write("\n" + UserSaid + "\n\n" + AssistantSaid + "\n\n");

        Assert.Equal(new[] { UserSaid, AssistantSaid }, TranscriptReader.TailLines(path));
    }

    // The "drop the first partial line" rule: TailLines seeks to
    // Length - TailBytes, so unless that lands exactly on a line boundary the
    // first line it reads is the torn back half of a row. Padding the front
    // with one long row puts the window's start in the middle of it.
    [Fact]
    public void ThePartialLineAtTheStartOfTheWindowIsDropped()
    {
        var padding = """{"type":"user","message":{"content":""" + "\"" + new string('x', TailBytes) + "\"}}";
        var path = Write(padding + "\n" + UserSaid + "\n" + AssistantSaid + "\n");

        var lines = TranscriptReader.TailLines(path);

        Assert.Equal(new[] { UserSaid, AssistantSaid }, lines);
    }

    // A window that starts at the beginning of the file has nothing torn in
    // it, so its first line is kept — the drop happens only past the start.
    [Fact]
    public void AWindowStartingAtTheFileStartKeepsItsFirstLine()
    {
        var path = Write(UserSaid + "\n" + AssistantSaid + "\n");

        var lines = TranscriptReader.TailLines(path, tailBytes: 1_000_000);

        Assert.Equal(UserSaid, lines[0]);
    }

    // The sized overload with a window smaller than the last row: whatever
    // comes back is a torn tail of that row or nothing, never the row before.
    [Fact]
    public void AWindowSmallerThanTheLastRowNeverReachesTheRowBefore()
    {
        var path = Write(UserSaid + "\n" + AssistantSaid);

        var lines = TranscriptReader.TailLines(path, tailBytes: 20);

        Assert.DoesNotContain(UserSaid, lines);
    }

    [Fact]
    public void AMissingFileIsNoLinesRatherThanAThrow()
    {
        Assert.Empty(TranscriptReader.TailLines(Path.Combine(_dir, "nope.jsonl")));
        Assert.Empty(TranscriptReader.TailLines(Path.Combine(_dir, "nope.jsonl"), 10));
    }

    // A transcript Claude Code is still writing to is open for writing while
    // this reads it; FileShare.ReadWrite is what lets the read through.
    [Fact]
    public void AFileOpenForWritingCanStillBeRead()
    {
        var path = Write(UserSaid + "\n");
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        Assert.Equal(new[] { UserSaid }, TranscriptReader.TailLines(path));
    }
}
