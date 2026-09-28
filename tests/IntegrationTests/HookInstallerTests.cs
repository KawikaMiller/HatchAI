using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace HatchAI.Tests;

// HatchAI's Windows hook installers, run as real subprocesses against scratch
// files. The property these exist for is the one most damaging to get wrong:
// **the installer merges into a settings file somebody already depends on,
// and never disturbs anything that is not its own.**
//
// So the central assertion is not "our entries are there", it is: take the
// file the installer wrote, remove HatchAI's entries, and what is left must
// be the original file, deep-equal as JSON. That catches a lost key, a
// reordered array, a one-element array turned into a scalar (which Claude
// Buddy's installer does, measured — see hatchai-hooks-common.ps1), and a
// neighbour's hook dropped, all with one comparison.
//
// Safety: every path passed is under a fresh scratch directory, every
// environment fallback the scripts consult is redirected there too (see
// HookHarness.Sandbox), and HATCHAI_INSTALLER_SANDBOX makes the scripts
// themselves refuse to write outside it. No test here can reach the real
// ~/.claude, ~/.codex or ~/.grok.
public class HookInstallerTests
{
    public static IEnumerable<object[]> Engines()
    {
        yield return new object[] { "powershell" };
        if (HookHarness.PwshAvailable()) yield return new object[] { "pwsh" };
    }

    private const string Marker = "HatchAIHook.ps1";

    // Claude Buddy's own eight entries exactly as its installer writes them
    // (copied from a real install, with the user folder replaced), plus a
    // third-party hook sharing an event with ours, an empty group somebody
    // else left, and the ordinary settings that sit beside hooks. The arrays
    // with one element and none are there on purpose.
    internal const string ExistingClaudeSettings = """
    {
      "model": "opus",
      "theme": "dark",
      "cleanupPeriodDays": 30,
      "includeCoAuthoredBy": true,
      "autoUpdatesChannel": null,
      "permissions": { "allow": ["Bash(ls:*)"], "deny": [], "additionalDirectories": ["C:\\work", "D:\\más — café"] },
      "env": { "MAX_THINKING_TOKENS": "8000" },
      "enabledPlugins": { "example@market": true },
      "statusLine": { "type": "command", "command": "powershell -File C:\\tools\\status.ps1", "padding": 0 },
      "hooks": {
        "Stop": [
          { "hooks": [ { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\AppData\\Local\\ClaudeBuddy\\ClaudeBuddyHook.ps1\" -State idle -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\"", "type": "command" } ] }
        ],
        "UserPromptSubmit": [
          { "hooks": [ { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\AppData\\Local\\ClaudeBuddy\\ClaudeBuddyHook.ps1\" -State generating -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\"", "type": "command" } ] }
        ],
        "SessionEnd": [
          { "hooks": [ { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\AppData\\Local\\ClaudeBuddy\\ClaudeBuddyHook.ps1\" -State ended -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\"", "type": "command" } ] }
        ],
        "SessionStart": [
          { "hooks": [ { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\AppData\\Local\\ClaudeBuddy\\ClaudeBuddyHook.ps1\" -State idle -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\"", "type": "command" } ] }
        ],
        "Notification": [
          { "hooks": [ { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\AppData\\Local\\ClaudeBuddy\\ClaudeBuddyHook.ps1\" -State waiting -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\"", "type": "command" } ], "matcher": "permission_prompt" },
          { "hooks": [ { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\AppData\\Local\\ClaudeBuddy\\ClaudeBuddyHook.ps1\" -State waiting -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\"", "type": "command" } ], "matcher": "elicitation_dialog" },
          { "hooks": [ { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\AppData\\Local\\ClaudeBuddy\\ClaudeBuddyHook.ps1\" -State generating -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\"", "type": "command" } ], "matcher": "elicitation_complete" }
        ],
        "PreToolUse": [
          { "hooks": [ { "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\AppData\\Local\\ClaudeBuddy\\ClaudeBuddyHook.ps1\" -State generating -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\"", "type": "command" } ], "matcher": ".*" },
          { "matcher": "Bash", "hooks": [ { "type": "command", "command": "C:\\tools\\lint-bash.exe", "timeout": 30 } ] }
        ],
        "PostToolUse": [
          { "matcher": "Edit", "hooks": [] }
        ]
      }
    }
    """;

