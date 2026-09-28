using Xunit;

namespace HatchAI.Tests;

// The pure half of HookSetup: reading which app's hooks a config file holds,
// the line the Settings row shows about it, the command that runs the
// installer, and how the installer's output becomes something to tell the
// person. The half that runs a process is in the integration suite.
public class HookSetupTests
{
    private const string Ours =
        """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"powershell.exe -File \"C:\\x\\HatchAIHook.ps1\" -State idle"}]}]}}""";

    private const string ClaudeBuddys =
        """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"powershell.exe -File \"C:\\x\\ClaudeBuddyHook.ps1\" -State idle"}]}]}}""";

    // ---- PresenceIn ----------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"hooks":"elsewhere"}""")]
    [InlineData("""{"hooks":{"Stop":"odd"}}""")]
    [InlineData("""{"hooks":{"Stop":[null,"a string",7,{"hooks":"odd"},{"hooks":[null,"str",{"command":42},{"type":"command","command":"lint.exe"}]}]}}""")]
    public void AFileWithNoRecognisableHookHasNone(string? json) =>
        Assert.Equal(HookSetup.Presence.None, HookSetup.PresenceIn(json));

    [Fact]
    public void HatchAIsHookIsFoundByItsScriptName() =>
        Assert.Equal(HookSetup.Presence.HatchAI, HookSetup.PresenceIn(Ours));

    [Fact]
    public void ClaudeBuddysAloneIsReportedAsSuch() =>
        Assert.Equal(HookSetup.Presence.ClaudeBuddyOnly, HookSetup.PresenceIn(ClaudeBuddys));

    [Fact]
    public void BothInstalledReadsAsOurs()
    {
        var both = """{"hooks":{"Stop":[{"hooks":[{"command":"ClaudeBuddyHook.ps1 idle"}]},{"hooks":[{"command":"HatchAIHook.ps1 idle"}]}]}}""";
        Assert.Equal(HookSetup.Presence.HatchAI, HookSetup.PresenceIn(both));
    }

    // Codex's own field for the Windows command, and the bash hook's name.
    [Theory]
    [InlineData("""{"hooks":{"SessionStart":[{"hooks":[{"commandWindows":"powershell -File HatchAIHook.ps1"}]}]}}""")]
    [InlineData("""{"hooks":{"SessionStart":[{"hooks":[{"command":"bash \"$HOME/.claude/hatchai/HatchAIHook.sh\" idle"}]}]}}""")]
    public void OursIsFoundInEveryShapeTheInstallersWrite(string json) =>
        Assert.Equal(HookSetup.Presence.HatchAI, HookSetup.PresenceIn(json));

    // ---- Describe ------------------------------------------------------------

    [Fact]
    public void DescribeNamesEachCliHatchAIIsInstalledFor()
    {
        Assert.Equal("Installed for Claude Code.",
            HookSetup.Describe(HookSetup.Presence.HatchAI, HookSetup.Presence.None, HookSetup.Presence.None));
        Assert.Equal("Installed for Claude Code, Codex, Grok.",
            HookSetup.Describe(HookSetup.Presence.HatchAI, HookSetup.Presence.HatchAI, HookSetup.Presence.HatchAI));
        Assert.Equal("Installed for Grok.",
            HookSetup.Describe(HookSetup.Presence.ClaudeBuddyOnly, HookSetup.Presence.None, HookSetup.Presence.HatchAI));
    }

    // Which of the three it is does not change the line: any one of Claude
    // Buddy's being present means sessions are still visible through it.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DescribeSaysWhenClaudeBuddysHooksAreCoveringForNow(int which)
    {
        var p = new HookSetup.Presence[3];
        p[which] = HookSetup.Presence.ClaudeBuddyOnly;

        var text = HookSetup.Describe(p[0], p[1], p[2]);
        Assert.StartsWith("Not installed yet.", text);
        Assert.Contains("Claude Buddy's hooks are installed", text);
    }

    [Fact]
    public void DescribeWithNothingSaysTheBuddyCannotSeeSessions() =>
        Assert.Equal("Not installed yet. Until they are, the buddy can't see your sessions.",
            HookSetup.Describe(HookSetup.Presence.None, HookSetup.Presence.None, HookSetup.Presence.None));

    // ---- Command -------------------------------------------------------------

