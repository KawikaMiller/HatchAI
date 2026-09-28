using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace HatchAI.Tests;

// HatchAISettings against real files: the round trip, what survives a save,
// what an unreadable file costs, and the one-time import from Claude Buddy.
//
// Every case gets its own folder and reloads the static class against it, so
// nothing here can see another case's file — or the developer's real one.
[Collection("Settings")]
public class HatchAISettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hatchai-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string? _was = Environment.GetEnvironmentVariable("HATCHAI_SETTINGS_DIR");

    public HatchAISettingsTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", _dir);
        HatchAISettings.ReloadForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", _was);
        HatchAISettings.ReloadForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private void WriteSettings(string json) => File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));

    private JsonObject ReadSettings() => JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();

    private static void Reload() => HatchAISettings.ReloadForTests();

    // ---- where and how it is written ----------------------------------------

    [Fact]
    public void TheFileLivesInTheSeamDirectoryAndIsNamedSettingsJson()
    {
        Assert.Equal(_dir, HatchAISettings.Directory);
        Assert.Equal(SettingsPath, HatchAISettings.Path_);
    }

    [Fact]
    public void NothingIsWrittenUntilSomethingChanges()
    {
        _ = HatchAISettings.BuddyEnabled;
        _ = HatchAISettings.OrbPositionFor("buddy");

        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void AFirstSaveWritesVersionOneWithoutABomAndLeavesNoTempFile()
    {
        HatchAISettings.BuddyBubblesEnabled = false;

        var bytes = File.ReadAllBytes(SettingsPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(HatchAISettings.FileVersion, ReadSettings()["version"]!.GetValue<int>());
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    // A version a newer build wrote is not rewritten back to 1 by this one.
    [Fact]
    public void AnExistingVersionIsLeftAlone()
    {
        WriteSettings("""{ "version": 7 }""");
        Reload();

        HatchAISettings.BuddyEnabled = false;

        Assert.Equal(7, ReadSettings()["version"]!.GetValue<int>());
    }

    // The folder may not exist yet on a first run; the save creates it.
    [Fact]
    public void ASaveCreatesAMissingFolder()
    {
        var nested = Path.Combine(_dir, "not", "yet");
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", nested);
        Reload();

        HatchAISettings.BuddyEnabled = false;

        Assert.True(File.Exists(Path.Combine(nested, "settings.json")));
    }

    // ---- unknown keys, at every depth -------------------------------------------

    // The reason the file is held as a JsonObject: a key this build does not
    // know is never touched, so it survives any save exactly as it was —
    // at the top level, inside the buddy, inside a position, and in a shape
    // (an array, a nested object) this build has never seen.
    [Fact]
    public void UnknownKeysRoundTripAtEveryDepth()
    {
        WriteSettings("""
            {
              "fromTheFuture": { "deep": [ 1, { "x": "y" } ] },
              "buddyEnabled": true,
              "orbPositions": { "buddy": { "x": 1, "y": 2, "monitor": "left" }, "other": { "x": 9, "y": 9 } },
              "buddy": { "uuid": "u", "futureBuddyKey": { "a": 1 } }
            }
            """);
        Reload();

        HatchAISettings.BuddyEnabled = false;
        HatchAISettings.SetOrbPosition("buddy", 30, 40);
        HatchAISettings.UpdateBuddy(b => b["tokens"] = 5);

        var root = ReadSettings();
        Assert.Equal("y", root["fromTheFuture"]!["deep"]![1]!["x"]!.GetValue<string>());
        Assert.Equal("left", root["orbPositions"]!["buddy"]!["monitor"]!.GetValue<string>());
        Assert.Equal(30, root["orbPositions"]!["buddy"]!["x"]!.GetValue<int>());
        Assert.Equal(9, root["orbPositions"]!["other"]!["x"]!.GetValue<int>());
        Assert.Equal(1, root["buddy"]!["futureBuddyKey"]!["a"]!.GetValue<int>());
        Assert.Equal(5, root["buddy"]!["tokens"]!.GetValue<int>());
        Assert.False(root["buddyEnabled"]!.GetValue<bool>());
    }

    // ---- the defensive readers ---------------------------------------------------

    [Fact]
    public void AGarbageValueCostsOnlyThatValue()
    {
        WriteSettings("""
            {
              "buddyEnabled": "no", "buddyBubblesEnabled": false,
              "orbPositions": { "buddy": { "x": "left", "y": 2 } },
              "claudeCodeProfileDirs": [ ".claude-work", 7, null, "", { "x": 1 }, ".claude-home" ]
            }
            """);
        Reload();

        Assert.True(HatchAISettings.BuddyEnabled);
        Assert.False(HatchAISettings.BuddyBubblesEnabled);
        Assert.Null(HatchAISettings.OrbPositionFor("buddy"));
        Assert.Equal(new[] { ".claude-work", ".claude-home" }, HatchAISettings.ClaudeCodeProfileDirs);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("12.0", 12)]
    [InlineData("-5", -5)]
    [InlineData("12.5", null)]
    [InlineData("\"12\"", null)]
    [InlineData("1e12", null)]
    [InlineData("true", null)]
    public void NumberAcceptsWholeValuesOnly(string json, int? expected)
    {
        Assert.Equal(expected, HatchAISettings.Number(JsonNode.Parse(json)));
    }

    [Fact]
    public void TheReadersAnswerTheirDefaultsForNoValueAtAll()
    {
        Assert.Null(HatchAISettings.Number(null));
        Assert.Null(HatchAISettings.Number(new JsonObject()));
        Assert.Null(HatchAISettings.Text(null));
        Assert.True(HatchAISettings.Bool(null, true));
        Assert.Empty(HatchAISettings.Strings(new JsonObject()));
    }

    // A hand edit in an old Notepad leaves a BOM, which JsonNode rejects. It
    // is stripped rather than costing the whole file.
    [Fact]
    public void ALeadingBomIsTolerated()
    {
        File.WriteAllText(SettingsPath, """{ "buddyBubblesEnabled": false }""", new UTF8Encoding(true));
        Reload();

        Assert.False(HatchAISettings.BuddyBubblesEnabled);
    }

    // ---- an unreadable file -----------------------------------------------------

    // Claude Buddy resets to defaults here and its next save erases the file.
    // This file can hold months of a pet's growth, so it is copied aside
    // first — and the copy is byte-for-byte what was there.
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[ \"an array, not a settings object\" ]")]
    [InlineData("42")]
    public void AnUnreadableFileIsSetAsideBeforeAnythingOverwritesIt(string content)
    {
        WriteSettings(content);
        Reload();

        Assert.True(HatchAISettings.BuddyEnabled); // defaults
        var aside = Assert.Single(Directory.GetFiles(_dir, "settings.json.unreadable-*"));
        Assert.Equal(content, File.ReadAllText(aside));

        HatchAISettings.BuddyEnabled = false;
        Assert.False(ReadSettings()["buddyEnabled"]!.GetValue<bool>());
        Assert.False(HatchAISettings.SaveBlocked);
    }

    // A file that cannot even be opened (held exclusively by something else)
    // is neither read nor copied — so nothing may ever be written over it.
    // Preferences changed this session are lost; the file on disk is not.
    [Fact]
    public void AFileThatCannotBeReadOrCopiedIsNeverOverwritten()
    {
        WriteSettings("""{ "buddy": { "uuid": "precious" } }""");
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Reload();
            Assert.True(HatchAISettings.SaveBlocked);
            Assert.Null(HatchAISettings.BuddyObject());

            HatchAISettings.BuddyEnabled = false;
            HatchAISettings.UpdateBuddy(b => b["uuid"] = "replacement");
        }

        Assert.Equal("precious", ReadSettings()["buddy"]!["uuid"]!.GetValue<string>());
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    // A save that fails (the file held exclusively mid-session) is recorded,
    // not thrown, and the in-memory value still reads back.
    [Fact]
    public void ASaveThatFailsDoesNotThrow()
    {
        HatchAISettings.BuddyEnabled = true;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            HatchAISettings.BuddyBubblesEnabled = false;
            Assert.False(HatchAISettings.BuddyBubblesEnabled);
        }
    }

    // ---- positions and lists ---------------------------------------------------

    [Fact]
    public void APositionRoundTripsAndClearingItRemovesOnlyThatKey()
    {
        HatchAISettings.SetOrbPosition("buddy", -100, 250);
        HatchAISettings.SetOrbPosition("other", 1, 2);
        Reload();

        Assert.Equal(new HatchAISettings.OrbPlacement(-100, 250), HatchAISettings.OrbPositionFor("buddy"));

        HatchAISettings.ClearOrbPosition("buddy");
        HatchAISettings.ClearOrbPosition("never-set");
        Reload();

        Assert.Null(HatchAISettings.OrbPositionFor("buddy"));
        Assert.NotNull(HatchAISettings.OrbPositionFor("other"));
    }

    // Setting the same position again writes nothing: the buddy window
    // reports its position on every move, and an unchanged one should not
    // cost a disk write.
    [Fact]
    public void SettingAnUnchangedPositionDoesNotWrite()
    {
        HatchAISettings.SetOrbPosition("buddy", 5, 6);
        var before = File.GetLastWriteTimeUtc(SettingsPath);
        File.SetLastWriteTimeUtc(SettingsPath, before.AddMinutes(-5));

        HatchAISettings.SetOrbPosition("buddy", 5, 6);

        Assert.Equal(before.AddMinutes(-5), File.GetLastWriteTimeUtc(SettingsPath));
    }

    // A position map that is some other shape is replaced by a real one the
    // first time a position is written; there is one key and the buddy's
    // position has to live in it.
    [Fact]
    public void APositionMapOfTheWrongShapeIsReplacedOnWrite()
    {
        WriteSettings("""{ "orbPositions": [ 1, 2 ] }""");
        Reload();

        Assert.Null(HatchAISettings.OrbPositionFor("buddy"));
        HatchAISettings.ClearOrbPosition("buddy");
        HatchAISettings.SetOrbPosition("buddy", 3, 4);

        Assert.Equal(new HatchAISettings.OrbPlacement(3, 4), HatchAISettings.OrbPositionFor("buddy"));
    }

    [Fact]
    public void ProfileDirsAndCodexHomesAddOnceAndRemove()
    {
        HatchAISettings.AddClaudeCodeProfileDir(".claude-work");
        HatchAISettings.AddClaudeCodeProfileDir(".claude-work");
        HatchAISettings.AddCodexHome(".codex-work");
        Reload();

        Assert.Equal(new[] { ".claude-work" }, HatchAISettings.ClaudeCodeProfileDirs);
        Assert.Equal(new[] { ".codex-work" }, HatchAISettings.CodexHomes);

        HatchAISettings.RemoveClaudeCodeProfileDir(".claude-work");
        HatchAISettings.RemoveCodexHome(".codex-work");
        HatchAISettings.RemoveCodexHome("never-added");
        Reload();

        Assert.Empty(HatchAISettings.ClaudeCodeProfileDirs);
        Assert.Empty(HatchAISettings.CodexHomes);
    }

    // Removing from a list that is not a list is a no-op, and adding to one
    // replaces it with a real list.
    [Fact]
    public void AListOfTheWrongShapeIsReplacedOnlyWhenWrittenTo()
    {
        WriteSettings("""{ "codexHomes": "not-a-list" }""");
        Reload();

        HatchAISettings.RemoveCodexHome("x");
        Assert.Equal("not-a-list", ReadSettings()["codexHomes"]!.GetValue<string>());

        HatchAISettings.AddCodexHome(".codex-2");
        Assert.Equal(new[] { ".codex-2" }, HatchAISettings.CodexHomes);
    }

    // ---- import from Claude Buddy ------------------------------------------------

    private string WriteClaudeBuddy(string json)
    {
        var path = Path.Combine(_dir, "claude-buddy-settings.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private const string ClaudeBuddyWithABuddy = """
        {
          "showOrbs": true,
          "orbLifetimeMinutes": 5,
          "orbPositions": { "buddy": { "x": 1200, "y": 640 }, "some-session": { "x": 1, "y": 1 } },
          "claudeCodeProfileDirs": [ ".claude-work" ],
          "codexHomes": [ ".codex-work", 5 ],
          "buddy": { "uuid": "imported-uuid", "rebirths": 1, "tokens": 900, "nested": { "keep": true } }
        }
        """;

    [Fact]
    public void ImportCopiesTheBuddyItsPositionAndItsFoldersAndNothingElse()
    {
        HatchAISettings.AddClaudeCodeProfileDir(".claude-mine");
        var source = WriteClaudeBuddy(ClaudeBuddyWithABuddy);
        var before = File.ReadAllBytes(source);

        var outcome = HatchAISettings.ImportFromClaudeBuddy(source);

        Assert.Equal(HatchAISettings.ImportOutcome.Imported, outcome);
        var root = ReadSettings();
        Assert.Equal("imported-uuid", root["buddy"]!["uuid"]!.GetValue<string>());
        Assert.True(root["buddy"]!["nested"]!["keep"]!.GetValue<bool>());
        Assert.Equal(new HatchAISettings.OrbPlacement(1200, 640), HatchAISettings.OrbPositionFor("buddy"));
        Assert.Null(HatchAISettings.OrbPositionFor("some-session"));
        Assert.Equal(new[] { ".claude-mine", ".claude-work" }, HatchAISettings.ClaudeCodeProfileDirs);
        Assert.Equal(new[] { ".codex-work" }, HatchAISettings.CodexHomes);
        Assert.False(root.ContainsKey("showOrbs"));
        Assert.False(root.ContainsKey("orbLifetimeMinutes"));

        // Claude Buddy's file is never written.
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    // Import replaces a buddy this file already holds — the Settings window
    // asks first — and running it twice does not duplicate the folders.
    [Fact]
    public void ImportReplacesAnExistingBuddyAndIsIdempotentForFolders()
    {
        HatchAISettings.UpdateBuddy(b => b["uuid"] = "old");
        var source = WriteClaudeBuddy(ClaudeBuddyWithABuddy);

        HatchAISettings.ImportFromClaudeBuddy(source);
        HatchAISettings.ImportFromClaudeBuddy(source);

        Assert.Equal("imported-uuid", HatchAISettings.BuddyObject()!["uuid"]!.GetValue<string>());
        Assert.Equal(new[] { ".claude-work" }, HatchAISettings.ClaudeCodeProfileDirs);
    }

    // A Claude Buddy position that is not a pair of numbers is not copied,
    // and the position this file already had is kept.
    [Fact]
    public void AnImportedBuddyWithoutAPositionLeavesTheCurrentPositionAlone()
    {
        HatchAISettings.SetOrbPosition("buddy", 7, 8);
        var source = WriteClaudeBuddy("""{ "buddy": { "uuid": "u" }, "orbPositions": { "buddy": { "x": "?" } } }""");

        Assert.Equal(HatchAISettings.ImportOutcome.Imported, HatchAISettings.ImportFromClaudeBuddy(source));
        Assert.Equal(new HatchAISettings.OrbPlacement(7, 8), HatchAISettings.OrbPositionFor("buddy"));
    }

    [Fact]
    public void AnImportedPositionIntoAFileWithNoPositionsCreatesTheMap()
    {
        var source = WriteClaudeBuddy("""{ "buddy": { "uuid": "u" }, "orbPositions": { "buddy": { "x": 3, "y": 4 } } }""");

        HatchAISettings.ImportFromClaudeBuddy(source);

        Assert.Equal(new HatchAISettings.OrbPlacement(3, 4), HatchAISettings.OrbPositionFor("buddy"));
    }

    [Fact]
    public void ImportOfAClaudeBuddyWithNoBuddyChangesNothing()
    {
        var source = WriteClaudeBuddy("""{ "claudeCodeProfileDirs": [ ".claude-work" ] }""");

        Assert.Equal(HatchAISettings.ImportOutcome.NoBuddy, HatchAISettings.ImportFromClaudeBuddy(source));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void ImportOfAMissingOrUnreadableFileSaysSo()
    {
        Assert.Equal(HatchAISettings.ImportOutcome.NotFound,
            HatchAISettings.ImportFromClaudeBuddy(Path.Combine(_dir, "nope.json")));
        Assert.Equal(HatchAISettings.ImportOutcome.Unreadable,
            HatchAISettings.ImportFromClaudeBuddy(WriteClaudeBuddy("{ nope")));
        Assert.Equal(HatchAISettings.ImportOutcome.Unreadable,
            HatchAISettings.ImportFromClaudeBuddy(WriteClaudeBuddy("[1]")));
        Assert.False(File.Exists(SettingsPath));
    }

    // Claude Buddy may be saving at the very moment the import reads: its
    // file is opened shared for writing and deleting, so an open writer
    // neither blocks the import nor is blocked by it.
    [Fact]
    public void ImportReadsWhileClaudeBuddyHoldsItsFileOpen()
    {
        var source = WriteClaudeBuddy(ClaudeBuddyWithABuddy);
        using var claudeBuddy = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(HatchAISettings.ImportOutcome.Imported, HatchAISettings.ImportFromClaudeBuddy(source));
    }
}
