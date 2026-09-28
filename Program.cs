using System.Diagnostics.CodeAnalysis;
using Avalonia;

namespace HatchAI
{
    // Excluded from coverage: the process entry point. Main hands control to
    // StartWithClassicDesktopLifetime, which owns the process for its
    // lifetime; BuildAvaloniaApp configures the one AppBuilder a process gets,
    // and the headless suite builds its own (see tests/UiTests'
    // TestAppBuilder). The order Main runs things in is Startup.Run's, which
    // is tested.
    [ExcludeFromCodeCoverage]
    internal static class Program
    {
        // Held for the process's lifetime once we own it, so it is not
        // finalized out from under us — a local would be eligible for GC (and
        // with it the finalizer that releases the mutex) as soon as Main
        // stopped referencing it.
        private static Mutex? _singleInstanceMutex;

        [STAThread]
        public static void Main(string[] args)
        {
            Startup.Run(
                installCrashLog: CrashLog.Install,

                // Whether this is the one HatchAI that gets to run. Its own
                // mutex name, never Claude Buddy's, so the two apps run side
                // by side; see SingleInstance.cs for the claim and its enum.
                claimSingleInstance: () =>
                {
                    var (claim, mutex) = SingleInstance.Claim(SingleInstance.MutexName);
                    if (SingleInstance.ShouldProceed(claim))
                    {
                        _singleInstanceMutex = mutex;
                        return true;
                    }

                    mutex.Dispose();
                    return false;
                },

                claimUiThread: Startup.ClaimUiThread,

                startUi: () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args));
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The pet and the tray are the whole UI; no Dock icon on macOS.
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .LogToTrace();
    }
}
