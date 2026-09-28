using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;

namespace HatchAI.Tests;

// The Settings window's "Session hooks" row: what it says, what the button
// does, and that clicking it twice is fine. The install itself is always the
// seam here — TestBootstrap replaces it with one that throws, so a test that
// forgets to install its own fake fails instead of running the real
// installer against this machine's Claude Code settings.
[Collection("Settings")]
public class HookSettingsUiTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");
        return (SettingsWindow)ctor.Invoke(null);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // Runs `body` with both seams replaced, and puts back whatever was there
    // (TestBootstrap's safe defaults) afterwards.
    private static void WithSeams(Func<Task<HookSetup.Result>> install, Func<string> state, Action body)
    {
        var installWas = SettingsWindow.InstallHooksForTests;
        var stateWas = SettingsWindow.HookStateForTests;
        try
        {
            SettingsWindow.InstallHooksForTests = install;
            SettingsWindow.HookStateForTests = state;
            body();
        }
        finally
        {
            SettingsWindow.InstallHooksForTests = installWas;
            SettingsWindow.HookStateForTests = stateWas;
        }
    }

    private static readonly HookSetup.Result Wired =
        new(true, new[] { "Claude Code: wired", "Codex: not installed, skipped", "Grok Build: not installed, skipped" }, "");

    [AvaloniaFact]
    public void TheRowSaysWhatItDoesAndWhatIsInstalledNow()
    {
        WithSeams(() => Task.FromResult(Wired), () => "Not installed yet. Until they are, the buddy can't see your sessions.", () =>
        {
            var window = NewWindow();

            var texts = window.AllRows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>().Prepend(r as TextBlock))
                .OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Session hooks", texts);
            Assert.Contains(SettingsWindow.HooksDescription, texts);
            // Between the buddy's rows and the import.
            Assert.True(texts.IndexOf("Show buddy") < texts.IndexOf("Session hooks"));
            Assert.True(texts.IndexOf("Session hooks") < texts.IndexOf("Import buddy from Claude Buddy"));

            Assert.Equal("Install hooks", window.InstallHooksButton!.Content);
            Assert.True(window.InstallHooksButton.IsEnabled);
            Assert.Equal("Not installed yet. Until they are, the buddy can't see your sessions.", window.HooksState!.Text);
            Assert.False(window.HooksOutcome!.IsVisible);

            // What a person needs to trust the button: it merges, it leaves
            // other tools alone, and it is safe to press again.
            Assert.Contains("beside whatever is already there", SettingsWindow.HooksDescription);
            Assert.Contains("Claude Buddy's included", SettingsWindow.HooksDescription);
            Assert.Contains("Safe to click again", SettingsWindow.HooksDescription);
        });
    }

    [AvaloniaFact]
    public void ClickingRunsTheInstallThenShowsTheOutcomeAndTheNewState()
    {
        var runs = 0;
        var installed = false;
        WithSeams(
            () => { runs++; installed = true; return Task.FromResult(Wired); },
            () => installed ? "Installed for Claude Code." : "Not installed yet.",
            () =>
            {
                var window = NewWindow();
                Assert.Equal("Not installed yet.", window.HooksState!.Text);

                Click(window.InstallHooksButton!);

                Assert.Equal(1, runs);
                Assert.True(window.HooksOutcome!.IsVisible);
                Assert.Equal(HookSetup.OutcomeText(Wired), window.HooksOutcome.Text);
                Assert.Equal("Installed for Claude Code.", window.HooksState.Text);
                Assert.True(window.InstallHooksButton!.IsEnabled);
            });
    }

    // Safe to click repeatedly: each click is one more run of an idempotent
    // installer, and the row ends in the same state every time.
    [AvaloniaFact]
    public void ClickingAgainRunsAgainAndEndsTheSame()
    {
        var runs = 0;
        WithSeams(() => { runs++; return Task.FromResult(Wired); }, () => "Installed for Claude Code.", () =>
        {
            var window = NewWindow();

            Click(window.InstallHooksButton!);
            var first = window.HooksOutcome!.Text;
            Click(window.InstallHooksButton!);

            Assert.Equal(2, runs);
            Assert.Equal(first, window.HooksOutcome.Text);
            Assert.True(window.InstallHooksButton!.IsEnabled);
        });
    }

    // While a run is in flight the button is off and the row says so; it
    // comes back when the run ends.
    [AvaloniaFact]
    public void TheButtonIsDisabledWhileTheInstallerRuns()
    {
        var pending = new TaskCompletionSource<HookSetup.Result>();
        WithSeams(() => pending.Task, () => "Not installed yet.", () =>
        {
            var window = NewWindow();

            var run = window.InstallHooksAsync();

            Assert.False(window.InstallHooksButton!.IsEnabled);
            Assert.Equal("Installing…", window.HooksOutcome!.Text);
            Assert.True(window.HooksOutcome.IsVisible);

            pending.SetResult(Wired);
            run.GetAwaiter().GetResult();

            Assert.True(window.InstallHooksButton.IsEnabled);
            Assert.Equal(HookSetup.OutcomeText(Wired), window.HooksOutcome.Text);
        });
    }

    [AvaloniaFact]
    public void AFailedRunIsShownAsAFailure()
    {
        var failed = new HookSetup.Result(false, new[] { "Claude Code: failed (not safe to edit)" }, "");
        WithSeams(() => Task.FromResult(failed), () => "Not installed yet.", () =>
        {
            var window = NewWindow();

            Click(window.InstallHooksButton!);

            Assert.Equal(HookSetup.OutcomeText(failed), window.HooksOutcome!.Text);
            Assert.StartsWith("The hooks could not be installed.", window.HooksOutcome.Text);
            Assert.True(window.InstallHooksButton!.IsEnabled);
        });
    }

    // An install that throws (it should not — HookSetup.Run never does — but
    // the row must not be left stuck on "Installing…" with a dead button).
    [AvaloniaFact]
    public void AnInstallThatThrowsIsShownAsAFailureAndTheButtonComesBack()
    {
        WithSeams(() => throw new IOException("disk on fire"), () => "Not installed yet.", () =>
        {
            var window = NewWindow();

            Click(window.InstallHooksButton!);

            Assert.Equal("The hooks could not be installed. disk on fire Nothing that was already in your settings was changed.",
                window.HooksOutcome!.Text);
            Assert.True(window.InstallHooksButton!.IsEnabled);
        });
    }

    // The bootstrap's own guard, checked: with no fake installed, a click
    // does not reach the real installer.
    [AvaloniaFact]
    public void WithoutAFakeTheBootstrapGuardStopsTheRealInstaller()
    {
        var window = NewWindow();

        Click(window.InstallHooksButton!);

        Assert.Contains("A UI test reached the real hook installer.", window.HooksOutcome!.Text);
    }
}
