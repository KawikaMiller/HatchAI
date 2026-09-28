using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace HatchAI.Tests;

// Runs once, before any test in this assembly, no matter which test class
// happens to run first. Every seam that would otherwise point at something
// real on the developer's machine is pointed at a scratch folder here, before
// any static constructor can read it:
//
//   HATCHAI_SETTINGS_DIR â€” HatchAISettings falls back to the real
//     %APPDATA%\HatchAI otherwise, and a test that writes a preference would
//     write it there for good.
//   HATCHAI_STATUS_ROOT â€” StatusDirectory falls back to the real temp folder,
//     where Claude Buddy's hooks are writing live status files. HatchAI never
//     writes there, but a test that *reads* it would be reading whatever the
//     developer's sessions happen to be doing.
//   HATCHAI_LOG_DIR â€” CrashLog falls back to the real %LOCALAPPDATA%\HatchAI.
//
// A status root rather than TMPDIR, for the reason Claude Buddy found the
// hard way (CB-172): moving TMPDIR inside the test process breaks the
// coverage collector's IPC.
internal static class TestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        var root = Path.Combine(Path.GetTempPath(), "hatchai-integrationtests-" + Guid.NewGuid().ToString("N")[..8]);

        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", Path.Combine(root, "settings"));

        var statusRoot = Path.Combine(root, "status-root");
        Directory.CreateDirectory(statusRoot);
        Environment.SetEnvironmentVariable("HATCHAI_STATUS_ROOT", statusRoot);

        Environment.SetEnvironmentVariable("HATCHAI_LOG_DIR", Path.Combine(root, "logs"));

        // Any installer this process starts inherits this, and refuses to
        // write outside it. Tests that run one on purpose narrow it further.
        Environment.SetEnvironmentVariable("HATCHAI_INSTALLER_SANDBOX", Path.Combine(root, "installer-sandbox"));

        HatchAISettings.ReloadForTests();
    }
}
