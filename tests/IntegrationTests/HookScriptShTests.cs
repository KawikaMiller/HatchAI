using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace HatchAI.Tests;

// HatchAIHook.sh as a real subprocess, as Claude Code, Codex and Grok run it
// on macOS and Linux: `[claude|codex|grok] <state>` on argv, the payload on
// stdin, and the file it writes under $TMPDIR/claude_buddy.
//
// Ported from Claude Buddy's HookScriptShTests against the same script under
// its old name, plus the StatusReader contract test the ps1 twin has.
// **Never run by the author**: HatchAI has only been built on Windows, where
// every test here skips. They are here so the first macOS run exercises the
// fork instead of discovering it untested.
public class HookScriptShTests
{
    private static readonly string HookScript = HookHarness.Script("HatchAIHook.sh");

    private static HookHarness.Result RunHook(
        string agent, string state, string payloadJson, string tmpDir,
        IDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(HookScript);
        psi.ArgumentList.Add(agent);
        psi.ArgumentList.Add(state);
        psi.Environment["TMPDIR"] = tmpDir;
        psi.Environment["CODEX_HOME"] = Path.Combine(tmpDir, "codex-home");
        psi.Environment["GROK_HOME"] = Path.Combine(tmpDir, "grok-home");
        psi.Environment.Remove("GROK_SESSION_ID");
        psi.Environment.Remove("GROK_HOOK_EVENT");
        psi.Environment.Remove("GROK_WORKSPACE_ROOT");
        if (extraEnv is not null)
            foreach (var (key, value) in extraEnv) psi.Environment[key] = value;

        return HookHarness.Run(psi, payloadJson);
    }

    private static void AssertSilentSuccess(HookHarness.Result result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private static string StatusDir(string tmpDir) =>
        Path.Combine(tmpDir.TrimEnd('/'), StatusDirectory.FolderName);

    private static string StatusFilePath(string tmpDir, string sessionId) =>
        Path.Combine(StatusDir(tmpDir), sessionId + ".txt");

    private static string Payload(object fields) => JsonSerializer.Serialize(fields);

    [UnixFact]
    public void WhatTheHookWritesIsWhatStatusReaderReads()
    {
        var tmp = Directory.CreateTempSubdirectory("hatchai-hook-").FullName;
        var transcript = Path.Combine(tmp, "t.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"ai-title\",\"aiTitle\":\"Named\",\"sessionId\":\"s-contract\"}\n");

        var payload = Payload(new { session_id = "s-contract", cwd = "/work/proj", transcript_path = transcript });
        AssertSilentSuccess(RunHook("claude", "generating", payload, tmp));

        var entry = StatusReader.ReadFile(StatusFilePath(tmp, "s-contract"));
        Assert.NotNull(entry);
        Assert.Equal("generating", entry!.Status.State);
        Assert.Equal("claude", entry.Status.Cli);
        Assert.Equal("/work/proj", entry.Status.Cwd);
        Assert.Equal(transcript, entry.Status.TranscriptPath);
        Assert.Equal("Named", entry.Status.Title);
    }

    [UnixFact]
    public void HookAlwaysExitsZeroWithNoOutput_ForEveryLiveStateAndAgent()
    {
        foreach (var agent in new[] { "claude", "codex", "grok" })
        foreach (var state in new[] { "idle", "generating", "waiting" })
        {
            var tmp = Directory.CreateTempSubdirectory("hatchai-hook-").FullName;
            var payload = Payload(new { session_id = "s-" + agent + "-" + state, cwd = "/tmp/proj", transcript_path = "" });
            AssertSilentSuccess(RunHook(agent, state, payload, tmp));
        }
    }

    [UnixFact]
    public void HookIgnoresAnUnrecognisedState_SilentlyAndWithoutReadingStdin()
    {
        var tmp = Directory.CreateTempSubdirectory("hatchai-hook-").FullName;
        var payload = Payload(new { session_id = "s1", cwd = "/tmp/proj", transcript_path = "" });

        AssertSilentSuccess(RunHook("claude", "bogus-state", payload, tmp));
        Assert.False(Directory.Exists(StatusDir(tmp)));
    }

    [UnixFact]
    public void EndedStateDeletesTheStatusFileAndExitsSilently()
    {
        var tmp = Directory.CreateTempSubdirectory("hatchai-hook-").FullName;
        Directory.CreateDirectory(StatusDir(tmp));
        var file = StatusFilePath(tmp, "s1");
        File.WriteAllText(file, "stale status");

        var payload = Payload(new { session_id = "s1", cwd = "/tmp/proj", transcript_path = "" });
        AssertSilentSuccess(RunHook("claude", "ended", payload, tmp));
        Assert.False(File.Exists(file));
    }

    [UnixFact]
    public void WithoutTheAutoColorMarker_NoColorRecordIsEverAppended()
    {
        var tmp = Directory.CreateTempSubdirectory("hatchai-hook-").FullName;
        var transcript = Path.Combine(tmp, "t.jsonl");
        File.WriteAllText(transcript, "");

        var payload = Payload(new { session_id = "s1", cwd = "/tmp/some-project", transcript_path = transcript });
        AssertSilentSuccess(RunHook("claude", "idle", payload, tmp));

        Assert.Equal("", File.ReadAllText(transcript));
        Assert.Contains("\"color\":\"\"", File.ReadAllText(StatusFilePath(tmp, "s1")));
    }

    [UnixFact]
    public void GrokEnvOverridesAClaudeArgvAndWritesCliGrok()
    {
        var tmp = Directory.CreateTempSubdirectory("hatchai-hook-").FullName;
        var payload = Payload(new { session_id = "g1", cwd = "/tmp/proj", transcript_path = "" });
        var env = new Dictionary<string, string> { ["GROK_SESSION_ID"] = "g1", ["GROK_HOOK_EVENT"] = "session_start" };

        AssertSilentSuccess(RunHook("claude", "idle", payload, tmp, env));
        Assert.Contains("\"cli\":\"grok\"", File.ReadAllText(StatusFilePath(tmp, "g1")));
    }

    [UnixFact]
    public void FieldExtractsTheTopLevelCwd_NotANestedToolInputCwd()
    {
        var tmp = Directory.CreateTempSubdirectory("hatchai-hook-").FullName;
        var payloadJson =
            "{\"session_id\":\"s2\",\"cwd\":\"/top/level\"," +
            "\"tool_input\":{\"cwd\":\"file:///nested/level\"}," +
            "\"transcript_path\":\"\"}";

        AssertSilentSuccess(RunHook("claude", "idle", payloadJson, tmp));
        var status = File.ReadAllText(StatusFilePath(tmp, "s2"));
        Assert.Contains("\"cwd\":\"/top/level\"", status);
        Assert.DoesNotContain("/nested/level", status);
    }

    [UnixFact]
    public void MissingSessionId_WritesTheStatusFileNamedUnknown()
    {
        var tmp = Directory.CreateTempSubdirectory("hatchai-hook-").FullName;
        AssertSilentSuccess(RunHook("claude", "idle", "{\"cwd\":\"/tmp/proj\",\"transcript_path\":\"\"}", tmp));
        Assert.True(File.Exists(StatusFilePath(tmp, "unknown")));
    }
}
