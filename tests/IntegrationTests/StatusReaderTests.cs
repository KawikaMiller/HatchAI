using System.Text;
using Xunit;

namespace HatchAI.Tests;

// StatusReader against a scratch status folder holding files in the exact
// shapes the two hooks write.
//
// The two fixtures below are copied from real files rather than written from
// memory: the bash one is the line ClaudeBuddyHook.sh's printf produces (the
// same fixture Claude Buddy's InternalSessions scan tests use), and the
// PowerShell one is a status file read off a real Windows machine running
// Claude Buddy's installed hook, with the user's paths and title replaced.
// Note the differences, which are the point: the PowerShell hook writes
// term_pid and no tty or tmux fields, puts the keys in its own order, and
// escapes backslashes.
[Collection("InternalSessions")]
public class StatusReaderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hatchai-status-" + Guid.NewGuid().ToString("N"));

    public StatusReaderTests()
    {
        Directory.CreateDirectory(_dir);
        InternalSessions.Clear();
    }

    public void Dispose()
    {
        InternalSessions.Clear();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Bash(string state = "generating", int pid = 4242, string cwd = "/Users/someone/project",
        string cli = "claude", string transcript = "") =>
        "{\"state\":\"" + state + "\",\"cli\":\"" + cli + "\",\"cwd\":\"" + cwd + "\","
        + "\"title\":\"\",\"color\":\"cyan\",\"term_program\":\"tmux\",\"term_id\":\"\","
        + "\"tty\":\"ttys001\",\"tmux_socket\":\"/tmp/tmux-501/default\",\"tmux_pane\":\"%1\","
        + "\"tmux_bin\":\"/opt/homebrew/bin/tmux\",\"session_pid\":" + pid
        + ",\"transcript_path\":\"" + transcript + "\"}";

    private static string Ps1(string state = "generating", int pid = 78412, string cli = "claude",
        string cwd = @"K:\\work\\project", string termProgram = "WindowsTerminal", int termPid = 19148,
        string transcript = @"C:\\Users\\someone\\.claude\\projects\\K--work-project\\s.jsonl") =>
        "{\"term_program\":\"" + termProgram + "\",\"cwd\":\"" + cwd + "\",\"term_pid\":" + termPid
        + ",\"cli\":\"" + cli + "\",\"color\":\"\",\"state\":\"" + state + "\",\"session_pid\":" + pid
        + ",\"title\":\"A real session title\",\"transcript_path\":\"" + transcript + "\",\"term_id\":\"\"}";

    private string Put(string id, string json, DateTime? written = null)
    {
        var path = Path.Combine(_dir, id + ".txt");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, written ?? Now.AddSeconds(-1));
        return path;
    }

    private List<ScanEntry> Scan(Func<int, bool>? isRunning = null, Func<string, bool>? transcriptExists = null) =>
        StatusReader.Scan(_dir, Now, isRunning ?? (_ => true), transcriptExists ?? (_ => true));

    private IEnumerable<string> Ids(Func<int, bool>? isRunning = null, Func<string, bool>? transcriptExists = null) =>
        Scan(isRunning, transcriptExists).Select(e => e.SessionId).OrderBy(x => x, StringComparer.Ordinal);

    // ---- the two hook shapes --------------------------------------------------

    [Fact]
    public void ReadsTheBashHooksShape()
    {
        Put("bash", Bash(transcript: "/Users/someone/.claude/projects/p/bash.jsonl"));

        var entry = Assert.Single(Scan());

        Assert.Equal("bash", entry.SessionId);
        Assert.Equal("generating", entry.Status.State);
        Assert.Equal(SessionSource.ClaudeCode, entry.Status.Source);
        Assert.Equal("/Users/someone/project", entry.Status.Cwd);
        Assert.Equal(4242, entry.Status.SessionPid);
        Assert.Equal("%1", entry.Status.TmuxPane);
        Assert.Equal("ttys001", entry.Status.Tty);
        Assert.Equal("/Users/someone/.claude/projects/p/bash.jsonl", entry.Status.TranscriptPath);
    }

    [Fact]
    public void ReadsThePowerShellHooksShape()
    {
        Put("ps1", Ps1());

        var entry = Assert.Single(Scan());

        Assert.Equal(78412, entry.Status.SessionPid);
        Assert.Equal(19148, entry.Status.TermPid);
        Assert.Equal("WindowsTerminal", entry.Status.TermProgram);
        Assert.Equal(@"K:\work\project", entry.Status.Cwd);
        Assert.Equal(@"C:\Users\someone\.claude\projects\K--work-project\s.jsonl", entry.Status.TranscriptPath);
        Assert.Equal("A real session title", entry.Status.Title);
        Assert.Equal(Now.AddSeconds(-1), entry.Written);
    }

    // The buddy needs the CLI: the ledger counts Claude Code and Codex only,
    // and the AI prompt reader parses by source. Absent means Claude Code.
    [Theory]
    [InlineData("claude", SessionSource.ClaudeCode)]
    [InlineData("", SessionSource.ClaudeCode)]
    [InlineData("codex", SessionSource.Codex)]
    [InlineData("CODEX", SessionSource.Codex)]
    [InlineData("grok", SessionSource.Grok)]
    [InlineData("something-new", SessionSource.ClaudeCode)]
    public void TheCliFieldDecidesTheSource(string cli, SessionSource expected)
    {
        Put("s", Ps1(cli: cli));

        Assert.Equal(expected, Assert.Single(Scan()).Status.Source);
    }

    [Fact]
    public void AFileWithNoCliFieldAtAllIsClaudeCode()
    {
        Put("old", "{\"state\":\"idle\",\"cwd\":\"/p\",\"session_pid\":10,\"term_program\":\"x\"}");

        Assert.Equal(SessionSource.ClaudeCode, Assert.Single(Scan()).Status.Source);
    }

    // A quoted pid costs nothing rather than the whole file.
    [Fact]
    public void ANumberWrittenAsAStringIsStillRead()
    {
        Put("q", "{\"state\":\"idle\",\"session_pid\":\"77\",\"term_program\":\"x\"}");

        Assert.Equal(77, Assert.Single(Scan()).Status.SessionPid);
    }

    // ---- what is not a session file -----------------------------------------------

    [Fact]
    public void OnlyTxtFilesAreRead()
    {
        Put("real", Ps1());
        File.WriteAllText(Path.Combine(_dir, ".auto-color"), "1");
        File.WriteAllText(Path.Combine(_dir, "settings-errors.log"), "{\"state\":\"idle\"}");
        File.WriteAllText(Path.Combine(_dir, "other.json"), Ps1());

        Assert.Equal(new[] { "real" }, Ids());
    }

    [Fact]
    public void AMissingFolderIsNoSessions()
    {
        Assert.Empty(StatusReader.Scan(Path.Combine(_dir, "never-created"), Now, _ => true));
    }

    // A read that lands mid-write (the hooks truncate then write) is skipped
    // this tick and read again on the next; nothing about it is remembered.
    [Theory]
    [InlineData("")]
    [InlineData("{\"state\":\"gener")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("not json at all")]
    public void ATornOrForeignFileIsSkippedAndTheRestAreStillRead(string content)
    {
        Put("torn", content);
        Put("good", Ps1());

        Assert.Equal(new[] { "good" }, Ids());
    }

    // No hook writes a BOM (the PowerShell hook writes UTF-8 without one on
    // purpose), but a file that has one is still a status file: the JSON
    // reader skips a UTF-8 BOM at the start of a stream.
    [Fact]
    public void AFileWithABomIsStillRead()
    {
        var path = Path.Combine(_dir, "bom.txt");
        File.WriteAllText(path, Ps1(), new UTF8Encoding(true));
        File.SetLastWriteTimeUtc(path, Now);

        Assert.Equal("bom", Assert.Single(Scan()).SessionId);
    }

    // ---- the read never gets in a hook's way ------------------------------------

    // The reason ReadFile opens with FileShare.Delete: on Windows, without it,
    // the hook's Remove-Item on SessionEnd fails silently while this app holds
    // the file, and the session's file outlives the session.
    [Fact]
    public void AHookCanDeleteAFileWhileItIsBeingRead()
    {
        var path = Put("ending", Ps1());

        using (StatusReader.OpenShared(path))
        {
            File.Delete(path);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AHookCanRewriteAFileWhileItIsBeingRead()
    {
        var path = Put("busy", Ps1(state: "idle"));

        using (StatusReader.OpenShared(path))
        {
            using var hook = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            hook.SetLength(0);
        }
    }

    // **The reader never writes, creates or deletes anything in the status
    // folder.** Every file — names, bytes and timestamps — is exactly as it
    // was after scanning, including ones the rules drop (dead, expired,
    // superseded, torn), which is where Claude Buddy would have swept.
    [Fact]
    public void ScanningNeverChangesTheFolder()
    {
        Put("live", Ps1(pid: 1));
        Put("dead", Ps1(pid: 2));
        Put("expired", Ps1(pid: 0, state: "idle"), Now.AddHours(-3));
        Put("superseded-old", Ps1(pid: 3), Now.AddMinutes(-2));
        Put("superseded-new", Ps1(pid: 3), Now.AddMinutes(-1));
        Put("torn", "{\"sta");
        File.WriteAllText(Path.Combine(_dir, ".auto-color"), "1");

        var before = Fingerprint();
        for (var i = 0; i < 3; i++) StatusReader.Scan(_dir, Now, pid => pid != 2);
        var after = Fingerprint();

        Assert.Equal(before, after);
    }

    private List<string> Fingerprint() =>
        new DirectoryInfo(_dir).GetFileSystemInfos("*", SearchOption.AllDirectories)
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .Select(f => f is FileInfo file
                ? $"{file.Name}|{file.LastWriteTimeUtc:O}|{Convert.ToBase64String(File.ReadAllBytes(file.FullName))}"
                : $"{f.Name}/")
            .ToList();

    // ---- plumbing that wears a session's clothes -----------------------------

    // The AI-bubble call's own folder, and — critically — Claude Buddy's
    // spoken-summary `claude -p`, which runs from the same folder with hooks
    // enabled. HatchAI cannot see Claude Buddy's pids, so the folder is the
    // only thing that identifies it.
    [Theory]
    [InlineData(@"C:\\Users\\someone\\AppData\\Local\\Temp\\claudebuddy-bubble-voice")]
    [InlineData(@"C:\\Users\\someone\\AppData\\Local\\Temp\\claudebuddy-bubble-voice\\")]
    [InlineData("/var/folders/xy/T/claudebuddy-bubble-voice")]
    // Claude Buddy's leftover remote-control relays.
    [InlineData("/Users/someone/.claude-buddy/claude-buddy-rc-studio")]
    [InlineData(@"C:\\Users\\someone\\CLAUDE-BUDDY-RC-x")]
    // Claude Buddy's macOS Grok usage probe.
    [InlineData("/var/folders/xy/T/claude-buddy-grok-refresh")]
    [InlineData("/var/folders/xy/T/claude-buddy-grok-refresh/")]
    public void SessionsInSomeAppsOwnFoldersAreDropped(string cwd)
    {
        Put("plumbing", Ps1(cwd: cwd));
        Put("user", Ps1(pid: 5));

        Assert.Equal(new[] { "user" }, Ids());
    }

    // The near misses stay: only the exact leaf is plumbing.
    [Theory]
    [InlineData("/Users/someone/claudebuddy-bubble-voice-notes")]
    [InlineData("/Users/someone/claude-buddy-grok-refresh-fork")]
    [InlineData("/Users/someone/claude-buddy-rc")]
    [InlineData("/Users/someone/my-claude-buddy-rc-thing")]
    public void FoldersThatMerelyLookSimilarAreKept(string cwd)
    {
        Put("real", Ps1(cwd: cwd));

        Assert.Equal(new[] { "real" }, Ids());
    }

    [Fact]
    public void ThisAppsOwnPidIsDropped()
    {
        Put("mine", Ps1(pid: 999));
        Put("theirs", Ps1(pid: 1000));
        InternalSessions.Remember(999);

        Assert.Equal(new[] { "theirs" }, Ids());
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/a/b/", "b")]
    [InlineData(@"C:\a\b", "b")]
    [InlineData("leaf", "leaf")]
    [InlineData("///", "")]
    public void LeafOfIsTheLastSegmentOnEitherSeparator(string? path, string expected)
    {
        Assert.Equal(expected, StatusReader.LeafOf(path));
    }

    [Fact]
    public void AnEmptyCwdIsNotARelay()
    {
        Assert.False(StatusReader.LooksLikeALeftoverRelay(null));
        Assert.False(StatusReader.LooksLikeALeftoverRelay(""));
    }

    // ---- liveness ------------------------------------------------------------

    // Ctrl+C fires no SessionEnd, so the file lingers; its dead pid is what
    // removes it. Waiting is not exempt from that.
    [Theory]
    [InlineData("generating")]
    [InlineData("waiting")]
    [InlineData("idle")]
    public void ASessionWhoseProcessHasExitedIsDropped(string state)
    {
        Put("dead", Ps1(pid: 111, state: state));
        Put("alive", Ps1(pid: 222));

        Assert.Equal(new[] { "alive" }, Ids(isRunning: pid => pid == 222));
    }

    // Rule B (owner decision, plan §2.4): a live process's file never expires
    // from idling alone, however long it has been quiet. This is what lets the
    // fifteen-minute long-idle moment fire at all, and what stops a fake
    // "ended" five minutes into a pause.
    [Theory]
    [InlineData("idle")]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void AFileWithALiveProcessNeverExpires(string state)
    {
        Put("quiet", Ps1(pid: 50, state: state), Now.AddHours(-6));

        Assert.Equal(new[] { "quiet" }, Ids());
    }

    // A file naming no process — an older hook, a subagent leftover — expires
    // after five quiet minutes, as Claude Buddy's default orb lifetime would,
    // unless it is waiting (its mtime freezes exactly while it matters).
    [Fact]
    public void APidlessFileExpiresAfterFiveQuietMinutesUnlessWaiting()
    {
        Put("fresh", Ps1(pid: 0, state: "idle"), Now - StatusReader.PidlessLifetime);
        Put("stale", Ps1(pid: 0, state: "idle"), Now - StatusReader.PidlessLifetime - TimeSpan.FromSeconds(1));
        Put("stale-generating", Ps1(pid: 0, state: "generating"), Now.AddMinutes(-30));
        Put("waiting", Ps1(pid: 0, state: "waiting"), Now.AddHours(-2));

        Assert.Equal(new[] { "fresh", "waiting" }, Ids());
    }

    // A pid of 0 is never asked about: ProcessLiveness answers "alive" for it,
    // and expiry is what covers it instead.
    [Fact]
    public void APidlessFileIsNeverAskedAboutItsProcess()
    {
        Put("pidless", Ps1(pid: 0), Now);
        var asked = new List<int>();

        StatusReader.Scan(_dir, Now, pid => { asked.Add(pid); return false; });

        Assert.Empty(asked);
    }

    // ---- superseded ----------------------------------------------------------

    // One process, several session ids (a /clear, a resume): only the newest
    // file is the live session, and the stale ones — frozen at whatever they
    // last said — go now rather than lingering as a second "generating".
    [Fact]
    public void OnlyTheNewestFileForAProcessIsKept()
    {
        Put("first", Ps1(pid: 7, state: "generating"), Now.AddMinutes(-10));
        Put("second", Ps1(pid: 7, state: "idle"), Now.AddMinutes(-5));
        Put("third", Ps1(pid: 7, state: "idle"), Now.AddMinutes(-1));

        Assert.Equal(new[] { "third" }, Ids());
    }

    // Two CLIs never share a real process: a Codex file carrying a Claude
    // Code session's pid (codex exec run as a Bash tool) must not supersede
    // the Claude session doing the work.
    [Fact]
    public void AFileFromADifferentCliWithTheSamePidDoesNotSupersede()
    {
        Put("claude", Ps1(pid: 7), Now.AddMinutes(-5));
        Put("codex", Ps1(pid: 7, cli: "codex"), Now.AddMinutes(-1));

        Assert.Equal(new[] { "claude", "codex" }, Ids());
    }

    // An mtime tie is broken by id, so the answer does not depend on the order
    // the directory happened to enumerate in.
    [Fact]
    public void AnMtimeTieIsBrokenByIdNotByEnumerationOrder()
    {
        Put("aaa", Ps1(pid: 7), Now.AddMinutes(-1));
        Put("bbb", Ps1(pid: 7), Now.AddMinutes(-1));

        Assert.Equal(new[] { "bbb" }, Ids());
    }

    [Fact]
    public void PidlessFilesAreNeverGroupedTogether()
    {
        Put("one", Ps1(pid: 0), Now);
        Put("two", Ps1(pid: 0), Now);

        Assert.Equal(new[] { "one", "two" }, Ids());
    }

    // What Claude Buddy's isLiveJob seam decides; HatchAI always passes
    // "not a job", but the rule itself keeps the exemption.
    [Fact]
    public void SupersededSparesAnIdTheJobListingSaysIsLive()
    {
        var older = new ScanEntry("older", new StatusFile { SessionPid = 7 }, Now.AddMinutes(-2));
        var newer = new ScanEntry("newer", new StatusFile { SessionPid = 7 }, Now);

        Assert.Empty(StatusReader.Superseded(new() { older, newer }, id => id == "older"));
        Assert.Equal(new[] { "older" }, StatusReader.Superseded(new() { older, newer }, _ => false));
    }

    // ---- reachability ----------------------------------------------------------

    // A Codex or Grok process with no terminal at all is a headless or
    // bridged invocation. Claude Code is exempt: with no job listing, a
    // background job cannot be ruled out, and it has no terminal by nature.
    [Theory]
    [InlineData("codex", false)]
    [InlineData("grok", false)]
    [InlineData("claude", true)]
    public void ANoTerminalProcessIsDroppedUnlessItIsClaudeCode(string cli, bool kept)
    {
        Put("headless", "{\"state\":\"idle\",\"cli\":\"" + cli + "\",\"session_pid\":42}");

        Assert.Equal(kept, Ids().Contains("headless"));
    }

    // Any one terminal field is enough to be kept.
    [Theory]
    [InlineData("\"tty\":\"ttys1\"")]
    [InlineData("\"term_program\":\"iTerm\"")]
    [InlineData("\"tmux_pane\":\"%3\"")]
    [InlineData("\"term_pid\":9")]
    public void ACodexProcessWithAnyTerminalIsKept(string field)
    {
        Put("cx", "{\"state\":\"idle\",\"cli\":\"codex\",\"session_pid\":42," + field + "}");

        Assert.Equal(new[] { "cx" }, Ids());
    }

    // Neither Codex nor Grok has a background job a pid-less file could be;
    // it is a session that ended without clearing up.
    [Theory]
    [InlineData("codex")]
    [InlineData("grok")]
    public void APidlessCodexOrGrokFileIsDropped(string cli)
    {
        Put("gone", Ps1(pid: 0, cli: cli), Now);

        Assert.Empty(Ids());
    }

    // No terminal and a transcript that is not there: nothing to show and
    // nowhere to click — an unprompted background worker. An empty path is
    // not knowing, which keeps it; a terminal keeps it however thin its chat.
    [Fact]
    public void NoTerminalAndAMissingTranscriptIsNothingToShow()
    {
        Put("nothing", "{\"state\":\"idle\",\"session_pid\":42,\"tty\":\"ttys9\",\"transcript_path\":\"/gone.jsonl\"}");
        Put("no-path", "{\"state\":\"idle\",\"session_pid\":43,\"tty\":\"ttys9\",\"transcript_path\":\"\"}");
        Put("terminal", "{\"state\":\"idle\",\"session_pid\":44,\"term_program\":\"x\",\"transcript_path\":\"/gone.jsonl\"}");
        Put("term-id", "{\"state\":\"idle\",\"session_pid\":45,\"term_id\":\"w1\",\"transcript_path\":\"/gone.jsonl\"}");

        Assert.Equal(new[] { "no-path", "term-id", "terminal" }, Ids(transcriptExists: _ => false));
        Assert.Equal(new[] { "no-path", "nothing", "term-id", "terminal" }, Ids(transcriptExists: _ => true));
    }

    // ---- the published snapshots ---------------------------------------------

    // A session ends by its file disappearing (the hook deletes it rather
    // than writing "ended"), and the buddy turns that disappearance into
    // SessionEnded. Driven end to end: real files, the real reader, the real
    // tracker, the real classifier.
    [Fact]
    public void ADeletedFileIsSeenAsTheSessionEnding()
    {
        var reader = new StatusReader(_dir, _ => true, _ => true, () => Now);
        var path = Put("s1", Ps1(state: "idle"));
        Put("s2", Ps1(pid: 2, state: "idle"));

        var before = reader.Publish(StatusReader.Scan(_dir, Now, _ => true), Now);
        File.Delete(path);
        var after = reader.Publish(StatusReader.Scan(_dir, Now.AddSeconds(2), _ => true), Now.AddSeconds(2));

        var moments = BuddyMoments.Classify(before, after, new DateTimeOffset(Now.AddSeconds(2)), new DateTimeOffset(Now));
        Assert.Equal(new BuddyMomentEvent("s1", BuddyMoment.SessionEnded), Assert.Single(moments));
    }

    // Rule B end to end: an idle session twenty minutes quiet is still there,
    // so its idle stretch crosses the long-idle threshold and LongIdle fires —
    // under Claude Buddy's five-minute expiry it would have "ended" at five.
    [Fact]
    public void ALongQuietLiveSessionReachesLongIdleInsteadOfEnding()
    {
        var t = Now;
        var reader = new StatusReader(_dir, _ => true, _ => true, () => t);
        Put("s", Ps1(state: "idle"), t);

        var first = reader.Publish(StatusReader.Scan(_dir, t, _ => true), t);
        var later = t.AddMinutes(15).AddSeconds(1);
        var second = reader.Publish(StatusReader.Scan(_dir, later, _ => true), later);

        var moments = BuddyMoments.Classify(first, second, new DateTimeOffset(later), new DateTimeOffset(t));
        Assert.Equal(new BuddyMomentEvent("s", BuddyMoment.LongIdle), Assert.Single(moments));
    }

    [Fact]
    public async Task AScanPublishesSnapshotsToWhoeverIsListening()
    {
        Put("s", Ps1(state: "waiting", transcript: @"C:\\t\\s.jsonl", cwd: @"C:\\work\\proj"));
        var reader = new StatusReader(_dir, _ => true, _ => true, () => Now);
        IReadOnlyList<SessionSnapshot>? got = null;
        reader.SnapshotsPublished = s => got = s;

        await reader.ScanAsync();

        var snap = Assert.Single(got!);
        Assert.Equal("s", snap.SessionId);
        Assert.Equal("waiting", snap.State);
        Assert.Equal(@"C:\t\s.jsonl", snap.TranscriptPath);
        Assert.Equal("A real session title", snap.Label);
        Assert.Equal(new DateTimeOffset(Now), snap.StateSince);
        Assert.Equal(_dir, reader.StatusDir);
    }

    [Fact]
    public async Task AScanWithNobodyListeningIsHarmless()
    {
        Put("s", Ps1());
        using var reader = new StatusReader(_dir, _ => true, _ => true, () => Now);

        await reader.ScanAsync();
    }

    // Two scans cannot overlap: a second one that arrives while the first is
    // still reading the disk does nothing, rather than publishing out of
    // order.
    [Fact]
    public async Task ASecondScanWhileOneIsRunningDoesNothing()
    {
        Put("s", Ps1(pid: 5));
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var reader = new StatusReader(_dir, _ => { entered.Set(); gate.Wait(TimeSpan.FromSeconds(10)); return true; },
            _ => true, () => Now);
        var published = 0;
        reader.SnapshotsPublished = _ => Interlocked.Increment(ref published);

        var first = reader.ScanAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await reader.ScanAsync();
        Assert.Equal(0, published);

        gate.Set();
        await first;
        Assert.Equal(1, published);
    }

    // Nothing here is expected to throw, but if something does the scan
    // records it and the next tick simply tries again.
    [Fact]
    public async Task AScanThatThrowsIsRecordedNotRaised()
    {
        var logs = Path.Combine(_dir, "..", "hatchai-status-log-" + Guid.NewGuid().ToString("N"));
        using var scope = CrashLog.ScopeForTests(logs);
        Put("s", Ps1(pid: 5));
        var reader = new StatusReader(_dir, _ => throw new InvalidOperationException("boom"), _ => true, () => Now);

        await reader.ScanAsync();
        await reader.ScanAsync(); // and the gate was released

        Assert.Contains("StatusReader.Scan", File.ReadAllText(CrashLog.Path_));
        try { Directory.Delete(logs, true); } catch { }
    }

    // The app's default seams: the real status folder, the real liveness
    // check. Constructing one reads nothing.
    [Fact]
    public void TheDefaultReaderPointsAtTheStatusDirectory()
    {
        using var reader = new StatusReader();

        Assert.Equal(StatusDirectory.Path(), reader.StatusDir);
    }
}
