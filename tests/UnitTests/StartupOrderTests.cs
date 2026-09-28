using Xunit;

namespace HatchAI.Tests;

// Startup.Run's order, which is the part of Main a test can hold on to: the
// crash log before anything that could fail, the single-instance claim before
// anything a duplicate should not do, and a duplicate doing nothing else at
// all (CB-178 in Claude Buddy).
public class StartupOrderTests
{
    [Fact]
    public void TheWinnerRunsEveryStepInOrder()
    {
        var steps = new List<string>();

        Startup.Run(
            installCrashLog: () => steps.Add("crashlog"),
            claimSingleInstance: () => { steps.Add("claim"); return true; },
            claimUiThread: () => steps.Add("uithread"),
            startUi: () => steps.Add("ui"));

        Assert.Equal(new[] { "crashlog", "claim", "uithread", "ui" }, steps);
    }

    // Another HatchAI already holds the mutex: this one writes nothing down,
    // claims no UI thread and starts no Avalonia — it returns from Main.
    [Fact]
    public void ADuplicateStopsAfterTheClaim()
    {
        var steps = new List<string>();

        Startup.Run(
            installCrashLog: () => steps.Add("crashlog"),
            claimSingleInstance: () => { steps.Add("claim"); return false; },
            claimUiThread: () => steps.Add("uithread"),
            startUi: () => steps.Add("ui"));

        Assert.Equal(new[] { "crashlog", "claim" }, steps);
    }

    // HatchAI's own mutex, never Claude Buddy's, so the two apps run side by
    // side; and never the old name by accident after a rename.
    [Fact]
    public void TheMutexIsHatchAIsOwn()
    {
        Assert.Equal("HatchAI_SingleInstance_Mutex", SingleInstance.MutexName);
        Assert.DoesNotContain("ClaudeBuddy", SingleInstance.MutexName);
    }
}