    private static readonly (string Event, string? Matcher, string State)[] ClaudeWanted =
    {
        ("SessionStart", null, "idle"),
        ("UserPromptSubmit", null, "generating"),
        ("PreToolUse", ".*", "generating"),
        ("Stop", null, "idle"),
        ("SessionEnd", null, "ended"),
        ("Notification", "permission_prompt", "waiting"),
        ("Notification", "elicitation_dialog", "waiting"),
        ("Notification", "elicitation_complete", "generating"),
    };

    private sealed record Scratch(string Root, string SettingsPath, string InstallDir, string TempDir);

    private static Scratch NewScratch(string? existing)
    {
        var root = HookHarness.NewSandbox("hatchai-installer-");
        var settings = Path.Combine(root, "profile", ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        if (existing is not null) File.WriteAllText(settings, existing);
        return new Scratch(root, settings, Path.Combine(root, "install"), Path.Combine(root, "hooktemp"));
    }

    private static HookHarness.Result RunClaudeInstaller(string engine, Scratch s, params string[] extra) =>
        HookHarness.RunPowerShell(engine, HookHarness.Script("install-windows-hooks.ps1"),
            new[] { "-SettingsPath", s.SettingsPath, "-InstallDir", s.InstallDir, "-TempDir", s.TempDir }.Concat(extra),
            sandbox: s.Root);

    private static void AssertOk(HookHarness.Result r) =>
        Assert.True(r.ExitCode == 0, $"exit {r.ExitCode}\nstdout:\n{r.Stdout}\nstderr:\n{r.Stderr}");

    private static JsonObject ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    internal static bool IsOurs(JsonNode? handler) =>
        handler is JsonObject o
        && new[] { "command", "commandWindows" }.Any(k =>
            o[k]?.GetValue<string>().Contains(Marker, StringComparison.Ordinal) == true);

    // The file minus HatchAI's entries. Our installer only ever adds whole
    // groups holding one handler of ours, so removing those groups (and an
    // event or hooks object that only we created) is its exact inverse.
    internal static JsonObject WithoutOurs(JsonObject config, JsonObject original)
    {
        var copy = config.DeepClone().AsObject();
        if (copy["hooks"] is not JsonObject hooks) return copy;

        foreach (var name in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[name] is not JsonArray groups) continue;
            foreach (var group in groups.ToList())
            {
                if (group?["hooks"] is JsonArray inner && inner.Count > 0 && inner.All(IsOurs))
                    groups.Remove(group);
            }

            var originallyHere = original["hooks"] is JsonObject oh && oh.ContainsKey(name);
            if (groups.Count == 0 && !originallyHere) hooks.Remove(name);
        }

        if (hooks.Count == 0 && original["hooks"] is null) copy.Remove("hooks");
        return copy;
    }

    internal static List<(string Event, string? Matcher, JsonObject Handler)> Ours(JsonObject config)
    {
        var found = new List<(string, string?, JsonObject)>();
        if (config["hooks"] is not JsonObject hooks) return found;
        foreach (var (name, value) in hooks)
        {
            if (value is not JsonArray groups) continue;
            foreach (var group in groups.OfType<JsonObject>())
            {
                if (group["hooks"] is not JsonArray inner) continue;
                foreach (var h in inner.Where(IsOurs))
                    found.Add((name, group["matcher"]?.GetValue<string>(), h!.AsObject()));
            }
        }

        return found;
    }

    private static int CountContaining(JsonObject config, string needle) =>
        config.ToJsonString().Split(needle).Length - 1;

