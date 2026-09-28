using Xunit;

namespace HatchAI.Tests
{
    // Serialises every test class that touches HatchAISettings or the crash
    // log's shared state.
    //
    // HatchAISettings is a static class holding one file's worth of JSON for
    // the whole process, and these classes repoint HATCHAI_SETTINGS_DIR and
    // reload it between cases. xUnit runs classes in parallel by default, so
    // without this one class's reload lands in the middle of another's
    // assertions — the race Claude Buddy's suites documented and fixed the
    // same way.
    [CollectionDefinition("Settings")]
    public class SettingsCollection
    {
    }

    // CrashLogFileTests asserts on what is in the log it scoped; kept in its
    // own serial collection as it was in Claude Buddy.
    [CollectionDefinition("LogDir")]
    public class LogDirCollection
    {
    }
}