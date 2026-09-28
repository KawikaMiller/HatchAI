using System.Text.Json;
using Xunit;

namespace HatchAI.Tests;

// HatchAIHook.ps1 as a real subprocess, the way Claude Code, Codex and Grok
// run it: a payload on stdin, -State/-Agent/-TempDir on the command line, and
// nothing observable but the exit code, the two streams and the file it
// writes under <TempDir>\claude_buddy.
//
// Ported from Claude Buddy's HookScriptPs1Tests, which targeted the same
// script under its old name. Two changes. It runs under Windows PowerShell
// 5.1 as well as pwsh, because 5.1 is what every command the installer wires
// actually invokes, and Claude Buddy's version only ever ran pwsh. And it adds
// the test that matters most for HatchAI: that what the hook writes is what
// StatusReader reads, through StatusReader itself rather than a string match.
public class HookScriptPs1Tests
{
    private static readonly string HookScript = HookHarness.Script("HatchAIHook.ps1");

    public static IEnumerable<object[]> Engines()
    {
        yield return new object[] { "powershell" };
        if (HookHarness.PwshAvailable()) yield return new object[] { "pwsh" };
    }

    private static HookHarness.Result RunHook(
        string engine, string agent, string state, string payloadJson, string tempDir,
        IDictionary<string, string>? extraEnv = null) =>
        HookHarness.RunPowerShell(
            engine, HookScript,
            new[] { "-State", state, "-Agent", agent, "-TempDir", tempDir },
            sandbox: tempDir, stdin: payloadJson, extraEnv: extraEnv);

