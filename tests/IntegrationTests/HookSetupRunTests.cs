using System.Text.Json.Nodes;
using Xunit;

namespace HatchAI.Tests;

// The half of HookSetup that touches disk and starts a process: the scripts
// embedded in the app, written out and run exactly as the Settings button
// runs them — but with every target an explicit scratch path, every
// environment fallback redirected, and the installer's sandbox on.
public class HookSetupRunTests
{
    private static readonly string[] Expected =
    {
        "HatchAIHook.ps1", "HatchAIHook.sh", "hatchai-hooks-common.ps1",
        "install-codex-hooks.ps1", "install-codex-hooks.sh", "install-grok-hooks.ps1",
        "install-grok-hooks.sh", "install-hooks.ps1", "install-hooks.sh",
        "install-macos-hooks.sh", "install-windows-hooks.ps1",
    };

    // The app carries every script in Hooks/, and carries them as they are in
    // the checkout — a stale or missing embed would install a different hook
    // from the one the rest of the suite tested.
    [Fact]
    public void EveryHookScriptIsEmbeddedAndExtractsByteForByte()
    {
        Assert.Equal(Expected.OrderBy(n => n, StringComparer.Ordinal), HookSetup.EmbeddedScripts());
        Assert.Equal(
            Directory.GetFiles(HookHarness.HooksDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal),
            HookSetup.EmbeddedScripts());

        var dir = HookHarness.NewSandbox("hatchai-extract-");
        HookSetup.ExtractTo(dir);

        foreach (var name in Expected)
            Assert.Equal(File.ReadAllBytes(HookHarness.Script(name)), File.ReadAllBytes(Path.Combine(dir, name)));
    }

    private static IReadOnlyDictionary<string, string> SandboxEnvironment(string root)
    {
        var psi = new System.Diagnostics.ProcessStartInfo();
        HookHarness.Sandbox(psi, root);
        var env = new Dictionary<string, string>();
        foreach (var key in new[] { "USERPROFILE", "HOME", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP",
                     "CODEX_HOME", "GROK_HOME", "HATCHAI_INSTALLER_SANDBOX" })
            env[key] = psi.Environment[key]!;
        // Only the scratch folders decide which CLIs are "installed".
        env["PATH"] = Environment.SystemDirectory;
        return env;
    }

    [WindowsFact]
    public async Task TheButtonsRunMergesIntoAnExistingFileAndSaysWhatItDid()
    {
        var root = HookHarness.NewSandbox("hatchai-setup-");
        var settings = Path.Combine(root, "profile", ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, HookInstallerTests.ExistingClaudeSettings);
        var options = new HookSetup.Options(
            SettingsPath: settings,
            InstallDir: Path.Combine(root, "install"),
            CodexHome: Path.Combine(root, "profile", ".codex"),   // absent: skipped
            GrokHome: Path.Combine(root, "profile", ".grok"),     // absent: skipped
            TempDir: Path.Combine(root, "hooktemp"),
            Environment: SandboxEnvironment(root));

        var tempBefore = Directory.GetDirectories(Path.GetTempPath(), "hatchai-hook-setup-*").ToHashSet();
        var result = await HookSetup.RunAsync(options);

        Assert.True(result.Succeeded, result.Output);
        Assert.Equal(new[] { "Claude Code: wired", "Codex: not installed, skipped", "Grok Build: not installed, skipped" },
            result.Summary);
        Assert.StartsWith("Done. Claude Code: wired.", HookSetup.OutcomeText(result));

        var original = JsonNode.Parse(HookInstallerTests.ExistingClaudeSettings)!.AsObject();
        var after = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        Assert.True(JsonNode.DeepEquals(original, HookInstallerTests.WithoutOurs(after, original)));
        Assert.Equal(8, HookInstallerTests.Ours(after).Count);
        Assert.Equal(HookSetup.Presence.HatchAI, HookSetup.PresenceIn(File.ReadAllText(settings)));

        // A second click: same file, still one set.
        var bytes = File.ReadAllBytes(settings);
        Assert.True((await HookSetup.RunAsync(options)).Succeeded);
        Assert.Equal(bytes, File.ReadAllBytes(settings));

        // The extracted installers were cleaned up after each run.
        var tempAfter = Directory.GetDirectories(Path.GetTempPath(), "hatchai-hook-setup-*").ToHashSet();
        Assert.Empty(tempAfter.Except(tempBefore));
    }

    [WindowsFact]
    public async Task AFailedRunIsReportedAndTheFileIsLeftAlone()
    {
        var root = HookHarness.NewSandbox("hatchai-setup-");
        var settings = Path.Combine(root, "profile", ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        const string odd = """{ "hooks": "managed elsewhere" }""";
        File.WriteAllText(settings, odd);

        var result = await HookSetup.RunAsync(new HookSetup.Options(
            SettingsPath: settings, InstallDir: Path.Combine(root, "install"),
            CodexHome: Path.Combine(root, "nope"), GrokHome: Path.Combine(root, "nope"),
            TempDir: Path.Combine(root, "t"), Environment: SandboxEnvironment(root)));

        Assert.False(result.Succeeded);
        Assert.StartsWith("The hooks could not be installed. Claude Code: failed", HookSetup.OutcomeText(result));
        Assert.Equal(odd, File.ReadAllText(settings));
    }

    // The runner's own guard, and the reason the UI suite can set it: with
    // the sandbox pointed somewhere else, even a run aimed at a real-looking
    // path writes nothing.
    [WindowsFact]
    public async Task WithTheSandboxElsewhereTheRunRefusesToWrite()
    {
        var root = HookHarness.NewSandbox("hatchai-setup-");
        var settings = Path.Combine(root, "profile", ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{}");
        var env = new Dictionary<string, string>(SandboxEnvironment(root))
        {
            ["HATCHAI_INSTALLER_SANDBOX"] = HookHarness.NewSandbox("hatchai-elsewhere-")
        };

        var result = await HookSetup.RunAsync(new HookSetup.Options(
            SettingsPath: settings, InstallDir: Path.Combine(root, "install"),
            CodexHome: Path.Combine(root, "nope"), GrokHome: Path.Combine(root, "nope"),
            TempDir: Path.Combine(root, "t"), Environment: env));

        Assert.False(result.Succeeded);
        Assert.Equal("{}", File.ReadAllText(settings));
        Assert.False(Directory.Exists(Path.Combine(root, "install")));
    }
}