    // ---- the merge -----------------------------------------------------------

    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void InstallKeepsEverythingAlreadyThereAndAddsExactlyOurEightEntries(string engine)
    {
        var s = NewScratch(ExistingClaudeSettings);
        var original = JsonNode.Parse(ExistingClaudeSettings)!.AsObject();

        AssertOk(RunClaudeInstaller(engine, s));
        var after = ReadJson(s.SettingsPath);

        // 1. Nothing that was there is gone or changed, in value or in order.
        Assert.True(JsonNode.DeepEquals(original, WithoutOurs(after, original)),
            "Removing HatchAI's entries did not give back the original file.\n--- after:\n" + after.ToJsonString());
        Assert.Equal(original.Select(kv => kv.Key), after.Select(kv => kv.Key));

        // Claude Buddy's own eight are all still there, verbatim.
        Assert.Equal(8, CountContaining(original, "ClaudeBuddyHook.ps1"));
        Assert.Equal(8, CountContaining(after, "ClaudeBuddyHook.ps1"));

        // The arrays Claude Buddy's installer would have mangled.
        Assert.Equal("""["Bash(ls:*)"]""", after["permissions"]!["allow"]!.ToJsonString());
        Assert.Equal("[]", after["permissions"]!["deny"]!.ToJsonString());
        Assert.Equal("[]", after["hooks"]!["PostToolUse"]![0]!["hooks"]!.ToJsonString());

        // 2. Ours: exactly the eight, each where it belongs and well formed.
        var ours = Ours(after);
        Assert.Equal(8, ours.Count);
        var installed = Path.Combine(s.InstallDir, "HatchAIHook.ps1");
        foreach (var (evt, matcher, state) in ClaudeWanted)
        {
            var match = Assert.Single(ours, o => o.Event == evt && o.Matcher == matcher);
            Assert.Equal("command", match.Handler["type"]!.GetValue<string>());
            Assert.Equal(
                $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{installed}\" -State {state} -TempDir \"{s.TempDir}\"",
                match.Handler["command"]!.GetValue<string>());
        }

        // Appended after what was there, never in front of it.
        Assert.Contains("ClaudeBuddyHook.ps1", after["hooks"]!["Stop"]![0]!.ToJsonString());
        Assert.Contains(Marker, after["hooks"]!["Stop"]![1]!.ToJsonString());

        // 3. The hook the commands point at is really there, and is ours.
        Assert.Equal(File.ReadAllText(HookHarness.Script("HatchAIHook.ps1")), File.ReadAllText(installed));

        // 4. A backup of exactly what was there, under a name that cannot
        //    overwrite Claude Buddy's own .claudebuddy-backup.
        Assert.Equal(ExistingClaudeSettings, File.ReadAllText(s.SettingsPath + ".hatchai-backup"));
        Assert.False(File.Exists(s.SettingsPath + ".claudebuddy-backup"));

        // 5. No BOM, and no temp file left behind.
        var bytes = File.ReadAllBytes(s.SettingsPath);
        Assert.False(bytes[0] == 0xEF, "settings.json must not start with a BOM");
        Assert.False(File.Exists(s.SettingsPath + ".hatchai-tmp"));
    }

    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void RunningItTwiceChangesNothingTheSecondTime(string engine)
    {
        var s = NewScratch(ExistingClaudeSettings);

        AssertOk(RunClaudeInstaller(engine, s));
        var first = File.ReadAllBytes(s.SettingsPath);
        var backupAfterFirst = File.ReadAllText(s.SettingsPath + ".hatchai-backup");

        var second = RunClaudeInstaller(engine, s);
        AssertOk(second);
        Assert.Contains("left unchanged", second.Stdout);

        Assert.Equal(first, File.ReadAllBytes(s.SettingsPath));
        var after = ReadJson(s.SettingsPath);
        Assert.Equal(8, Ours(after).Count);
        Assert.Equal(8, CountContaining(after, "ClaudeBuddyHook.ps1"));
        // The no-op did not overwrite the first run's backup of the real original.
        Assert.Equal(backupAfterFirst, File.ReadAllText(s.SettingsPath + ".hatchai-backup"));
        Assert.Equal(ExistingClaudeSettings, backupAfterFirst);
    }

