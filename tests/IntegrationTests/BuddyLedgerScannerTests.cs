using System.Text;
using Xunit;

namespace HatchAI.Tests;

// BuddyLedgerScanner against real files in a temp tree (CB-195): discovery,
// cursors that survive a restart through a real settings.json, and the file
// shapes the pure ledger never sees — a shrink, a line longer than the read
// window, a file that cannot be opened.
//
// Tokens are credited here as `Tokens + OutputTokens` rather than through
// BuddyProgress.Credit, which is E1's and is not what these tests are about.
[Collection("Settings")]
public class BuddyLedgerScannerTests
{
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
    private static int _clock;

    private static string Row(string id, long output) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"usage\":{\"input_tokens\":900,"
        + "\"cache_read_input_tokens\":40000,\"output_tokens\":" + output + "}},\"timestamp\":\""
        + DateTimeOffset.Parse("2026-09-26T00:00:00Z").AddSeconds(Interlocked.Increment(ref _clock))
            .ToString("O") + "\"}\n";

    private static string TokenCount(long output) =>
        "{\"timestamp\":\"2026-09-26T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\","
        + "\"info\":{\"total_token_usage\":{\"output_tokens\":" + output + "}}}}\n";

    private static string NewTree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-ledgerscan-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static BuddyState Fresh() => BuddyState.Hatch("scan", Since);

    private static BuddyState Apply(BuddyState state, LedgerScanResult result)
    {
        var next = result.ApplyLedger(state);
        return next with { Tokens = next.Tokens + result.OutputTokens };
    }

    private static BuddyLedgerScanner Scanner(string home, int window = BuddyLedgerScanner.DefaultWindowBytes) =>
        new(home, new[] { Path.Combine(home, ".claude") }, new[] { Path.Combine(home, ".codex") }, window);

    [Fact]
    public void RunPersistRestartRunAgainIsUnchangedThenAppendedRowsAddExactlyTheDelta()
    {
        var home = NewTree();
        var project = Path.Combine(home, ".claude", "projects", "K--repo");
        var main = Write(Path.Combine(project, "s1.jsonl"), Row("m1", 4) + Row("m1", 193) + Row("m2", 7));
        var sub = Write(Path.Combine(project, "s1", "subagents", "agent-a.jsonl"),
            Row("m2", 7) + Row("m3", 50));
        Write(Path.Combine(home, ".codex", "sessions", "2026", "09", "26", "rollout-x.jsonl"),
            TokenCount(100) + TokenCount(160));

        var settings = NewTree();
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", settings);
        HatchAISettings.ReloadForTests();

        var first = Scanner(home).ScanAll(Fresh());
        var state = Apply(Fresh(), first);
        Assert.Equal(193 + 7 + 50 + 160, state.Tokens);
        new BuddyStore().Save(state);

        // A new process: settings re-read from disk, a scanner with no memory.
        HatchAISettings.ReloadForTests();
        var restored = new BuddyStore().Load()!;
        Assert.Equal(state.Tokens, restored.Tokens);

        var again = Scanner(home).ScanAll(restored);
        Assert.Equal(0, again.OutputTokens);
        restored = Apply(restored, again);
        Assert.Equal(state.Tokens, restored.Tokens);

        File.AppendAllText(main, Row("m4", 11));
        File.AppendAllText(sub, Row("m3", 80) + Row("m5", 2));
        var delta = Scanner(home).ScanAll(restored);
        Assert.Equal(11 + 30 + 2, delta.OutputTokens);
    }