    // Codex reads a hook's stdout as strict permission-request JSON and
    // treats exit code 2 as a deny, so a hook that ever prints anything
    // starts refusing the user's own approvals. Asserted in every test.
    private static void AssertSilentSuccess(HookHarness.Result result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private static string StatusDir(string tempDir) => Path.Combine(tempDir, StatusDirectory.FolderName);

    private static string StatusFilePath(string tempDir, string sessionId) =>
        Path.Combine(StatusDir(tempDir), sessionId + ".txt");

    private static string Payload(object fields) => JsonSerializer.Serialize(fields);

    // A direct C# port of the script's Get-CksumCrc, so the auto-colour test
    // has a golden value computed rather than guessed. The golden check
    // below is the real `cksum` of "/tmp/proj", captured by hand in Claude
    // Buddy.
    internal static uint WindowsCksumCrc(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        uint crc = 0;

        void Roll(byte value)
        {
            crc ^= (uint)value << 24;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        foreach (var b in bytes) Roll(b);

        var len = bytes.Length;
        while (len > 0)
        {
            Roll((byte)(len & 0xFF));
            len /= 256;
        }

        return ~crc;
    }

    [Fact]
    public void WindowsCrc32Port_AgreesWithThePosixCksumBinary() =>
        Assert.Equal(591481296u, WindowsCksumCrc("/tmp/proj"));

    private static string ExpectedAutoColor(string cwd)
    {
        string[] palette =
        {
            "red", "orange", "yellow", "green", "teal", "cyan",
            "blue", "purple", "violet", "magenta", "pink"
        };
        return palette[(int)(WindowsCksumCrc(cwd) % (uint)palette.Length)];
    }

    // ---- the contract with StatusReader --------------------------------------

    // The one property this fork exists to keep: a file the hook writes is a
    // file StatusReader reads, with every field it uses, and survives the
    // reader's rules. If this fails, HatchAI would install hooks and then see
    // no sessions — silently, which is the worst way for it to fail.
    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void WhatTheHookWritesIsWhatStatusReaderReads(string engine)
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        var projectDir = Directory.CreateTempSubdirectory("hatchai-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllLines(transcript, new[]
        {
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
            "{\"type\":\"ai-title\",\"aiTitle\":\"Café naming — part two\",\"sessionId\":\"s-contract\"}"
        });

        var payload = Payload(new { session_id = "s-contract", cwd = @"C:\work\proj", transcript_path = transcript });
        AssertSilentSuccess(RunHook(engine, "claude", "generating", payload, tempDir));

        var path = StatusFilePath(tempDir, "s-contract");
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "a BOM makes System.Text.Json reject the file");

        var entry = StatusReader.ReadFile(path);
        Assert.NotNull(entry);
        Assert.Equal("s-contract", entry!.SessionId);
        Assert.Equal("generating", entry.Status.State);
        Assert.Equal("claude", entry.Status.Cli);
        Assert.Equal(SessionSource.ClaudeCode, entry.Status.Source);
        Assert.Equal(@"C:\work\proj", entry.Status.Cwd);
        Assert.Equal(transcript, entry.Status.TranscriptPath);
        // UTF-8 end to end, which 5.1's Set-Content would have broken.
        Assert.Equal("Café naming — part two", entry.Status.Title);

        // And it survives the rules as a live session.
        var kept = StatusReader.Scan(StatusDir(tempDir), DateTime.UtcNow, _ => true);
        Assert.Equal(new[] { "s-contract" }, kept.Select(e => e.SessionId));

        // The exact key set the Windows hook has always written, so a reader
        // written against Claude Buddy's file keeps working against this one.
        using var doc = JsonDocument.Parse(bytes);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "color", "cli", "cwd", "session_pid", "state", "term_id", "term_pid", "term_program", "title", "transcript_path" }
                .OrderBy(k => k, StringComparer.Ordinal),
            keys);
    }

    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void CodexAndGrokFilesAreReadWithTheirOwnSource(string engine)
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        foreach (var (agent, source) in new[] { ("codex", SessionSource.Codex), ("grok", SessionSource.Grok) })
        {
            var payload = Payload(new { session_id = "s-" + agent, cwd = @"C:\proj", transcript_path = "" });
            AssertSilentSuccess(RunHook(engine, agent, "idle", payload, tempDir));

            var entry = StatusReader.ReadFile(StatusFilePath(tempDir, "s-" + agent));
            Assert.NotNull(entry);
            Assert.Equal(agent, entry!.Status.Cli);
            Assert.Equal(source, entry.Status.Source);
            Assert.Equal("idle", entry.Status.State);
        }
    }

    // ---- ported from Claude Buddy -------------------------------------------

    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgent(string engine)
    {
        foreach (var agent in new[] { "claude", "codex", "grok" })
        foreach (var state in new[] { "idle", "generating", "waiting" })
        {
            var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
            var payload = Payload(new { session_id = "s-" + agent + "-" + state, cwd = @"C:\proj", transcript_path = "" });

            AssertSilentSuccess(RunHook(engine, agent, state, payload, tempDir));
            Assert.True(File.Exists(StatusFilePath(tempDir, "s-" + agent + "-" + state)));
        }
    }

    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void EndedStateDeletesTheStatusFileAndExitsSilently(string engine)
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        Directory.CreateDirectory(StatusDir(tempDir));
        var file = StatusFilePath(tempDir, "s1");
        File.WriteAllText(file, "stale status");

        var payload = Payload(new { session_id = "s1", cwd = @"C:\proj", transcript_path = "" });
        AssertSilentSuccess(RunHook(engine, "claude", "ended", payload, tempDir));

        Assert.False(File.Exists(file), "ended must delete the session's status file");
    }

    // StatusReader opens with FileShare.Delete precisely so it can never be
    // the reader that makes this delete fail. Proven end to end here with
    // the real hook rather than File.Delete.
    [WindowsFact]
    public void EndedStillDeletesWhileStatusReaderHoldsTheFileOpen()
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        var payload = Payload(new { session_id = "held", cwd = @"C:\proj", transcript_path = "" });
        AssertSilentSuccess(RunHook("powershell", "claude", "idle", payload, tempDir));
        var file = StatusFilePath(tempDir, "held");

        using (StatusReader.OpenShared(file))
        {
            AssertSilentSuccess(RunHook("powershell", "claude", "ended", payload, tempDir));
        }

        Assert.False(File.Exists(file));
    }

    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void MissingSessionId_WritesTheStatusFileNamedUnknown(string engine)
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        var payloadJson = "{\"cwd\":\"C:\\\\proj\",\"transcript_path\":\"\"}";

        AssertSilentSuccess(RunHook(engine, "claude", "idle", payloadJson, tempDir));

        Assert.True(File.Exists(StatusFilePath(tempDir, "unknown")));
    }

    [WindowsFact]
    public void CustomTitleWinsOverAiTitle_RegardlessOfWhichWasWrittenLast()
    {
        AssertCustomTitleWins(writeCustomFirst: true);
        AssertCustomTitleWins(writeCustomFirst: false);
    }

    private static void AssertCustomTitleWins(bool writeCustomFirst)
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        var projectDir = Directory.CreateTempSubdirectory("hatchai-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");

        var customLine = "{\"type\":\"custom-title\",\"customTitle\":\"my name\",\"sessionId\":\"s1\"}";
        var aiLine = "{\"type\":\"ai-title\",\"aiTitle\":\"auto name\",\"sessionId\":\"s1\"}";
        File.WriteAllLines(transcript, writeCustomFirst ? new[] { customLine, aiLine } : new[] { aiLine, customLine });

        var payload = Payload(new { session_id = "s1", cwd = @"C:\proj", transcript_path = transcript });
        AssertSilentSuccess(RunHook("powershell", "claude", "idle", payload, tempDir));

        Assert.Equal("my name", StatusReader.ReadFile(StatusFilePath(tempDir, "s1"))!.Status.Title);
    }

    // Kept verbatim from Claude Buddy, and inert unless Claude Buddy's own
    // marker is in the folder — HatchAI never writes it. Tested so that, on a
    // machine running both, the two hooks provably agree on the colour.
    [WindowsFact]
    public void AutoColorMarker_AppendsAgentColorRecordMatchingThePortedCksumHash()
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        Directory.CreateDirectory(StatusDir(tempDir));
        File.WriteAllText(Path.Combine(StatusDir(tempDir), ".auto-color"), "");

        var projectDir = Directory.CreateTempSubdirectory("hatchai-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllText(transcript, "");

        const string cwd = @"C:\some\fixed\project\path";
        var payload = Payload(new { session_id = "s1", cwd, transcript_path = transcript });
        AssertSilentSuccess(RunHook("powershell", "claude", "idle", payload, tempDir));

        var expectedColor = ExpectedAutoColor(cwd);
        Assert.Contains($"\"color\":\"{expectedColor}\"", File.ReadAllText(StatusFilePath(tempDir, "s1")));
        Assert.Contains(
            $"{{\"type\":\"agent-color\",\"agentColor\":\"{expectedColor}\",\"sessionId\":\"s1\"}}",
            File.ReadAllText(transcript));
    }

    [WindowsFact]
    public void WithoutTheAutoColorMarker_TheTranscriptIsNeverWritten()
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        var projectDir = Directory.CreateTempSubdirectory("hatchai-hook-proj-").FullName;
        var transcript = Path.Combine(projectDir, "t.jsonl");
        File.WriteAllText(transcript, "");

        var payload = Payload(new { session_id = "s1", cwd = @"C:\some\project", transcript_path = transcript });
        AssertSilentSuccess(RunHook("powershell", "claude", "idle", payload, tempDir));

        Assert.Equal("", File.ReadAllText(transcript));
        Assert.Contains("\"color\":\"\"", File.ReadAllText(StatusFilePath(tempDir, "s1")));
    }

    [WindowsFact]
    public void GrokEnvOverridesAClaudeArgvAndWritesCliGrok()
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        var payload = Payload(new { session_id = "g1", cwd = @"C:\proj", transcript_path = "" });
        var env = new Dictionary<string, string>
        {
            ["GROK_SESSION_ID"] = "g1",
            ["GROK_HOOK_EVENT"] = "session_start"
        };

        AssertSilentSuccess(RunHook("powershell", "claude", "idle", payload, tempDir, env));

        Assert.Contains("\"cli\":\"grok\"", File.ReadAllText(StatusFilePath(tempDir, "g1")));
    }

    [WindowsFact]
    public void GrokCamelCasePayloadFieldsAreRead()
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        var payload = Payload(new { sessionId = "camel-1", cwd = @"C:\proj", transcriptPath = "" });

        AssertSilentSuccess(RunHook("powershell", "grok", "idle", payload, tempDir));

        Assert.Contains("\"cli\":\"grok\"", File.ReadAllText(StatusFilePath(tempDir, "camel-1")));
    }

    [WindowsFact]
    public void GrokAutoColorDoesNotAppendToTheTranscript()
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        Directory.CreateDirectory(StatusDir(tempDir));
        File.WriteAllText(Path.Combine(StatusDir(tempDir), ".auto-color"), "");

        var sessionDir = Path.Combine(tempDir, "sess");
        Directory.CreateDirectory(sessionDir);
        var transcript = Path.Combine(sessionDir, "updates.jsonl");
        File.WriteAllText(transcript, "{\"method\":\"session/update\"}\n");
        File.WriteAllText(Path.Combine(sessionDir, "summary.json"),
            """{"generated_title":"Grok title","title_is_manual":false}""");

        const string cwd = @"C:\some\fixed\project\path";
        var payload = Payload(new { session_id = "g-color", cwd, transcript_path = transcript });
        AssertSilentSuccess(RunHook("powershell", "grok", "idle", payload, tempDir));

        Assert.Equal("{\"method\":\"session/update\"}\n", File.ReadAllText(transcript));
        var status = File.ReadAllText(StatusFilePath(tempDir, "g-color"));
        Assert.Contains("\"cli\":\"grok\"", status);
        Assert.Contains("\"title\":\"Grok title\"", status);
        Assert.Contains($"\"color\":\"{ExpectedAutoColor(cwd)}\"", status);
    }

    [WindowsFact]
    public void CodexRolloutFallback_FindsTheRolloutFile_WithoutCrashingOrPrinting()
    {
        var tempDir = HookHarness.NewSandbox("hatchai-hook-ps1-");
        var codexHome = Path.Combine(tempDir, "codexhome");
        const string sessionId = "abc123-session";
        var rolloutDir = Path.Combine(codexHome, "sessions", "2026", "08", "21");
        Directory.CreateDirectory(rolloutDir);
        var rolloutFile = Path.Combine(rolloutDir, $"rollout-2026-08-21T00-00-00-{sessionId}.jsonl");
        File.WriteAllText(rolloutFile, "not a real codex rollout row\n");

        var payload = Payload(new { session_id = sessionId, cwd = @"C:\proj", transcript_path = "" });
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = codexHome };
        AssertSilentSuccess(RunHook("powershell", "codex", "idle", payload, tempDir, env));

        Assert.Equal(rolloutFile, StatusReader.ReadFile(StatusFilePath(tempDir, sessionId))!.Status.TranscriptPath);
    }
}