    // A re-run after the install moved (a different InstallDir, as when the
    // app is reinstalled elsewhere) replaces ours rather than adding a second
    // set — the same strip-then-add, matched on the filename not the path.
    [WindowsFact]
    public void ARerunFromAnotherInstallDirReplacesOursInsteadOfAddingASecondSet()
    {
        var s = NewScratch(ExistingClaudeSettings);
        AssertOk(RunClaudeInstaller("powershell", s));

        var moved = s with { InstallDir = Path.Combine(s.Root, "elsewhere") };
        AssertOk(RunClaudeInstaller("powershell", moved));

        var after = ReadJson(s.SettingsPath);
        var ours = Ours(after);
        Assert.Equal(8, ours.Count);
        Assert.All(ours, o => Assert.Contains(Path.Combine(moved.InstallDir, Marker), o.Handler["command"]!.GetValue<string>()));
        Assert.Equal(8, CountContaining(after, "ClaudeBuddyHook.ps1"));
    }

    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void UninstallGivesBackTheOriginalFile(string engine)
    {
        var s = NewScratch(ExistingClaudeSettings);
        AssertOk(RunClaudeInstaller(engine, s));
        AssertOk(RunClaudeInstaller(engine, s, "-Uninstall"));

        var after = ReadJson(s.SettingsPath);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ExistingClaudeSettings), after),
            "install then uninstall should be the identity. Got:\n" + after.ToJsonString());
    }

    [WindowsFact]
    public void NoSettingsFileYetCreatesOneHoldingOnlyOurHooks()
    {
        var s = NewScratch(existing: null);

        AssertOk(RunClaudeInstaller("powershell", s));

        var after = ReadJson(s.SettingsPath);
        Assert.Equal(new[] { "hooks" }, after.Select(kv => kv.Key));
        Assert.Equal(8, Ours(after).Count);
        Assert.False(File.Exists(s.SettingsPath + ".hatchai-backup"), "nothing existed to back up");
    }

    [WindowsFact]
    public void AnExtraProfileDirIsWiredTooAndLeftAsMergedAsTheDefault()
    {
        var s = NewScratch(ExistingClaudeSettings);
        var extraDir = Path.Combine(s.Root, "profile", ".claude-work");
        Directory.CreateDirectory(extraDir);
        File.WriteAllText(Path.Combine(extraDir, "settings.json"), """{ "model": "sonnet" }""");

        // Two profiles through one -File argument, as the app passes them.
        AssertOk(RunClaudeInstaller("powershell", s, "-ProfileDir", extraDir + ";" + Path.Combine(s.Root, "profile", ".claude-other")));

        var work = ReadJson(Path.Combine(extraDir, "settings.json"));
        Assert.Equal("sonnet", work["model"]!.GetValue<string>());
        Assert.Equal(8, Ours(work).Count);
        Assert.Equal(8, Ours(ReadJson(Path.Combine(s.Root, "profile", ".claude-other", "settings.json"))).Count);
        Assert.Equal(8, Ours(ReadJson(s.SettingsPath)).Count);
    }

    // ---- refusals ------------------------------------------------------------

    // The negative control for the sandbox itself: were the guard not
    // working, every other test's "we never touch a real file" would rest on
    // the tests alone.
    [WindowsFact]
    public void TheSandboxGuardRefusesAPathOutsideIt()
    {
        var s = NewScratch(existing: null);
        var outside = HookHarness.NewSandbox("hatchai-outside-");
        var target = Path.Combine(outside, "settings.json");
        File.WriteAllText(target, """{ "model": "opus" }""");

        var r = HookHarness.RunPowerShell("powershell", HookHarness.Script("install-windows-hooks.ps1"),
            new[] { "-SettingsPath", target, "-InstallDir", s.InstallDir, "-TempDir", s.TempDir },
            sandbox: s.Root);

        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("HATCHAI_INSTALLER_SANDBOX", r.Stdout + r.Stderr);
        Assert.Equal("""{ "model": "opus" }""", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".hatchai-backup"));
    }

    [WindowsFact]
    public void AFileThatIsNotJsonIsRefusedAndLeftAlone()
    {
        const string broken = """{ "model": "opus", "hooks": { """;
        var s = NewScratch(broken);

        var r = RunClaudeInstaller("powershell", s);

        Assert.NotEqual(0, r.ExitCode);
        Assert.Equal(broken, File.ReadAllText(s.SettingsPath));
    }

    [WindowsFact]
    public void AHooksValueThatIsNotAnObjectIsRefusedAndLeftAlone()
    {
        const string odd = """{ "hooks": "managed elsewhere" }""";
        var s = NewScratch(odd);

        var r = RunClaudeInstaller("powershell", s);

        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("not safe to edit", r.Stdout + r.Stderr);
        Assert.Equal(odd, File.ReadAllText(s.SettingsPath));
    }

    // ---- Codex ---------------------------------------------------------------

    private const string ExistingCodexHooks = """
    {
      "hooks": {
        "SessionStart": [
          { "hooks": [ { "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\.codex\\claude-buddy\\ClaudeBuddyHook.ps1\" -Agent codex -State idle -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\\\"", "commandWindows": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\someone\\.codex\\claude-buddy\\ClaudeBuddyHook.ps1\" -Agent codex -State idle -TempDir \"C:\\Users\\someone\\AppData\\Local\\Temp\\\"" } ] }
        ],
        "PreToolUse": [
          { "matcher": "shell", "hooks": [ { "type": "command", "command": "guard.exe" } ] }
        ]
      },
      "other": { "keep": [1] }
    }
    """;

    [WindowsTheory]
    [MemberData(nameof(Engines))]
    public void CodexInstallMergesAndIsIdempotent(string engine)
    {
        var root = HookHarness.NewSandbox("hatchai-codex-");
        var codexHome = Path.Combine(root, "profile", ".codex");
        Directory.CreateDirectory(codexHome);
        var hooksPath = Path.Combine(codexHome, "hooks.json");
        File.WriteAllText(hooksPath, ExistingCodexHooks);
        var temp = Path.Combine(root, "hooktemp");

        string[] args = { "-CodexHome", codexHome, "-TempDir", temp + "\\" };
        AssertOk(HookHarness.RunPowerShell(engine, HookHarness.Script("install-codex-hooks.ps1"), args, root));
        var first = File.ReadAllBytes(hooksPath);

        var original = JsonNode.Parse(ExistingCodexHooks)!.AsObject();
        var after = ReadJson(hooksPath);
        Assert.True(JsonNode.DeepEquals(original, WithoutOurs(after, original)), after.ToJsonString());
        Assert.Equal(2, CountContaining(after, "ClaudeBuddyHook.ps1"));

        var ours = Ours(after);
        Assert.Equal(7, ours.Count);
        var installed = Path.Combine(codexHome, "hatchai", Marker);
        Assert.True(File.Exists(installed));
        foreach (var o in ours)
        {
            var cmd = o.Handler["command"]!.GetValue<string>();
            Assert.Equal(cmd, o.Handler["commandWindows"]!.GetValue<string>());
            Assert.Contains($"-File \"{installed}\" -Agent codex -State ", cmd);
            // Trailing separator trimmed, unlike Claude Buddy's Codex installer.
            Assert.EndsWith($"-TempDir \"{temp}\"", cmd);
            Assert.Equal(o.Event == "PermissionRequest", o.Handler["async"]?.GetValue<bool>() == true);
        }
        Assert.Equal(
            new[] { "PermissionRequest", "PostToolUse", "PreToolUse", "SessionEnd", "SessionStart", "Stop", "UserPromptSubmit" },
            ours.Select(o => o.Event).OrderBy(e => e, StringComparer.Ordinal));

        var again = HookHarness.RunPowerShell(engine, HookHarness.Script("install-codex-hooks.ps1"), args, root);
        AssertOk(again);
        Assert.Equal(first, File.ReadAllBytes(hooksPath));
    }

    // ---- Grok ----------------------------------------------------------------

    [WindowsFact]
    public void GrokGetsAFileOfItsOwnAndLeavesClaudeBuddysAlone()
    {
        var root = HookHarness.NewSandbox("hatchai-grok-");
        var grokHome = Path.Combine(root, "profile", ".grok");
        var hooksDir = Path.Combine(grokHome, "hooks");
        Directory.CreateDirectory(hooksDir);
        const string theirs = """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "ClaudeBuddyHook.ps1 idle" } ] } ] } }""";
        File.WriteAllText(Path.Combine(hooksDir, "claude-buddy.json"), theirs);

        string[] args = { "-GrokHome", grokHome, "-TempDir", Path.Combine(root, "hooktemp") };
        AssertOk(HookHarness.RunPowerShell("powershell", HookHarness.Script("install-grok-hooks.ps1"), args, root));
        var ours = Path.Combine(hooksDir, "hatchai.json");
        var first = File.ReadAllBytes(ours);

        var config = ReadJson(ours);
        Assert.Equal(6, Ours(config).Count);
        Assert.All(Ours(config), o =>
        {
            Assert.Contains("-Agent grok -State ", o.Handler["command"]!.GetValue<string>());
            Assert.Equal(15, o.Handler["timeout"]!.GetValue<int>());
        });
        Assert.True(File.Exists(Path.Combine(grokHome, "hatchai", Marker)));
        Assert.Equal(theirs, File.ReadAllText(Path.Combine(hooksDir, "claude-buddy.json")));

        AssertOk(HookHarness.RunPowerShell("powershell", HookHarness.Script("install-grok-hooks.ps1"), args, root));
        Assert.Equal(first, File.ReadAllBytes(ours));

        AssertOk(HookHarness.RunPowerShell("powershell", HookHarness.Script("install-grok-hooks.ps1"), args.Append("-Uninstall"), root));
        Assert.False(File.Exists(ours));
        Assert.Equal(theirs, File.ReadAllText(Path.Combine(hooksDir, "claude-buddy.json")));
    }

    // ---- the all-CLIs entry point the app runs --------------------------------

    private static HookHarness.Result RunAll(string root, IEnumerable<string> args)
    {
        // PATH narrowed to the system, so "is the CLI installed" is decided by
        // the scratch folders alone and not by whatever this machine has.
        var env = new Dictionary<string, string>
        {
            ["PATH"] = Environment.SystemDirectory + ";" + Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0")
        };
        return HookHarness.RunPowerShell("powershell", HookHarness.Script("install-hooks.ps1"), args, root, extraEnv: env);
    }

    [WindowsFact]
    public void TheEntryPointWiresWhatIsInstalledAndSaysWhatItSkipped()
    {
        var s = NewScratch(ExistingClaudeSettings);
        var codexHome = Path.Combine(s.Root, "profile", ".codex");
        Directory.CreateDirectory(codexHome);
        var grokHome = Path.Combine(s.Root, "profile", ".grok");  // not created: Grok absent

        var args = new[]
        {
            "-SettingsPath", s.SettingsPath, "-InstallDir", s.InstallDir, "-TempDir", s.TempDir,
            "-CodexHome", codexHome, "-GrokHome", grokHome
        };
        var r = RunAll(s.Root, args);
        AssertOk(r);

        var summary = r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("SUMMARY ")).ToArray();
        Assert.Equal(new[]
        {
            "SUMMARY Claude Code: wired",
            "SUMMARY Codex: wired",
            "SUMMARY Grok Build: not installed, skipped"
        }, summary);

        Assert.Equal(8, Ours(ReadJson(s.SettingsPath)).Count);
        Assert.Equal(7, Ours(ReadJson(Path.Combine(codexHome, "hooks.json"))).Count);
        Assert.False(Directory.Exists(grokHome));

        // And again: still one set each.
        AssertOk(RunAll(s.Root, args));
        Assert.Equal(8, Ours(ReadJson(s.SettingsPath)).Count);
        Assert.Equal(7, Ours(ReadJson(Path.Combine(codexHome, "hooks.json"))).Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ExistingClaudeSettings),
            WithoutOurs(ReadJson(s.SettingsPath), JsonNode.Parse(ExistingClaudeSettings)!.AsObject())));
    }

    [WindowsFact]
    public void TheEntryPointReportsAFailureWithANonZeroExit()
    {
        var s = NewScratch("""{ "hooks": "managed elsewhere" }""");
        var r = RunAll(s.Root, new[]
        {
            "-SettingsPath", s.SettingsPath, "-InstallDir", s.InstallDir, "-TempDir", s.TempDir,
            "-CodexHome", Path.Combine(s.Root, "nope"), "-GrokHome", Path.Combine(s.Root, "nope2")
        });

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("SUMMARY Claude Code: failed", r.Stdout);
    }

    // ---- end to end: the command the installer wrote, run as Claude Code would ----

    // Everything above proves the file is right. This proves the file *works*:
    // it takes the exact command string the installer wrote into settings.json
    // and runs it through cmd.exe, as Claude Code does, with a hook payload on
    // stdin — then reads the status file it produced through StatusReader. A
    // quoting mistake in the command, a wrong -TempDir, or a hook that writes
    // anything StatusReader cannot read, all fail here.
    [WindowsFact]
    public void TheWiredCommandsReallyProduceStatusFilesStatusReaderReads()
    {
        var s = NewScratch(ExistingClaudeSettings);
        var codexHome = Path.Combine(s.Root, "profile", ".codex");
        Directory.CreateDirectory(codexHome);
        var r = RunAll(s.Root, new[]
        {
            "-SettingsPath", s.SettingsPath, "-InstallDir", s.InstallDir, "-TempDir", s.TempDir,
            "-CodexHome", codexHome, "-GrokHome", Path.Combine(s.Root, "absent")
        });
        AssertOk(r);

        var statusDir = Path.Combine(s.TempDir, StatusDirectory.FolderName);
        string CommandFor(JsonObject config, string evt) =>
            Ours(config).First(o => o.Event == evt).Handler["command"]!.GetValue<string>();

        var claude = ReadJson(s.SettingsPath);
        var payload = JsonSerializer.Serialize(new { session_id = "e2e-claude", cwd = @"C:\work\proj", transcript_path = "", hook_event_name = "UserPromptSubmit" });
        var run = HookHarness.RunCommandLine(CommandFor(claude, "UserPromptSubmit"), s.Root, payload);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.Stdout);
        Assert.Equal("", run.Stderr);

        var entry = StatusReader.ReadFile(Path.Combine(statusDir, "e2e-claude.txt"));
        Assert.NotNull(entry);
        Assert.Equal("generating", entry!.Status.State);
        Assert.Equal(SessionSource.ClaudeCode, entry.Status.Source);
        Assert.Equal(@"C:\work\proj", entry.Status.Cwd);

        // Codex's command, which is the one Claude Buddy's installer quoted
        // wrongly; with the trim it reaches the same folder.
        var codex = ReadJson(Path.Combine(codexHome, "hooks.json"));
        payload = JsonSerializer.Serialize(new { session_id = "e2e-codex", cwd = @"C:\work\proj", transcript_path = "" });
        run = HookHarness.RunCommandLine(CommandFor(codex, "SessionStart"), s.Root, payload);
        Assert.Equal(0, run.ExitCode);
        var codexEntry = StatusReader.ReadFile(Path.Combine(statusDir, "e2e-codex.txt"));
        Assert.NotNull(codexEntry);
        Assert.Equal(SessionSource.Codex, codexEntry!.Status.Source);

        // Both survive the reader's own scan of the folder.
        var kept = StatusReader.Scan(statusDir, DateTime.UtcNow, _ => true).Select(e => e.SessionId).ToHashSet();
        Assert.Contains("e2e-claude", kept);

        // And SessionEnd, through its own wired command, removes the file.
        run = HookHarness.RunCommandLine(CommandFor(claude, "SessionEnd"), s.Root,
            JsonSerializer.Serialize(new { session_id = "e2e-claude", cwd = @"C:\work\proj" }));
        Assert.Equal(0, run.ExitCode);
        Assert.False(File.Exists(Path.Combine(statusDir, "e2e-claude.txt")));
    }
}