    // The buddy must not evolve on its own voice (CB-202, and the spoken
    // summary's Bug against CB-195): anything the app's own `claude -p` ever
    // writes lands in the project folder Claude Code names after
    // BubbleVoice.WorkDir, and that folder is skipped — its main transcripts
    // and its subagents both, whatever case the name arrives in.
    //
    // The folder names are the real Windows shape (every non-alphanumeric
    // turned to '-', as found under ~/.claude/projects on the dev machine),
    // not TranscriptReader.EncodeCwd's, which keeps the drive colon. The
    // control is temp's own folder beside it, credited in full: a skip that
    // swallowed every temp project would pass the first assertion alone.
    [Fact]
    public void TheBuddyVoicesOwnProjectFolderIsNotCountedButItsNeighbourIs()
    {
        var home = NewTree();
        var projects = Path.Combine(home, ".claude", "projects");
        Write(Path.Combine(projects, "C--Users-me-AppData-Local-Temp-claudebuddy-bubble-voice", "v.jsonl"),
            Row("v1", 500));
        Write(Path.Combine(projects, "C--Users-me-AppData-Local-Temp-claudebuddy-bubble-voice", "v", "subagents",
            "agent-a.jsonl"), Row("v2", 700));
        Write(Path.Combine(projects, "-private-var-T-ClaudeBuddy-Bubble-Voice", "w.jsonl"), Row("v3", 900));
        var neighbour = Write(Path.Combine(projects, "C--Users-me-AppData-Local-Temp", "n.jsonl"), Row("n1", 40));

        var scanner = Scanner(home);
        var result = scanner.ScanAll(Fresh());

        Assert.Equal(40, result.OutputTokens);
        Assert.Equal(new[] { neighbour }, scanner.Discover().Select(f => f.Path));
        Assert.Equal(new[] { neighbour }, result.Cursors.Keys);
    }

    [Fact]
    public void AnUnchangedFileIsNotReadTwiceByTheSameScanner()
    {
        var home = NewTree();
        Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"), Row("x1", 5));
        var scanner = Scanner(home);

        var first = scanner.ScanAll(Fresh());
        var state = Apply(Fresh(), first);

        // Handing back a state that forgot the ids and the cursor's floor
        // would recount if the file were read; it is not read at all.
        var amnesiac = state with { RecentMessageIds = Array.Empty<string>() };
        Assert.Equal(0, scanner.ScanAll(amnesiac).OutputTokens);
        Assert.Empty(scanner.ScanAll(amnesiac).Cursors);
    }

    [Fact]
    public void AFileWhoseTimeMovedButNotItsLengthIsReadAgainAndEarnsNothingNew()
    {
        var home = NewTree();
        var path = Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"), Row("t1", 5));
        var scanner = Scanner(home);
        var state = Apply(Fresh(), scanner.ScanAll(Fresh()));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var again = scanner.ScanAll(state);

        Assert.Equal(0, again.OutputTokens);
        Assert.Empty(again.Cursors);
    }

    [Fact]
    public void ADefaultScannerTouchesNothingUntilAsked()
    {
        // Constructing one reads no settings and no disk; the real home is
        // only resolved into a path.
        var scanner = new BuddyLedgerScanner();

        Assert.Equal(0, scanner.ScanPaths(Fresh(), Array.Empty<string>()).OutputTokens);
    }

    [Fact]
    public void AScannerWhoseResultWasNotAppliedReadsTheFileAgain()
    {
        var home = NewTree();
        Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"), Row("y1", 5));
        var scanner = Scanner(home);

        Assert.Equal(5, scanner.ScanAll(Fresh()).OutputTokens);
        Assert.Equal(5, scanner.ScanAll(Fresh()).OutputTokens);
    }

    private static string RowAt(string id, long output, DateTimeOffset at) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"usage\":{\"output_tokens\":" + output
        + "}},\"timestamp\":\"" + at.ToString("O") + "\"}\n";

