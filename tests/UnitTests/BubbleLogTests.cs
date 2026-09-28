using System.Text.Json;
using Xunit;

namespace HatchAI.Tests;

// The bubble log's format and its file (CB-202, after live use). The format
// is asserted as parsed JSON as well as bytes, because a person reads this
// file and a tool may grep it; the file is driven in a scratch folder with
// the synchronous schedule unless the test is about the background drain.
public class BubbleLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 23, 59, 1, 123, TimeSpan.FromHours(-7));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-bubblelog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static BubbleLogEntry Entry(
        BubbleOutcome outcome = BubbleOutcome.ShownTable, string? text = "Hello.", string? reason = null,
        string? source = BubbleLogEntry.TableSource, long? latency = null, bool ai = false) =>
        new(T0, BuddyMoment.Responded, outcome, reason, source, latency, text, ai);

    private BubbleLogFile SyncLog(long maxBytes = BubbleLogFile.DefaultMaxBytes) =>
        new(_dir, maxBytes, schedule: drain => drain());

    private string[] Lines(string? path = null) =>
        File.ReadAllLines(path ?? Path.Combine(_dir, BubbleLogFile.FileName));

    // ---- the format -------------------------------------------------------

    [Fact]
    public void AnEntryIsOneLineWithEveryKeyInOrder()
    {
        var line = Entry(BubbleOutcome.Fallback, "Back to it.", "timed-out", BubbleLogEntry.TableSource, 6000, true)
            .ToJsonLine();

        Assert.Equal(
            "{\"ts\":\"2026-09-27T06:59:01.123Z\",\"moment\":\"Responded\",\"outcome\":\"fallback\","
            + "\"reason\":\"timed-out\",\"source\":\"table\",\"latencyMs\":6000,\"text\":\"Back to it.\","
            + "\"aiEnabled\":true}",
            line);
    }

    [Fact]
    public void NullFieldsAreWrittenAsNullNotLeftOut()
    {
        using var doc = JsonDocument.Parse(
            new BubbleLogEntry(T0, BuddyMoment.LongIdle, BubbleOutcome.Suppressed, "idle-off", null, null, null, false)
                .ToJsonLine());
        var root = doc.RootElement;

        Assert.Equal(8, root.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("source").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("latencyMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("text").ValueKind);
        Assert.Equal("LongIdle", root.GetProperty("moment").GetString());
        Assert.False(root.GetProperty("aiEnabled").GetBoolean());
    }

    [Theory]
    [InlineData(nameof(BubbleOutcome.ShownTable), "shown-table")]
    [InlineData(nameof(BubbleOutcome.ShownAi), "shown-ai")]
    [InlineData(nameof(BubbleOutcome.Fallback), "fallback")]
    [InlineData(nameof(BubbleOutcome.Dropped), "dropped")]
    [InlineData(nameof(BubbleOutcome.Suppressed), "suppressed")]
    [InlineData(nameof(BubbleOutcome.Deferred), "deferred")]
    public void EachOutcomeHasItsName(string outcome, string name) =>
        Assert.Equal(name, BubbleLogEntry.OutcomeName(Enum.Parse<BubbleOutcome>(outcome)));

    [Fact]
    public void EveryOutcomeHasADistinctName() =>
        Assert.Equal(Enum.GetValues<BubbleOutcome>().Length,
            Enum.GetValues<BubbleOutcome>().Select(BubbleLogEntry.OutcomeName).Distinct().Count());

    [Theory]
    [InlineData(nameof(BubbleFailure.NoCli), "no-cli")]
    [InlineData(nameof(BubbleFailure.StartFailed), "start-failed")]
    [InlineData(nameof(BubbleFailure.NonZeroExit), "non-zero-exit")]
    [InlineData(nameof(BubbleFailure.ProcessFailed), "process-failed")]
    [InlineData(nameof(BubbleFailure.BadOutput), "bad-output")]
    [InlineData(nameof(BubbleFailure.RejectedByValidator), "rejected-by-validator")]
    [InlineData(nameof(BubbleFailure.Threw), "threw")]
    [InlineData(nameof(BubbleFailure.Cancelled), "cancelled")]
    [InlineData(nameof(BubbleFailure.TimedOut), "timed-out")]
    [InlineData(nameof(BubbleFailure.Unknown), "unknown")]
    public void EachFailureHasItsReason(string failure, string reason) =>
        Assert.Equal(reason, BubbleLogReason.Of(Enum.Parse<BubbleFailure>(failure)));

    [Fact]
    public void EveryFailureHasADistinctReason() =>
        Assert.Equal(Enum.GetValues<BubbleFailure>().Length,
            Enum.GetValues<BubbleFailure>().Select(BubbleLogReason.Of).Distinct().Count());

    // The text is untrusted: whatever it holds, it stays one line of valid
    // JSON and reads back exactly. Relaxed escaping keeps an apostrophe and
    // an accent readable.
    [Fact]
    public void HostileTextStaysOnOneLineAndRoundTrips()
    {
        const string text = "line one\nline two\r\t\"quoted\" \\ it's café </script>";
        var line = Entry(text: text).ToJsonLine();

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("it's café", line);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(text, doc.RootElement.GetProperty("text").GetString());
    }

    // ---- the file -----------------------------------------------------------

    [Fact]
    public void TheDefaultFileSitsBesideSettingsJson()
    {
        Assert.Equal(HatchAISettings.Directory, BubbleLogFile.DefaultDirectory);
        Assert.Equal(Path.Combine(HatchAISettings.Directory, "bubble-log.jsonl"), BubbleLogFile.DefaultPath);
        Assert.Equal(Path.Combine(HatchAISettings.Directory, "bubble-log.jsonl"), new BubbleLogFile().Path_);
    }

    [Fact]
    public void EntriesAreAppendedInOrderAndTheFolderIsCreated()
    {
        using var log = SyncLog();
        log.Log(Entry(text: "one"));
        log.Log(Entry(text: "two"));

        using var second = SyncLog();
        second.Log(Entry(text: "three"));

        Assert.Equal(new[] { "one", "two", "three" },
            Lines().Select(l => JsonDocument.Parse(l).RootElement.GetProperty("text").GetString()));
    }

    // No byte-order mark: the first line must parse as JSON on its own.
    [Fact]
    public void TheFileHasNoByteOrderMark()
    {
        using (var log = SyncLog()) log.Log(Entry());

        var bytes = File.ReadAllBytes(Path.Combine(_dir, BubbleLogFile.FileName));
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    [Fact]
    public void PastTheLimitTheFileRollsOverAndReplacesTheOlderOne()
    {
        var line = Entry().ToJsonLine();
        var max = (line.Length + 1) * 3;   // three lines fit, the fourth rolls
        using var log = SyncLog(max);

        for (var i = 0; i < 3; i++) log.Log(Entry());
        Assert.False(File.Exists(log.PreviousPath));

        log.Log(Entry(text: "fourth"));
        Assert.Equal(3, Lines(log.PreviousPath).Length);
        Assert.Single(Lines());

        // A second rollover replaces the first one's file rather than failing.
        for (var i = 0; i < 3; i++) log.Log(Entry(text: "later."));
        Assert.Contains("fourth", Lines(log.PreviousPath)[0]);
        Assert.Single(Lines());
    }

    [Fact]
    public void ManyEntriesStayBounded()
    {
        const long max = 4096;
        using var log = SyncLog(max);

        for (var i = 0; i < 500; i++) log.Log(Entry(text: "entry " + i));

        var one = Entry(text: "entry 499").ToJsonLine().Length + 1;
        Assert.True(new FileInfo(log.Path_).Length < max + one);
        Assert.True(new FileInfo(log.PreviousPath).Length < max + one);
        Assert.Contains("entry 499", Lines().Last());
    }

    // A folder that cannot be created — its parent is a file — loses the
    // entry and throws nothing, and the log still works once it can.
    [Fact]
    public void AnUnwritableFolderIsSwallowed()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "a-file");
        File.WriteAllText(blocker, "x");
        using var log = new BubbleLogFile(Path.Combine(blocker, "sub"), schedule: drain => drain());

        log.Log(Entry());
        log.Log(Entry());

        Assert.False(File.Exists(log.Path_));
    }

    // Held open by someone else without sharing: the write fails, quietly.
    [Fact]
    public void ALockedFileIsSwallowed()
    {
        Directory.CreateDirectory(_dir);
        using var log = SyncLog();
        using (new FileStream(log.Path_, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            log.Log(Entry(text: "lost"));
        }

        log.Log(Entry(text: "kept"));
        Assert.Equal(new[] { "kept" },
            Lines().Select(l => JsonDocument.Parse(l).RootElement.GetProperty("text").GetString()));
    }

    // The production schedule: many threads logging at once, through the
    // background drain, and every entry on disk once it is disposed.
    [Fact]
    public void ConcurrentWritersLoseNothing()
    {
        var log = new BubbleLogFile(_dir);
        const int threads = 8, each = 100;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < each; i++) log.Log(Entry(text: $"{t}:{i}"));
        });
        log.Dispose();

        var texts = Lines().Select(l => JsonDocument.Parse(l).RootElement.GetProperty("text").GetString()).ToList();
        Assert.Equal(threads * each, texts.Count);
        Assert.Equal(threads * each, texts.Distinct().Count());

        // Each thread's own entries keep their order.
        for (var t = 0; t < threads; t++)
        {
            var mine = texts.Where(x => x!.StartsWith(t + ":")).Select(x => int.Parse(x!.Split(':')[1])).ToList();
            Assert.Equal(Enumerable.Range(0, each), mine);
        }
    }

    // A drain that has not run yet — a stalled disk — and a quit: Dispose
    // writes what is queued on the calling thread.
    [Fact]
    public void DisposeFlushesWhatTheDrainHasNotWritten()
    {
        Action? held = null;
        var log = new BubbleLogFile(_dir, schedule: drain => held = drain);

        log.Log(Entry(text: "a"));
        log.Log(Entry(text: "b"));
        Assert.False(File.Exists(log.Path_));
        Assert.NotNull(held);

        log.Dispose();
        Assert.Equal(2, Lines().Length);

        // The late drain finds nothing left and writes nothing twice; an
        // entry after Dispose is ignored.
        held!();
        log.Log(Entry(text: "after"));
        Assert.Equal(2, Lines().Length);
    }

    // The queue is bounded: behind a stalled disk, entries past the cap are
    // dropped rather than held in memory.
    [Fact]
    public void TheQueueIsBoundedBehindAStalledDrain()
    {
        var log = new BubbleLogFile(_dir, maxQueued: 3, schedule: _ => { });

        for (var i = 0; i < 10; i++) log.Log(Entry(text: "e" + i));
        log.Dispose();

        Assert.Equal(3, Lines().Length);
    }

    // A schedule that cannot start a drain: nothing throws, and the entry
    // waits for the next chance (here, Dispose).
    [Fact]
    public void AScheduleThatThrowsIsSwallowed()
    {
        var calls = 0;
        var log = new BubbleLogFile(_dir, schedule: _ => { calls++; throw new InvalidOperationException("no pool"); });

        log.Log(Entry(text: "a"));
        log.Log(Entry(text: "b"));
        Assert.Equal(2, calls);           // the failed start did not leave it thinking a drain runs
        log.Dispose();

        Assert.Equal(2, Lines().Length);
    }

    // Half a surrogate pair is not valid UTF-16. The encoder writes it as a
    // replacement rather than refusing, so the entry still lands as one
    // parseable line — which is why Log has no catch around serialising.
    [Fact]
    public void HalfASurrogatePairStillWritesOneValidLine()
    {
        using var log = SyncLog();

        log.Log(Entry(text: "bad \uD800 half"));
        log.Log(Entry(text: "fine"));

        var lines = Lines();
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.StartsWith("bad ", doc.RootElement.GetProperty("text").GetString());
    }
}

// Which moments may be generated (owner decision on CB-202 after live use).
// Walks the whole enum, so a moment added later fails here until it has been
// deliberately classified.
public class BuddyMomentAiTests
{
    private static readonly Dictionary<BuddyMoment, bool> Expected = new()
    {
        [BuddyMoment.Responded] = true,
        [BuddyMoment.UserResponded] = true,
        [BuddyMoment.NeedsAttention] = true,
        [BuddyMoment.Thinking] = true,
        [BuddyMoment.SessionStarted] = false,
        [BuddyMoment.SessionEnded] = false,
        [BuddyMoment.LongIdle] = false,
        [BuddyMoment.Evolved] = false,
    };

    [Fact]
    public void EveryMomentIsClassified()
    {
        foreach (var moment in Enum.GetValues<BuddyMoment>())
        {
            Assert.True(Expected.ContainsKey(moment), $"{moment} has no expected answer in this test");
            Assert.Equal(Expected[moment], BuddyMoments.MayUseAi(moment));
        }
        Assert.Equal(Enum.GetValues<BuddyMoment>().Length, Expected.Count);
    }

    [Fact]
    public void AnUnclassifiedValueThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BuddyMoments.MayUseAi((BuddyMoment)99));
}
