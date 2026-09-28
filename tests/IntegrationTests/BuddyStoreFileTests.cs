using System.Text.Json.Nodes;
using Xunit;

namespace HatchAI.Tests;

// A view that draws nothing, for driving the real controller against the
// real store and scanner. The UI suite's FakeBuddyView records calls; nothing
// here needs them.
internal sealed class QuietView : IBuddyView
{
#pragma warning disable CS0067 // raised by nothing here; the controller only subscribes
    public event Action? HideRequested;
#pragma warning restore CS0067
    public event Action? RebirthRequested;
    public void Show(BuddyGenome genome, BuddyState state) { }
    public void UpdateState(BuddyGenome genome, BuddyState state) { }
    public void Hide() { }
    public void ShowBubble(string text) { }
    public void HideBubble() { }
    public void RaiseRebirth() => RebirthRequested?.Invoke();
}

// The buddy's persistence through a real settings.json on disk (CB-195): the
// two preference bools, and the top-level "buddy" object that BuddyStore
// edits in place.
//
// The case that matters most is the unknown nested key. A sub-key a newer
// build writes under "buddy" survives a save by this build only because the
// object is edited in place rather than rebuilt — and nothing but a round
// trip through the real file can show that it is. (In Claude Buddy only the
// top level had _unknownKeys; HatchAISettings holds the whole file as a
// JsonObject, so the neighbour cases below now pin every depth.)
[Collection("Settings")]
public class BuddyStoreFileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");

    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hatchai-buddystore-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", dir);
        HatchAISettings.ReloadForTests();
    }

    // Claude Buddy asserted its own typed turnFinishedSound setting kept its
    // value beside a garbage buddy switch. HatchAI has no such setting, so the
    // same key is now an unknown neighbour, and what is asserted is the thing
    // that matters for HatchAI: a save leaves it exactly as it was.
    private static void AssertTheUnknownNeighbourSurvivesASave(string dir)
    {
        HatchAISettings.BuddyAiBubblesEnabled = HatchAISettings.BuddyAiBubblesEnabled;
        Assert.Equal("Hero", ReadFile(dir)["turnFinishedSound"]!.GetValue<string>());
    }

    private static JsonObject ReadFile(string dir) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!.AsObject();

    // --- buddyEnabled / buddyBubblesEnabled ---

    [Fact]
    public void BothBuddySwitchesDefaultToOn()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.True(HatchAISettings.BuddyEnabled);
        Assert.True(HatchAISettings.BuddyBubblesEnabled);
    }

    [Fact]
    public void BothBuddySwitchesRoundTripThroughTheFile()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        HatchAISettings.BuddyEnabled = false;
        HatchAISettings.BuddyBubblesEnabled = false;

        var root = ReadFile(dir);
        Assert.False(root["buddyEnabled"]!.GetValue<bool>());
        Assert.False(root["buddyBubblesEnabled"]!.GetValue<bool>());

        PointSettingsAt(dir);
        Assert.False(HatchAISettings.BuddyEnabled);
        Assert.False(HatchAISettings.BuddyBubblesEnabled);
    }

    [Fact]
    public void AGarbageBuddySwitchCostsOnlyItself()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"turnFinishedSound\": \"Hero\", \"buddyEnabled\": \"no\", \"buddyBubblesEnabled\": 0 }");

        PointSettingsAt(dir);

        AssertTheUnknownNeighbourSurvivesASave(dir);
        Assert.True(HatchAISettings.BuddyEnabled);
        Assert.True(HatchAISettings.BuddyBubblesEnabled);
    }

    // --- buddyAiBubblesEnabled (CB-202) ---
    //
    // The one buddy switch that defaults off: turning it on spends the user's
    // usage and sends part of their prompt to a model, so a missing key, and a
    // key that fails to parse, must both read as off.

    [Fact]
    public void AiBubblesDefaultToOff()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.False(HatchAISettings.BuddyAiBubblesEnabled);
    }

    [Fact]
    public void AiBubblesRoundTripThroughTheFile()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        HatchAISettings.BuddyAiBubblesEnabled = true;
        Assert.True(ReadFile(dir)["buddyAiBubblesEnabled"]!.GetValue<bool>());

        PointSettingsAt(dir);
        Assert.True(HatchAISettings.BuddyAiBubblesEnabled);

        HatchAISettings.BuddyAiBubblesEnabled = false;
        PointSettingsAt(dir);
        Assert.False(HatchAISettings.BuddyAiBubblesEnabled);
    }

    [Fact]
    public void AGarbageAiBubblesSwitchReadsAsOff()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"turnFinishedSound\": \"Hero\", \"buddyAiBubblesEnabled\": \"yes\" }");

        PointSettingsAt(dir);

        AssertTheUnknownNeighbourSurvivesASave(dir);
        Assert.False(HatchAISettings.BuddyAiBubblesEnabled);
    }

    // --- buddyIdleBubblesEnabled / buddyBubbleLogEnabled (CB-202) ---
    //
    // Opposite defaults on purpose: idle chatter is off until asked for, the
    // local log is on until switched off. A missing key and a garbage key must
    // both read as the default, and neither may cost another setting.

    [Fact]
    public void IdleBubblesDefaultOffAndTheBubbleLogDefaultsOn()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.False(HatchAISettings.BuddyIdleBubblesEnabled);
        Assert.True(HatchAISettings.BuddyBubbleLogEnabled);
    }

    [Fact]
    public void IdleBubblesAndTheBubbleLogRoundTripThroughTheFile()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        HatchAISettings.BuddyIdleBubblesEnabled = true;
        HatchAISettings.BuddyBubbleLogEnabled = false;
        var root = ReadFile(dir);
        Assert.True(root["buddyIdleBubblesEnabled"]!.GetValue<bool>());
        Assert.False(root["buddyBubbleLogEnabled"]!.GetValue<bool>());

        PointSettingsAt(dir);
        Assert.True(HatchAISettings.BuddyIdleBubblesEnabled);
        Assert.False(HatchAISettings.BuddyBubbleLogEnabled);

        HatchAISettings.BuddyIdleBubblesEnabled = false;
        HatchAISettings.BuddyBubbleLogEnabled = true;
        PointSettingsAt(dir);
        Assert.False(HatchAISettings.BuddyIdleBubblesEnabled);
        Assert.True(HatchAISettings.BuddyBubbleLogEnabled);
    }

    [Fact]
    public void GarbageIdleAndLogSwitchesReadAsTheirDefaults()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"turnFinishedSound\": \"Hero\", \"buddyIdleBubblesEnabled\": \"yes\", \"buddyBubbleLogEnabled\": 0 }");

        PointSettingsAt(dir);

        AssertTheUnknownNeighbourSurvivesASave(dir);
        Assert.False(HatchAISettings.BuddyIdleBubblesEnabled);
        Assert.True(HatchAISettings.BuddyBubbleLogEnabled);
    }

    // A save neither duplicates the buddy keys nor drops a key a newer build
    // wrote beside them.
    [Fact]
    public void TheNewKeysAreKnownAndAnUnknownNeighbourSurvivesASave()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"buddyIdleBubblesEnabled\": true, \"buddyBubbleLogEnabled\": false, \"someFutureKey\": { \"x\": 1 } }");

        PointSettingsAt(dir);
        HatchAISettings.BuddyAiBubblesEnabled = false; // any save

        var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
        var root = ReadFile(dir);
        Assert.Equal(1, root["someFutureKey"]!["x"]!.GetValue<int>());
        Assert.True(root["buddyIdleBubblesEnabled"]!.GetValue<bool>());
        Assert.False(root["buddyBubbleLogEnabled"]!.GetValue<bool>());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"buddyIdleBubblesEnabled\""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"buddyBubbleLogEnabled\""));
    }

    // --- the buddy object ---

    [Fact]
    public void NoBuddyObjectLoadsAsNoBuddyAndSavingNothingWritesNoKey()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Null(new BuddyStore().Load());

        HatchAISettings.BuddyEnabled = true;
        Assert.False(ReadFile(dir).ContainsKey("buddy"));
    }

    [Fact]
    public void ABuddyRoundTripsThroughARealFileWithUnknownKeysAtBothLevelsIntact()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {
              "aTopLevelKeyFromTheFuture": { "x": 1 },
              "buddy": {
                "uuid": "u-1",
                "rebirths": 1,
                "countingSince": "2026-09-01T00:00:00.0000000+00:00",
                "tokens": 500,
                "aNestedKeyFromTheFuture": [ 1, 2, 3 ],
                "ledger": { "files": { "/t.jsonl": { "offset": 10, "crc": "beef" } } }
              }
            }
            """);
        PointSettingsAt(dir);

        var store = new BuddyStore(() => Now);
        var loaded = store.Load()!;
        Assert.Equal("u-1", loaded.Uuid);
        Assert.Equal(500, loaded.Tokens);
        Assert.Equal(10, loaded.Cursors["/t.jsonl"].Offset);

        store.Save(loaded with
        {
            Tokens = 700,
            LifetimeTokens = 700,
            Cursors = new Dictionary<string, LedgerCursor> { ["/t.jsonl"] = new(90, Now, "m", 3, 0) },
            RecentMessageIds = new[] { "m" }
        });

        var root = ReadFile(dir);
        Assert.Equal(1, root["aTopLevelKeyFromTheFuture"]!["x"]!.GetValue<int>());
        var buddy = root["buddy"]!.AsObject();
        Assert.Equal(3, buddy["aNestedKeyFromTheFuture"]!.AsArray().Count);
        Assert.Equal("beef", buddy["ledger"]!["files"]!["/t.jsonl"]!["crc"]!.GetValue<string>());

        // A fresh process: the settings class re-reads the file.
        PointSettingsAt(dir);
        var again = new BuddyStore().Load()!;
        Assert.Equal(700, again.Tokens);
        Assert.Equal(new LedgerCursor(90, Now, "m", 3, 0), again.Cursors["/t.jsonl"]);
        Assert.Equal(new[] { "m" }, again.RecentMessageIds);

        // And a save of an unrelated setting by this build still carries both.
        HatchAISettings.BuddyBubblesEnabled = false;
        root = ReadFile(dir);
        Assert.True(root.ContainsKey("aTopLevelKeyFromTheFuture"));
        Assert.True(root["buddy"]!.AsObject().ContainsKey("aNestedKeyFromTheFuture"));
    }

    private sealed class NoLedger : IBuddyLedgerSource
    {
        public LedgerScanResult Scan(BuddyState state, IReadOnlyList<string> livePaths, bool discover) =>
            LedgerScanResult.Empty;
    }

    // QA on CB-195: a buddy object whose uuid is blank but which still holds
    // a history, a lifetime total and cursors. Load used to answer "no
    // buddy", so the controller hatched a fresh one over it — lifetime and
    // rebirths written back as zero — and, because Save appends history only
    // past the length already on disk, the next rebirth's retired buddy was
    // never written at all. Driven through the real controller and the real
    // file, since the loss is in how the two meet.
    [Fact]
    public void ABlankUuidIsRepairedInPlaceAndNothingElseIsLost()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {
              "buddy": {
                "uuid": "   ",
                "rebirths": 2,
                "countingSince": "2026-09-01T00:00:00.0000000+00:00",
                "tokens": 43000,
                "lifetimeTokens": 130000,
                "history": [
                  { "rebirth": 0, "name": "First", "species": "Duck", "tokens": 42500 },
                  { "rebirth": 1, "name": "Second", "species": "Cat", "tokens": 44500 }
                ],
                "ledger": { "files": { "/t.jsonl": { "offset": 10 } }, "recentIds": [ "m1" ] }
              }
            }
            """);
        PointSettingsAt(dir);

        var view = new QuietView();
        var controller = new BuddyController(view, new BuddyStore(() => Now), new NoLedger(), () => true, () => true, () => Now);
        controller.Reapply();

        var state = controller.State!;
        Assert.False(string.IsNullOrWhiteSpace(state.Uuid));
        Assert.Equal(2, state.Rebirths);
        Assert.Equal(43_000, state.Tokens);
        Assert.Equal(130_000, state.LifetimeTokens);
        Assert.Equal(new[] { "First", "Second" }, state.History.Select(h => h.Name));
        Assert.Equal(10, state.Cursors["/t.jsonl"].Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z"), state.CountingSince);

        // Written at once, so a crash now cannot roll a different buddy.
        Assert.Equal(state.Uuid, ReadFile(dir)["buddy"]!["uuid"]!.GetValue<string>());
        PointSettingsAt(dir);
        Assert.Equal(state.Uuid, new BuddyStore().Load()!.Uuid);

        // 43,000 is past the rebirth threshold (42,500 since CB-195's egg),
        // so the rebirth below is accepted rather than quietly refused.
        view.RaiseRebirth();

        var buddy = ReadFile(dir)["buddy"]!;
        Assert.Equal(3, buddy["history"]!.AsArray().Count);
        Assert.Equal(3, buddy["rebirths"]!.GetValue<int>());
        Assert.Equal(130_000, buddy["lifetimeTokens"]!.GetValue<long>());
        Assert.Equal(43_000, buddy["history"]![2]!["tokens"]!.GetValue<long>());
        controller.Dispose();
    }

    [Fact]
    public void ABuddyKeyThatIsNotAnObjectIsKeptUntilABuddyReplacesIt()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"buddy\": [ \"a newer shape\" ] }");
        PointSettingsAt(dir);

        var store = new BuddyStore();
        Assert.Null(store.Load());
        Assert.Null(HatchAISettings.BuddyObject());

        HatchAISettings.BuddyEnabled = true;
        Assert.IsType<JsonArray>(ReadFile(dir)["buddy"]);

        store.Save(BuddyState.Hatch("u-2", Now));
        Assert.Equal("u-2", ReadFile(dir)["buddy"]!["uuid"]!.GetValue<string>());
    }

    [Fact]
    public void BuddyObjectIsADetachedCopy()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        new BuddyStore().Save(BuddyState.Hatch("u-3", Now));

        var copy = HatchAISettings.BuddyObject()!;
        copy["uuid"] = "tampered";

        Assert.Equal("u-3", new BuddyStore().Load()!.Uuid);
    }
}