    // A file written entirely before the hatch holds nothing countable, so it
    // is skipped without being read — and, since QA on CB-195, without being
    // given a cursor either: every such file used to be written into
    // settings.json at ~350 bytes apiece. When it is resumed after the hatch
    // it is read from the top, and the countingSince floor is what keeps the
    // old rows out.
    [Fact]
    public void AFileWrittenBeforeTheHatchIsSkippedWithoutACursorButAppendsAfterCount()
    {
        var home = NewTree();
        var hatch = DateTimeOffset.UtcNow.AddMinutes(-5);
        var path = Write(Path.Combine(home, ".claude", "projects", "p", "old.jsonl"),
            RowAt("o1", 999, hatch.AddDays(-3)));
        File.SetLastWriteTimeUtc(path, hatch.UtcDateTime.AddDays(-3));
        var state = BuddyState.Hatch("h", hatch);

        var scanner = Scanner(home);
        var first = scanner.ScanAll(state);
        Assert.Equal(0, first.OutputTokens);
        Assert.Empty(first.Cursors);
        Assert.Empty(first.Removed);

        // And the same again on the next walk: skipped on its time alone.
        state = Apply(state, first);
        Assert.Empty(scanner.ScanAll(state).Cursors);

        File.AppendAllText(path, RowAt("o2", 6, hatch.AddMinutes(1)));

        var resumed = Scanner(home).ScanAll(state);
        Assert.Equal(6, resumed.OutputTokens);
        Assert.Equal(new FileInfo(path).Length, resumed.Cursors[path].Offset);
    }