    [Fact]
    public void OnWindowsItRunsTheEntryPointWithPowerShell51AndNoTargetsByDefault()
    {
        var (file, args) = HookSetup.Command(@"C:\t", Array.Empty<string>(), new HookSetup.Options(), windows: true);

        Assert.EndsWith(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"), file);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(@"C:\t", "install-hooks.ps1") }, args);
    }

    [Fact]
    public void EveryExplicitTargetIsPassedAndProfilesTravelAsOneArgument()
    {
        var options = new HookSetup.Options(
            SettingsPath: @"C:\s\settings.json", InstallDir: @"C:\s\i", CodexHome: @"C:\s\codex",
            GrokHome: @"C:\s\grok", TempDir: @"C:\s\t");

        var (_, args) = HookSetup.Command(@"C:\t", new[] { ".claude-work", ".claude-two" }, options, windows: true);

        Assert.Equal(new[]
        {
            "-SettingsPath", @"C:\s\settings.json", "-InstallDir", @"C:\s\i", "-CodexHome", @"C:\s\codex",
            "-GrokHome", @"C:\s\grok", "-TempDir", @"C:\s\t", "-ProfileDirs", ".claude-work;.claude-two"
        }, args.Skip(6));
    }

    [Fact]
    public void OnMacOSItRunsTheBashEntryPoint()
    {
        var (file, args) = HookSetup.Command("/t", new[] { "x" }, new HookSetup.Options(SettingsPath: "/s"), windows: false);

        Assert.Equal("/bin/bash", file);
        Assert.Equal(new[] { Path.Combine("/t", "install-hooks.sh") }, args);
    }

    // ---- Interpret and OutcomeText ------------------------------------------------

    private const string SuccessOutput =
        "=== Claude Code\r\nHook installed: C:\\x\r\n\r\nSUMMARY Claude Code: wired\r\nSUMMARY Codex: not installed, skipped\r\nSUMMARY Grok Build: not installed, skipped\r\n";

    [Fact]
    public void AZeroExitWithASummaryIsASuccess()
    {
        var result = HookSetup.Interpret(0, SuccessOutput, "");

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "Claude Code: wired", "Codex: not installed, skipped", "Grok Build: not installed, skipped" },
            result.Summary);
        Assert.Equal(SuccessOutput, result.Output);
    }

    [Fact]
    public void ANonZeroExitOrNoSummaryIsAFailure()
    {
        Assert.False(HookSetup.Interpret(1, SuccessOutput, "").Succeeded);
        // Exit 0 with no summary at all means the entry point never got to
        // the end — PowerShell refusing to run the script, for one.
        var silent = HookSetup.Interpret(0, "", "File cannot be loaded.");
        Assert.False(silent.Succeeded);
        Assert.Equal("\nFile cannot be loaded.", silent.Output);
    }

    [Fact]
    public void SuccessTextListsWhatHappenedAndAsksForARestart()
    {
        var text = HookSetup.OutcomeText(HookSetup.Interpret(0, SuccessOutput, ""));

        Assert.Equal("Done. Claude Code: wired. Codex: not installed, skipped. Grok Build: not installed, skipped. "
            + "Restart any running sessions so they pick the hooks up.", text);
    }

    [Fact]
    public void WhenCodexWasWiredTheTrustStepIsSaid()
    {
        var text = HookSetup.OutcomeText(new HookSetup.Result(true, new[] { "Claude Code: wired", "Codex: wired" }, ""));

        Assert.Contains("Codex will ask you to trust the new hooks", text);
    }

    [Fact]
    public void FailureTextNamesWhatFailedAndSaysNothingElseChanged()
    {
        var result = HookSetup.Interpret(1,
            "SUMMARY Claude Code: failed (not safe to edit)\nSUMMARY Codex: not installed, skipped\n", "");

        Assert.Equal("The hooks could not be installed. Claude Code: failed (not safe to edit). "
            + "Nothing that was already in your settings was changed.", HookSetup.OutcomeText(result));
    }

    [Fact]
    public void AFailureWithNoSummaryFallsBackToTheLastLineOfOutput()
    {
        var text = HookSetup.OutcomeText(new HookSetup.Result(false, Array.Empty<string>(), "first\nThe real reason.\n\n"));
        Assert.Equal("The hooks could not be installed. The real reason. Nothing that was already in your settings was changed.", text);

        var empty = HookSetup.OutcomeText(new HookSetup.Result(false, Array.Empty<string>(), ""));
        Assert.Equal("The hooks could not be installed. Nothing that was already in your settings was changed.", empty);
    }
}
