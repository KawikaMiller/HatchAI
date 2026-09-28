using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

// Every [AvaloniaFact] in this assembly shares one Avalonia application and
// one headless dispatcher (see TestAppBuilder), so test classes run one at a
// time rather than constructing windows on several threads at once — the same
// defence Claude Buddy's UI suite keeps against Avalonia's FontManager race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HatchAI.Tests;

// Runs before any test in this assembly touches a line of app code, and before
// the Avalonia bootstrap in TestAppBuilder. Points every seam that would
// otherwise reach something real on the developer's machine at a scratch
// folder: HatchAI's settings (a BuddyWindow reads them the moment it is
// shown), the status folder root (Claude Buddy's hooks write the real one),
// and the crash log.
internal static class TestBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "hatchai-uitests-" + Guid.NewGuid().ToString("N")[..8]);

        var settings = Path.Combine(root, "settings");
        Directory.CreateDirectory(settings);
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", settings);

        var statusRoot = Path.Combine(root, "status-root");
        Directory.CreateDirectory(statusRoot);
        Environment.SetEnvironmentVariable("HATCHAI_STATUS_ROOT", statusRoot);

        Environment.SetEnvironmentVariable("HATCHAI_LOG_DIR", Path.Combine(root, "logs"));

        HatchAISettings.ReloadForTests();
    }
}