    // The case QA measured: a machine with years of transcripts. None of them
    // may reach settings.json; the two written since the hatch do.
    [Fact]
    public void ManyFilesFromBeforeTheHatchLeaveNothingInSettings()
    {
        var home = NewTree();
        var hatch = DateTimeOffset.UtcNow.AddMinutes(-5);
        var project = Path.Combine(home, ".claude", "projects", "p");
        for (var i = 0; i < 400; i++)
        {
            var old = Write(Path.Combine(project, $"old-{i}.jsonl"), RowAt("old" + i, 50, hatch.AddDays(-10)));
            File.SetLastWriteTimeUtc(old, hatch.UtcDateTime.AddDays(-10));
        }

        var live1 = Write(Path.Combine(project, "live-1.jsonl"), RowAt("n1", 7, hatch.AddMinutes(1)));
        var live2 = Write(Path.Combine(project, "live-1", "subagents", "agent-a.jsonl"), RowAt("n2", 8, hatch.AddMinutes(2)));

        var settings = NewTree();
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", settings);
        HatchAISettings.ReloadForTests();

        var state = BuddyState.Hatch("many", hatch);
        var result = Scanner(home).ScanAll(state);
        Assert.Equal(15, result.OutputTokens);
        Assert.Equal(new[] { live1, live2 }.OrderBy(p => p), result.Cursors.Keys.OrderBy(p => p));

        new BuddyStore().Save(Apply(state, result));
        var files = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(settings, "settings.json")))!
            ["buddy"]!["ledger"]!["files"]!.AsObject();
        Assert.Equal(2, files.Count);
    }

    // A settings.json written by the build before that fix carries a cursor
    // for every old file. They are dropped on the next walk, and dropping one
    // costs nothing: the file is still skipped unread, and if it is ever
    // resumed the floor keeps its old rows out exactly as above.
    [Fact]
    public void CursorsAlreadyStoredForFilesFromBeforeTheHatchArePruned()
    {
        var home = NewTree();
        var hatch = DateTimeOffset.UtcNow.AddMinutes(-5);
        var old = Write(Path.Combine(home, ".claude", "projects", "p", "old.jsonl"), RowAt("o1", 9, hatch.AddDays(-1)));
        File.SetLastWriteTimeUtc(old, hatch.UtcDateTime.AddDays(-1));
        var codex = Write(Path.Combine(home, ".codex", "sessions", "rollout-old.jsonl"), TokenCount(5));
        File.SetLastWriteTimeUtc(codex, hatch.UtcDateTime.AddDays(-1));
        var state = BuddyState.Hatch("prune", hatch) with
        {
            Cursors = new Dictionary<string, LedgerCursor>
            {
                [old] = new(new FileInfo(old).Length, null, null, 0, 0),
                [codex] = new(new FileInfo(codex).Length, null, null, 0, 5),
            }
        };

        var result = Scanner(home).ScanAll(state);

        // Claude Code only: a Codex rollout's cursor carries the running
        // total its next delta is taken from, and stays.
        Assert.Equal(new[] { old }, result.Removed);
        Assert.Equal(0, result.OutputTokens);
        Assert.Equal(new[] { codex }, result.ApplyLedger(state).Cursors.Keys);

        // ScanPaths prunes the same way when it is the one to meet the file.
        Assert.Equal(new[] { old }, Scanner(home).ScanPaths(state, new[] { old }).Removed);
    }

    // A path with a NUL in it makes FileInfo throw ArgumentException, which
    // used to escape the per-file catch and abort the whole round — every
    // other transcript included, every ten seconds, for as long as the
    // session that reported it stayed on screen.
    [Fact]
    public void AnUnusablePathCostsOnlyItself()
    {
        var home = NewTree();
        var good = Write(Path.Combine(home, "a", "live.jsonl"), Row("nul1", 8));

        var result = Scanner(home).ScanPaths(Fresh(), new[] { Path.Combine(home, "bad\0name.jsonl"), good });

        Assert.Equal(8, result.OutputTokens);
        Assert.Equal(new[] { good }, result.Cursors.Keys);
    }

    // The crash story under the save throttle (QA on CB-195): the real
    // controller, the real store over a real settings.json, the real scanner
    // over real files, and a "crash" — the controller simply abandoned, as a
    // killed process would leave it — at the two moments that matter. Each
    // time the next run must end on exactly what the files hold: nothing lost
    // from the unsaved window, nothing from before it counted twice.
    [Fact]
    public async Task ACrashInsideTheSaveWindowReReadsRatherThanLosesOrDoubleCounts()
    {
        var home = NewTree();
        var hatch = DateTimeOffset.UtcNow.AddHours(-1);
        var path = Write(Path.Combine(home, ".claude", "projects", "p", "live.jsonl"),
            RowAt("c1", 100, hatch.AddMinutes(1)));

        var settings = NewTree();
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", settings);
        HatchAISettings.ReloadForTests();
        new BuddyStore().Save(BuddyState.Hatch("crash", hatch));

        var now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        BuddyController Run() => new(
            new QuietView(), new BuddyStore(), new BuddyLedgerSource(Scanner(home)),
            () => true, () => false, () => now);
        var live = new[]
        {
            new SessionSnapshot("s", "generating", now, SessionSource.ClaudeCode, "s", home, path, "")
        };

        // Run 1: credited in memory, not yet due to be written — then gone.
        var run1 = Run();
        run1.Reapply();
        await run1.LedgerRoundAsync();
        Assert.Equal(100, run1.State!.Tokens);
        Assert.Equal(0, new BuddyStore().Load()!.Tokens);

        // Run 2 starts from the cursorless state on disk, reads the same
        // bytes again, and this time lives long enough to save.
        now = now.AddMinutes(1);
        var run2 = Run();
        run2.Reapply();
        run2.OnSnapshots(live);
        await run2.LedgerRoundAsync();
        Assert.Equal(100, run2.State!.Tokens);
        now = now.AddSeconds(30);
        run2.Pump();
        Assert.Equal(100, new BuddyStore().Load()!.Tokens);

        // More output, credited but not written, and another crash.
        File.AppendAllText(path, RowAt("c2", 50, hatch.AddMinutes(2)));
        now = now.AddSeconds(10);
        await run2.LedgerRoundAsync();
        Assert.Equal(150, run2.State!.Tokens);
        Assert.Equal(100, new BuddyStore().Load()!.Tokens);

        // Run 3: the saved cursor stands past c1, so only c2 is read again.
        now = now.AddMinutes(1);
        var run3 = Run();
        run3.Reapply();
        await run3.LedgerRoundAsync();
        Assert.Equal(150, run3.State!.Tokens);
        Assert.Equal(150, run3.State.LifetimeTokens);

        // A clean quit writes it.
        run3.Dispose();
        Assert.Equal(150, new BuddyStore().Load()!.Tokens);
    }

    [Fact]
    public void TokensPastLongMaxValueAcrossFilesSaturate()
    {
        var home = NewTree();
        var project = Path.Combine(home, ".claude", "projects", "p");
        Write(Path.Combine(project, "a.jsonl"), Row("max-a", long.MaxValue));
        Write(Path.Combine(project, "b.jsonl"), Row("max-b", long.MaxValue));
        Write(Path.Combine(project, "c.jsonl"), Row("max-c1", long.MaxValue) + Row("max-c2", long.MaxValue));

        // A window small enough that c is read one row per go, so the sum
        // inside one file saturates as well as the sum across files.
        var both = Scanner(home, window: 300).ScanAll(Fresh());
        var alone = Scanner(home, window: 300).ScanPaths(Fresh(), new[] { Path.Combine(project, "c.jsonl") });

        Assert.Equal(long.MaxValue, both.OutputTokens);
        Assert.Equal(long.MaxValue, alone.OutputTokens);
    }

    [Fact]
    public void AFileThatShrankIsReReadFromTheTopWithoutRecounting()
    {
        var home = NewTree();
        var path = Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"),
            Row("s1", 10) + Row("s2", 20) + Row("s3", 30));
        var state = Apply(Fresh(), Scanner(home).ScanAll(Fresh()));
        Assert.Equal(60, state.Tokens);

        // Rewritten shorter, keeping the last row and adding a new one. The
        // recent ids are cleared to prove the timestamp floor alone holds.
        var lines = File.ReadAllLines(path);
        File.WriteAllText(path, lines[2] + "\n" + Row("s4", 4));
        state = state with { RecentMessageIds = Array.Empty<string>() };
        Assert.True(new FileInfo(path).Length < state.Cursors[path].Offset);

        var after = Scanner(home).ScanAll(state);
        Assert.Equal(4, after.OutputTokens);
    }

    [Fact]
    public void ALongFileIsReadInSeveralWindows()
    {
        var home = NewTree();
        var text = string.Concat(Enumerable.Range(0, 40).Select(i => Row("w" + i, 1)));
        Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"), text);

        var result = Scanner(home, window: 300).ScanAll(Fresh());

        Assert.Equal(40, result.OutputTokens);
    }

    [Fact]
    public void ALineLongerThanTheWindowIsSkippedOnceItEnds()
    {
        var home = NewTree();
        var big = "{\"type\":\"user\",\"pad\":\"" + new string('x', 2000) + "\"}\n";
        var path = Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"),
            Row("b1", 3) + big + Row("b2", 4));

        var result = Scanner(home, window: 500).ScanAll(Fresh());

        Assert.Equal(7, result.OutputTokens);
        Assert.Equal(new FileInfo(path).Length, result.Cursors[path].Offset);
    }

    [Fact]
    public void ALineLongerThanTheWindowThatHasNotEndedIsLeftForLater()
    {
        var home = NewTree();
        var head = Row("c1", 3);
        var path = Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"),
            head + "{\"type\":\"user\",\"pad\":\"" + new string('x', 2000));

        var result = Scanner(home, window: 500).ScanAll(Fresh());

        Assert.Equal(3, result.OutputTokens);
        Assert.Equal(Encoding.UTF8.GetByteCount(head), result.Cursors[path].Offset);
    }

    [Fact]
    public void AFileNotEndingInANewlineStopsWithoutLooping()
    {
        var home = NewTree();
        var path = Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"), Row("d1", 3) + "{\"type\"");

        var result = Scanner(home).ScanAll(Fresh());

        Assert.Equal(3, result.OutputTokens);
        Assert.True(result.Cursors[path].Offset < new FileInfo(path).Length);
    }

    [Fact]
    public void AnEmptyFileEarnsNothingAndGetsNoCursor()
    {
        var home = NewTree();
        Write(Path.Combine(home, ".claude", "projects", "p", "empty.jsonl"), "");

        var result = Scanner(home).ScanAll(Fresh());

        Assert.Equal(0, result.OutputTokens);
        Assert.Empty(result.Cursors);
    }

    [Fact]
    public void ACursorWhoseFileIsGoneIsReportedRemovedAndOneStillPresentIsNot()
    {
        var home = NewTree();
        var kept = Write(Path.Combine(home, ".claude", "projects", "p", "kept.jsonl"), Row("k", 1));
        var gone = Path.Combine(home, ".claude", "projects", "p", "gone.jsonl");
        var elsewhere = Write(Path.Combine(home, "live", "outside.jsonl"), Row("e", 1));
        var state = Fresh() with
        {
            Cursors = new Dictionary<string, LedgerCursor>
            {
                [gone] = new(10, null, null, 0, 0),
                [elsewhere] = new(0, null, null, 0, 0),
            }
        };

        var result = Scanner(home).ScanAll(state);

        Assert.Equal(new[] { gone }, result.Removed);
        Assert.True(result.Cursors.ContainsKey(kept));
        Assert.DoesNotContain(gone, result.ApplyLedger(state).Cursors.Keys);
        Assert.Contains(elsewhere, result.ApplyLedger(state).Cursors.Keys);
    }

    [Fact]
    public void ScanPathsReadsOnlyTheNamedTranscriptsOnceEachAndSkipsGrok()
    {
        var home = NewTree();
        var claude = Write(Path.Combine(home, "a", "live.jsonl"), Row("l1", 8));
        var codex = Write(Path.Combine(home, "b", "rollout-1.jsonl"), TokenCount(12));
        var grok = Write(Path.Combine(home, "c", "updates.jsonl"), Row("g1", 1000));
        Write(Path.Combine(home, ".claude", "projects", "p", "unnamed.jsonl"), Row("u1", 500));

        var result = Scanner(home).ScanPaths(Fresh(), new[] { claude, claude, codex, grok, null, " " });

        Assert.Equal(8 + 12, result.OutputTokens);
        Assert.Equal(new[] { claude, codex }.OrderBy(p => p), result.Cursors.Keys.OrderBy(p => p));
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void AMissingNamedTranscriptIsSkipped()
    {
        var home = NewTree();

        var result = Scanner(home).ScanPaths(Fresh(), new[] { Path.Combine(home, "nope.jsonl") });

        Assert.Equal(0, result.OutputTokens);
        Assert.Empty(result.Cursors);
    }

    [Fact]
    public void TheSameResponseInTwoFilesOfOneScanIsCreditedOnce()
    {
        var home = NewTree();
        var project = Path.Combine(home, ".claude", "projects", "p");
        Write(Path.Combine(project, "a.jsonl"), Row("dup", 40));
        Write(Path.Combine(project, "a", "subagents", "agent-1.jsonl"), Row("dup", 40));

        var result = Scanner(home).ScanAll(Fresh());

        Assert.Equal(40, result.OutputTokens);
        Assert.Equal(new[] { "dup" }, result.CreditedMessageIds);
    }

    [Fact]
    public void AMissingTreeDiscoversNothing()
    {
        var home = NewTree();

        Assert.Empty(Scanner(home).Discover());
    }

    [Fact]
    public void TheDefaultRootsComeFromHomeAndSettings()
    {
        var settings = NewTree();
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", settings);
        HatchAISettings.ReloadForTests();

        var home = NewTree();
        var claude = Write(Path.Combine(home, ".claude", "projects", "p", "a.jsonl"), Row("r1", 1));
        var codex = Write(Path.Combine(home, ".codex", "sessions", "rollout-z.jsonl"), TokenCount(1));

        var found = new BuddyLedgerScanner(home).Discover().ToList();

        Assert.Contains((claude, LedgerFormat.ClaudeCode), found);
        Assert.Contains((codex, LedgerFormat.Codex), found);
    }

    [Fact]
    public void AFileThatCannotBeOpenedIsSkippedAndReadNextTime()
    {
        var home = NewTree();
        var path = Write(Path.Combine(home, ".claude", "projects", "p", "locked.jsonl"), Row("z1", 9));
        var scanner = Scanner(home);

        LedgerScanResult locked;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            locked = scanner.ScanAll(Fresh());
        }

        // Only Windows enforces FileShare.None against another open; on macOS
        // the read goes through, which is equally not a crash.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(0, locked.OutputTokens);
            Assert.Empty(locked.Cursors);
        }

        Assert.Equal(9, Apply(Fresh(), locked).Tokens + scanner.ScanAll(Apply(Fresh(), locked)).OutputTokens);
    }
}
