using Xunit;

namespace HatchAI.Tests;

// Tests that only mean something on one platform — a fake CLI written as a
// shell script, or one written as a .cmd — skip rather than fail on the other,
// so a `dotnet test` run reports "skipped" for the twin that couldn't have run
// here instead of silently never exercising it. (Ported from Claude Buddy,
// where the twins were its two hook scripts; the skip messages keep its words.)
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            Skip = "bash hook only runs on macOS/Linux";
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "ps1 hook only runs on Windows";
    }
}
