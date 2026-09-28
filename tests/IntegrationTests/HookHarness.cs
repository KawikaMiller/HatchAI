using System.Diagnostics;

namespace HatchAI.Tests;

// Shared plumbing for the tests that run HatchAI's hook scripts and hook
// installers as real subprocesses: where they live in the checkout, and how
// to run one with a payload on stdin and a controlled environment.
//
// Nothing here ever points a script at a real config. Every caller passes
// scratch paths, and RunPowerShell additionally overrides every environment
// variable the scripts fall back to (USERPROFILE, HOME, LOCALAPPDATA,
// APPDATA, TEMP, TMP, CODEX_HOME, GROK_HOME) with the scratch root it is
// given, and sets HATCHAI_INSTALLER_SANDBOX so the installers themselves
// refuse to write outside it. Three layers, because the one thing these tests
// must never do is edit the developer's own ~/.claude/settings.json.
internal static class HookHarness
{
    internal static readonly string RepoRoot = FindRepoRoot();
    internal static readonly string HooksDir = Path.Combine(RepoRoot, "Hooks");

    internal static string Script(string name) => Path.Combine(HooksDir, name);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hooks", "HatchAIHook.ps1")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find Hooks/HatchAIHook.ps1 by walking up from " + AppContext.BaseDirectory);
    }

    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    // Windows PowerShell 5.1, which is what every command the installers
    // write actually invokes, and what the app runs the installer with.
    internal static string WindowsPowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    // The engines worth running a script under: 5.1 because it is what
    // production uses, pwsh because a person re-running an installer by hand
    // is as likely as not to be in PowerShell 7, and the two serialise JSON
    // differently enough to have caught a bug each.
    internal static string EngineFile(string engine) => engine switch
    {
        "powershell" => WindowsPowerShell,
        "pwsh" => "pwsh",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null)
    };

    internal static bool PwshAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pwsh", "-NoProfile -Command exit 0")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // Points every fallback a hook script or installer consults at `sandbox`,
    // so that even a default the caller forgot to override lands in scratch.
    internal static void Sandbox(ProcessStartInfo psi, string sandbox)
    {
        var profile = Path.Combine(sandbox, "profile");
        var local = Path.Combine(profile, "AppData", "Local");
        var roaming = Path.Combine(profile, "AppData", "Roaming");
        var temp = Path.Combine(local, "Temp");
        foreach (var dir in new[] { profile, local, roaming, temp }) Directory.CreateDirectory(dir);

        psi.Environment["USERPROFILE"] = profile;
        psi.Environment["HOME"] = profile;
        psi.Environment["LOCALAPPDATA"] = local;
        psi.Environment["APPDATA"] = roaming;
        psi.Environment["TEMP"] = temp;
        psi.Environment["TMP"] = temp;
        psi.Environment["CODEX_HOME"] = Path.Combine(profile, ".codex");
        psi.Environment["GROK_HOME"] = Path.Combine(profile, ".grok");
        psi.Environment["HATCHAI_INSTALLER_SANDBOX"] = sandbox;

        // Grok injects these into every child, and the hook treats them as
        // stronger than -Agent; a test run from inside a Grok session would
        // otherwise relabel every case.
        psi.Environment.Remove("GROK_SESSION_ID");
        psi.Environment.Remove("GROK_HOOK_EVENT");
        psi.Environment.Remove("GROK_WORKSPACE_ROOT");
    }

    internal static Result RunPowerShell(
        string engine, string script, IEnumerable<string> args, string sandbox,
        string? stdin = null, IDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = EngineFile(engine),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);

        Sandbox(psi, sandbox);
        if (extraEnv is not null)
            foreach (var (key, value) in extraEnv) psi.Environment[key] = value;

        return Run(psi, stdin);
    }

    // Runs a command line exactly as Claude Code would: handed to cmd.exe
    // as one string, which is how a hook's "command" reaches a process on
    // Windows. That is the only way to prove the quoting the installer bakes
    // in survives being parsed, rather than assuming it does.
    internal static Result RunCommandLine(string commandLine, string sandbox, string? stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(commandLine);
        Sandbox(psi, sandbox);
        return Run(psi, stdin);
    }

    internal static Result Run(ProcessStartInfo psi, string? stdin)
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start " + psi.FileName);

        // A script may exit before reading stdin (the bash hook does, for a
        // state it does not know); a broken pipe then is a pass, not a
        // failure. Swallowed around the write only.
        try
        {
            if (stdin is not null) process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(psi.FileName + " did not exit within 120 s");
        }

        return new Result(process.ExitCode, stdout.Result, stderr.Result);
    }

    internal static string NewSandbox(string prefix) =>
        Directory.CreateTempSubdirectory(prefix).FullName;
}
