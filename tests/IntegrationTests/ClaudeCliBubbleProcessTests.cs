using System.Diagnostics;
using System.Text;
using Xunit;

namespace HatchAI.Tests;

// ClaudeCliBubbleGenerator.RunProcessAsync against a real subprocess (CB-202):
// a fake `claude`, written into a temp directory per test, that records its
// arguments and its stdin to files and then does whatever the case needs —
// answers, fails, prints garbage, or hangs.
//
// A .cmd on Windows and a sh script elsewhere, because those are the two
// shapes a real `claude` can take that this app will start: a native binary
// (tested only by running it), a Unix script, or an npm .cmd shim that
// CreateProcess routes through cmd.exe. A fake .cmd is therefore also the
// shim case's own shape — it gets cmd.exe as its direct child, with the pid
// consequences RunProcessAsync's comment describes.
//
// Nothing here reaches a model or the network. The real-CLI check was run by
// hand, three calls at most, and is recorded on the ticket rather than
// committed: an unattended test that spends the user's usage is not a test.
public class ClaudeCliBubbleProcessTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cb-fakeclaude-" + Guid.NewGuid().ToString("N"));

    private string WorkDir => Path.Combine(_dir, "work", BubbleVoice.WorkDirLeaf);
    private string ArgsFile => Path.Combine(_dir, "args.txt");
    private string StdinFile => Path.Combine(_dir, "stdin.txt");
    private string StartedFile => Path.Combine(_dir, "started");
    private string GoFile => Path.Combine(_dir, "go");

    public ClaudeCliBubbleProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // A killed process can hold its files for a moment after the kill
        // returns on Windows. Leaving a temp folder behind is not a failure.
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private enum Then { Answer, Fail, Garbage, Hang, WaitForGo }

    // The fake. Records first (arguments, then all of stdin, then a marker),
    // so every case can assert what was sent before it asserts what came back.
    private string FakeClaude(Then then)
    {
        if (OperatingSystem.IsWindows())
        {
            var body = then switch
            {
                Then.Answer => "echo {\"type\":\"result\",\"is_error\":false,\"result\":\"Nice build.\"}\r\nexit /b 0",
                Then.Fail => "echo {\"type\":\"result\",\"is_error\":true}\r\nexit /b 3",
                Then.Garbage => "echo this is not json\r\nexit /b 0",
                Then.Hang => "ping -n 120 127.0.0.1 > nul\r\nexit /b 0",
                _ => ":wait\r\nif exist \"%~dp0go\" exit /b 0\r\nping -n 2 127.0.0.1 > nul\r\ngoto wait",
            };
            var path = Path.Combine(_dir, "claude.cmd");
            File.WriteAllText(path,
                "@echo off\r\n"
                + "echo %* > \"%~dp0args.txt\"\r\n"
                + "findstr \"^\" > \"%~dp0stdin.txt\"\r\n"
                + "echo. > \"%~dp0started\"\r\n"
                + body + "\r\n",
                new UTF8Encoding(false));
            return path;
        }
        else
        {
            var body = then switch
            {
                Then.Answer => "echo '{\"type\":\"result\",\"is_error\":false,\"result\":\"Nice build.\"}'\nexit 0",
                Then.Fail => "echo '{\"type\":\"result\",\"is_error\":true}'\nexit 3",
                Then.Garbage => "echo 'this is not json'\nexit 0",
                Then.Hang => "sleep 120\nexit 0",
                _ => "while [ ! -e \"$dir/go\" ]; do sleep 0.2; done\nexit 0",
            };
            var path = Path.Combine(_dir, "claude");
            File.WriteAllText(path,
                "#!/bin/sh\n"
                + "dir=$(dirname \"$0\")\n"
                + "printf '%s\\n' \"$@\" > \"$dir/args.txt\"\n"
                + "cat > \"$dir/stdin.txt\"\n"
                + "echo $$ > \"$dir/started\"\n"
                + body + "\n",
                new UTF8Encoding(false));
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
    }

    private ProcessStartInfo StartInfo(string claude) =>
        ClaudeCliBubbleGenerator.StartInfoFor(claude, WorkDir, "Be the buddy.");

    // ---- the ordinary round trip --------------------------------------------

    [Fact]
    public async Task ASuccessfulCallReturnsStdoutAndTheTextWentOverStdinOnly()
    {
        const string sent = "moment=SessionStarted; prompt=fix the flaky build SECRET-MARKER";

        var stdout = await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.Answer)), sent, CancellationToken.None);

        Assert.NotNull(stdout);
        Assert.Contains("\"result\":\"Nice build.\"", stdout);

        // Over stdin, whole.
        Assert.Equal(sent, File.ReadAllText(StdinFile).TrimEnd('\r', '\n', ' '));

        // And never on the command line, which any process on the machine can
        // read. The flags that are there are the planned ones.
        var args = File.ReadAllText(ArgsFile);
        Assert.DoesNotContain("SECRET-MARKER", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("disableAllHooks", args);
        Assert.Contains("alwaysThinkingEnabled", args);
        Assert.Contains("Be the buddy.", args);
    }

    // No BOM reaches the child: it would be the first character of the user
    // message. Checked on the bytes the fake received, not on the encoding
    // object, since what matters is what crossed the pipe.
    [Fact]
    public async Task StdinArrivesAsUtf8WithNoByteOrderMark()
    {
        await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.Answer)), "cafe café", CancellationToken.None);

        var bytes = File.ReadAllBytes(StdinFile);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.StartsWith("cafe café", Encoding.UTF8.GetString(bytes));
    }

    // The working directory the child actually ran in is the one asked for,
    // created on the way if it was missing.
    [Fact]
    public async Task TheChildRunsInTheWorkDir()
    {
        Assert.False(Directory.Exists(WorkDir));

        var stdout = await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.Answer)), "hi", CancellationToken.None);

        Assert.NotNull(stdout);
        Assert.True(Directory.Exists(WorkDir));
    }

    // ---- failures -------------------------------------------------------------

    [Fact]
    public async Task ANonZeroExitIsNullEvenWithOutput()
    {
        var stdout = await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.Fail)), "hi", CancellationToken.None);

        Assert.Null(stdout);
    }

    // Garbage with exit 0 is still stdout at this layer — deciding it is not an
    // answer is BubbleCliOutput.Parse's job, one layer up. Asserted so the split
    // is explicit rather than assumed in either direction.
    [Fact]
    public async Task GarbageWithACleanExitIsReturnedForTheParserToRefuse()
    {
        var stdout = await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.Garbage)), "hi", CancellationToken.None);

        Assert.NotNull(stdout);
        Assert.Contains("this is not json", stdout);
    }

    [Fact]
    public async Task AMissingBinaryIsNullAndDoesNotThrow()
    {
        var stdout = await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(Path.Combine(_dir, OperatingSystem.IsWindows() ? "absent.exe" : "absent")),
            "hi", CancellationToken.None);

        Assert.Null(stdout);
    }

    // ---- what each null was, for the bubble log (CB-202) ---------------------

    [Fact]
    public async Task ANonZeroExitIsReportedAsOne()
    {
        var reported = new List<BubbleFailure>();

        Assert.Null(await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.Fail)), "hi", CancellationToken.None, reported.Add));

        Assert.Equal(new[] { BubbleFailure.NonZeroExit }, reported);
    }

    [Fact]
    public async Task AMissingBinaryIsReportedAsAFailedStart()
    {
        var reported = new List<BubbleFailure>();

        Assert.Null(await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(Path.Combine(_dir, OperatingSystem.IsWindows() ? "absent.exe" : "absent")),
            "hi", CancellationToken.None, reported.Add));

        Assert.Equal(new[] { BubbleFailure.StartFailed }, reported);
    }

    [Fact]
    public async Task ASuccessReportsNothing()
    {
        var reported = new List<BubbleFailure>();

        Assert.NotNull(await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.Answer)), "hi", CancellationToken.None, reported.Add));

        Assert.Empty(reported);
    }

    // A child that exits without reading its stdin, sent more than a pipe
    // holds: the write breaks with the token unfired, which is the exchange
    // failing rather than the caller giving up.
    [Fact]
    public async Task AChildThatHangsUpOnStdinIsReportedAsAProcessFailure()
    {
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_dir, "claude.cmd");
            File.WriteAllText(path, "@echo off\r\nexit /b 0\r\n", new UTF8Encoding(false));
        }
        else
        {
            path = Path.Combine(_dir, "claude");
            File.WriteAllText(path, "#!/bin/sh\nexit 0\n", new UTF8Encoding(false));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var reported = new List<BubbleFailure>();

        Assert.Null(await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(path), new string('x', 4 * 1024 * 1024), CancellationToken.None, reported.Add));

        Assert.Equal(new[] { BubbleFailure.ProcessFailed }, reported);
    }

    // Cancelled mid-run: the caller knows its own token, so nothing is said.
    [Fact]
    public async Task ACancelledRunReportsNothing()
    {
        using var cts = new CancellationTokenSource();
        var reported = new List<BubbleFailure>();

        var call = ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.WaitForGo)), "hi", cts.Token, reported.Add);
        await WaitFor(() => File.Exists(StartedFile));
        cts.Cancel();

        Assert.Null(await call);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenStartsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var stdout = await ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.Answer)), "hi", cts.Token);

        Assert.Null(stdout);
        Assert.False(File.Exists(ArgsFile));
    }

    // The controller's 6 s timeout, or a moment gone stale, arrives as `ct`.
    // The call answers null promptly, and the hung child — and on Windows the
    // ping under the fake's cmd.exe — is killed rather than left spending the
    // user's usage. "Promptly" is a generous bound: the fake would hang for two
    // minutes otherwise.
    [Fact]
    public async Task AHangIsKilledWhenTheTokenFires()
    {
        using var cts = new CancellationTokenSource();
        var pingsBefore = OperatingSystem.IsWindows() ? Pids("ping") : new HashSet<int>();
        var clock = Stopwatch.StartNew();

        var call = ClaudeCliBubbleGenerator.RunProcessAsync(StartInfo(FakeClaude(Then.Hang)), "hi", cts.Token);
        await WaitFor(() => File.Exists(StartedFile));

        // On Windows, wait for the grandchild too, so the kill below is
        // asserted against a tree that really has two levels in it.
        var pings = new List<int>();
        if (OperatingSystem.IsWindows())
            await WaitFor(() => (pings = Pids("ping").Except(pingsBefore).ToList()).Count > 0);
        cts.Cancel();

        Assert.Null(await call);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");

        // The whole tree: no ping this test started survives the kill.
        await WaitFor(() => !pings.Any(IsAlive));
    }

    // ---- the pid claim --------------------------------------------------------

    // Remembered while the child runs and forgotten once it ends, so the orb
    // scan drops its status file for exactly that window. The pid is found
    // from outside: on Unix the fake writes its own `$$`; on Windows the fake
    // is a .cmd, its direct child is cmd.exe, and the pid is whichever cmd.exe
    // appeared while the call was starting.
    [Fact]
    public async Task ThePidIsClaimedWhileTheChildRunsAndReleasedAfter()
    {
        var cmdBefore = OperatingSystem.IsWindows() ? Pids("cmd") : new HashSet<int>();

        var call = ClaudeCliBubbleGenerator.RunProcessAsync(
            StartInfo(FakeClaude(Then.WaitForGo)), "hi", CancellationToken.None);
        await WaitFor(() => File.Exists(StartedFile));

        var candidates = OperatingSystem.IsWindows()
            ? Pids("cmd").Except(cmdBefore).ToList()
            : new List<int> { int.Parse(File.ReadAllText(StartedFile).Trim()) };

        Assert.Contains(candidates, InternalSessions.IsInternal);

        File.WriteAllText(GoFile, "");
        Assert.NotNull(await call);

        Assert.DoesNotContain(candidates, InternalSessions.IsInternal);
    }

    // Released on the failure path too — the finally, not the happy path, is
    // what guarantees a pid the OS reuses later is not hidden.
    [Fact]
    public async Task ThePidIsReleasedWhenTheCallIsCancelled()
    {
        var cmdBefore = OperatingSystem.IsWindows() ? Pids("cmd") : new HashSet<int>();
        using var cts = new CancellationTokenSource();

        var call = ClaudeCliBubbleGenerator.RunProcessAsync(StartInfo(FakeClaude(Then.WaitForGo)), "hi", cts.Token);
        await WaitFor(() => File.Exists(StartedFile));

        var candidates = OperatingSystem.IsWindows()
            ? Pids("cmd").Except(cmdBefore).ToList()
            : new List<int> { int.Parse(File.ReadAllText(StartedFile).Trim()) };
        Assert.Contains(candidates, InternalSessions.IsInternal);

        cts.Cancel();
        Assert.Null(await call);

        Assert.DoesNotContain(candidates, InternalSessions.IsInternal);
    }

    // Kill is reached from a catch that promises null, including for a child
    // that has already exited by the time the catch runs.
    [Fact]
    public async Task KillToleratesAChildThatHasAlreadyExited()
    {
        using var proc = Process.Start(StartInfo(FakeClaude(Then.Garbage)))!;
        proc.StandardInput.Close();
        await proc.WaitForExitAsync();

        ClaudeCliBubbleGenerator.Kill(proc);
    }

    // ---- helpers ----------------------------------------------------------------

    private static HashSet<int> Pids(string name)
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            set.Add(p.Id);
            p.Dispose();
        }

        return set;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Polls rather than sleeps a fixed time, bounded so a broken fake fails
    // the test instead of hanging the suite.
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition never became true");
            await Task.Delay(50);
        }
    }
}
