using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;

namespace HatchAI.Tests;

// The pieces HatchAI added around the ported buddy: the tray, the settings
// window's import row, BuddyController.Reload (which the import needs), and
// the app's icons. Real windows and a real controller over fakes, in the
// headless platform; the tray is built without its notification-area icon,
// which the headless platform does not have.
[Collection("Settings")]
public class HatchAIShellTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hatchai-shell-" + Guid.NewGuid().ToString("N"));
    private readonly string? _settingsWas = Environment.GetEnvironmentVariable("HATCHAI_SETTINGS_DIR");

    public HatchAIShellTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", _dir);
        HatchAISettings.ReloadForTests();
    }

    public void Dispose()
    {
        BuddyController.Instance = null;
        Tray.Instance = null;
        SettingsWindow.ClaudeBuddySettingsPathForTests = null;
        Environment.SetEnvironmentVariable("HATCHAI_SETTINGS_DIR", _settingsWas);
        HatchAISettings.ReloadForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static BuddyWindow RealWindow() => new(() => T0, animate: false)
    {
        WorkAreaAt = _ => new PixelRect(0, 0, 1920, 1040)
    };

    private static void Click(NativeMenuItem item) =>
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static BuddyController Controller(IBuddyView view, IBuddyStore store, IBuddyLedgerSource? ledger = null,
        IBubbleGenerator? generator = null, Func<bool>? ai = null) =>
        new(view, store, ledger ?? new FakeLedgerSource(),
            () => HatchAISettings.BuddyEnabled, () => HatchAISettings.BuddyBubblesEnabled, () => T0,
            BuddyControllerTests.FakeRules, generator, ai);

    // ---- the tray -------------------------------------------------------------

    [AvaloniaFact]
    public void TheTrayMenuHasTheBuddysItemsInOrder()
    {
        using var tray = new Tray(null, () => null, createIcon: false);

        var headers = tray.Menu.Items.Select(i => i is NativeMenuItemSeparator ? "—" : ((NativeMenuItem)i).Header).ToArray();

        Assert.Equal(new[] { "Show buddy", "Open card", "Rebirth…", "—", "Speech bubbles", "Settings…", "—", "Quit HatchAI" }, headers);
        Assert.Equal(MenuItemToggleType.CheckBox, tray.BubblesItem.ToggleType);
    }

    // Rebirth is offered only once it is earned, and neither card item works
    // while the buddy is hidden or not yet loaded.
    [AvaloniaFact]
    public void TheCardAndRebirthItemsFollowTheBuddy()
    {
        var store = new FakeBuddyStore { Stored = BuddyState.Hatch("u", T0) with { Tokens = 100 } };
        var window = RealWindow();
        var controller = Controller(new FakeBuddyView(), store);
        using var tray = new Tray(window, () => controller, createIcon: false);

        HatchAISettings.BuddyEnabled = true;
        tray.Refresh();
        Assert.False(tray.OpenCardItem.IsEnabled); // no state loaded yet

        controller.Reapply();
        tray.Refresh();
        Assert.True(tray.OpenCardItem.IsEnabled);
        Assert.False(tray.RebirthItem.IsEnabled);

        store.Stored = store.Stored! with { Tokens = 50_000 };
        controller.Reload();
        tray.Refresh();
        Assert.True(tray.RebirthItem.IsEnabled);

        HatchAISettings.BuddyEnabled = false;
        tray.Refresh();
        Assert.False(tray.OpenCardItem.IsEnabled);
        Assert.False(tray.RebirthItem.IsEnabled);
        controller.Dispose();
    }

    [AvaloniaFact]
    public void WithNoBuddyWindowTheCardItemsStayOff()
    {
        var controller = Controller(new FakeBuddyView(), new FakeBuddyStore());
        controller.Reapply();
        using var tray = new Tray(null, () => controller, createIcon: false);

        Assert.False(tray.OpenCardItem.IsEnabled);
        Click(tray.OpenCardItem);
        Click(tray.RebirthItem);
        controller.Dispose();
    }

    // The tray's Rebirth opens the card on its rebirth confirmation and does not
    // rebirth anything itself: the card's confirmation stays the only path.
    [AvaloniaFact]
    public void RebirthOpensTheCardOnTheOfferAndRebirthsNothing()
    {
        HatchAISettings.BuddyEnabled = true;
        var window = RealWindow();
        var state = BuddyState.Hatch("u", T0) with { Tokens = 50_000 };
        window.ShowBuddy(BuddyControllerTests.Genome, state);
        var rebirths = 0;
        window.RebirthRequested += () => rebirths++;
        using var tray = new Tray(window, () => null, createIcon: false);

        Click(tray.RebirthItem);

        Assert.NotNull(window.Card);
        Assert.True(window.Card!.IsVisible);
        Assert.True(window.Card.IsConfirmingRebirth);
        Assert.Equal(0, rebirths);
        window.Close();
    }

    [AvaloniaFact]
    public void OpenCardOpensTheCardWithoutTheOffer()
    {
        var window = RealWindow();
        window.ShowBuddy(BuddyControllerTests.Genome, BuddyState.Hatch("u", T0) with { Tokens = 50_000 });
        using var tray = new Tray(window, () => null, createIcon: false);

        Click(tray.OpenCardItem);

        Assert.True(window.Card!.IsVisible);
        Assert.False(window.Card.IsConfirmingRebirth);
        window.Close();
    }

    [AvaloniaFact]
    public void ShowBuddyFlipsTheSettingTellsTheControllerAndRefreshes()
    {
        var view = new FakeBuddyView();
        var controller = Controller(view, new FakeBuddyStore());
        BuddyController.Instance = controller;
        using var tray = new Tray(null, () => controller, createIcon: false);
        Tray.Instance = tray;
        HatchAISettings.BuddyEnabled = false;
        tray.Refresh();

        Click(tray.ShowBuddyItem);

        Assert.True(HatchAISettings.BuddyEnabled);
        Assert.True(tray.ShowBuddyItem.IsChecked);
        Assert.Contains("show", view.Calls);
        controller.Dispose();
    }

    // The tray's Speech bubbles is the same preference as the buddy menu's
    // item: flipping it off takes down a bubble already showing and keeps the
    // buddy menu's check in step.
    [AvaloniaFact]
    public void SpeechBubblesFlipsThePreferenceAndKeepsTheBuddyMenuInStep()
    {
        HatchAISettings.BuddyEnabled = true;
        HatchAISettings.BuddyBubblesEnabled = true;
        var window = RealWindow();
        window.ShowBuddy(BuddyControllerTests.Genome, BuddyState.Hatch("u", T0));
        window.ShowBubble("hello");
        Assert.True(window.Bubble!.IsVisible);
        using var tray = new Tray(window, () => null, createIcon: false);
        Tray.Instance = tray;

        Click(tray.BubblesItem);

        Assert.False(HatchAISettings.BuddyBubblesEnabled);
        Assert.False(tray.BubblesItem.IsChecked);
        Assert.False(window.BubblesMenuItem.IsChecked);
        Assert.False(window.Bubble.IsVisible);

        Click(tray.BubblesItem);
        Assert.True(HatchAISettings.BuddyBubblesEnabled);
        Assert.True(window.BubblesMenuItem.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public void ToggleBubblesWithNoTrayInstalledOnlyWritesThePreference()
    {
        Tray.Instance = null;
        HatchAISettings.BuddyBubblesEnabled = true;

        Tray.ToggleBubbles();

        Assert.False(HatchAISettings.BuddyBubblesEnabled);
    }

    [AvaloniaFact]
    public void SettingsAndQuitGoThroughTheirSeams()
    {
        using var tray = new Tray(null, () => null, createIcon: false);
        var settings = 0;
        var quit = 0;
        tray.OpenSettings = () => settings++;
        tray.Quit = () => quit++;

        Click(tray.SettingsItem);
        Click(tray.QuitItem);

        Assert.Equal(1, settings);
        Assert.Equal(1, quit);
    }

    [AvaloniaFact]
    public void DisposingTheInstalledTrayUninstallsIt()
    {
        var tray = new Tray(null, () => null, createIcon: false);
        Tray.Instance = tray;
        var other = new Tray(null, () => null, createIcon: false);

        other.Dispose();
        Assert.Same(tray, Tray.Instance);

        tray.Dispose();
        Assert.Null(Tray.Instance);
    }

    // The default controller source is the running app's instance.
    [AvaloniaFact]
    public void ByDefaultTheTrayAsksTheRunningController()
    {
        HatchAISettings.BuddyEnabled = true;
        var window = RealWindow();
        var controller = Controller(new FakeBuddyView(), new FakeBuddyStore());
        controller.Reapply();
        BuddyController.Instance = controller;

        using var tray = new Tray(window, createIcon: false);

        Assert.True(tray.OpenCardItem.IsEnabled);
        controller.Dispose();
    }

    // ---- BuddyController.Reload -------------------------------------------------

    [AvaloniaFact]
    public void ReloadWhileShownDropsTheOldBuddyUnsavedAndShowsTheNewOne()
    {
        HatchAISettings.BuddyEnabled = true;
        var view = new FakeBuddyView();
        var store = new FakeBuddyStore { Stored = BuddyState.Hatch("old", T0) };
        var controller = Controller(view, store);
        controller.Reapply();
        view.Calls.Clear();

        store.Stored = BuddyState.Hatch("imported", T0) with { Tokens = 900 };
        controller.Reload();

        Assert.Equal("imported", controller.State!.Uuid);
        Assert.Equal(new[] { "hidebubble", "show" }, view.Calls);
        Assert.Empty(store.Saved); // the old buddy was never written back
        Assert.Equal(900, view.LastState!.Tokens);
        controller.Dispose();
    }

    [AvaloniaFact]
    public void ReloadWhileHiddenShowsNothingAndTheNextShowLoadsTheNewBuddy()
    {
        HatchAISettings.BuddyEnabled = true;
        var view = new FakeBuddyView();
        var store = new FakeBuddyStore { Stored = BuddyState.Hatch("old", T0) };
        var controller = Controller(view, store);
        controller.Reapply();
        HatchAISettings.BuddyEnabled = false;
        controller.Reapply();
        view.Calls.Clear();

        store.Stored = BuddyState.Hatch("imported", T0);
        controller.Reload();
        Assert.Equal(new[] { "hidebubble" }, view.Calls);
        Assert.Null(controller.State);

        HatchAISettings.BuddyEnabled = true;
        controller.Reapply();
        Assert.Equal("imported", controller.State!.Uuid);
        controller.Dispose();
    }

    private static SessionSnapshot S(string id, string state, DateTimeOffset since) =>
        new(id, state, since, SessionSource.ClaudeCode, id, "/w/" + id, "", "");

    // A generated line in flight was about the old buddy: its call is
    // cancelled, and its answer — if it still arrives — is never shown.
    [AvaloniaFact]
    public void ReloadCancelsALineBeingGenerated()
    {
        HatchAISettings.BuddyEnabled = true;
        HatchAISettings.BuddyBubblesEnabled = true;
        var view = new FakeBuddyView();
        var generator = new FakeBubbleGenerator();
        var controller = Controller(view, new FakeBuddyStore(), generator: generator, ai: () => true);
        controller.Reapply();
        controller.OnSnapshots(new[] { S("a", "generating", T0) });
        controller.OnSnapshots(new[] { S("a", "idle", T0) });
        Assert.True(controller.Generating);

        controller.Reload();

        Assert.False(controller.Generating);
        Assert.True(generator.Last.Token.IsCancellationRequested);
        generator.Last.Tcs.SetResult("too late");
        Assert.DoesNotContain("too late", view.Bubbles);
        controller.Dispose();
    }

    // A moment held for later was about the old buddy too, and is dropped.
    [AvaloniaFact]
    public void ReloadDropsAHeldMoment()
    {
        HatchAISettings.BuddyEnabled = true;
        HatchAISettings.BuddyBubblesEnabled = true;
        var view = new FakeBuddyView();
        var now = T0;
        var controller = new BuddyController(view, new FakeBuddyStore(), new FakeLedgerSource(),
            () => true, () => true, () => now, BuddyControllerTests.FakeRules);
        controller.Reapply();
        controller.OnSnapshots(new[] { S("a", "generating", now) });
        controller.OnSnapshots(new[] { S("a", "idle", now) });          // Responded, shown
        controller.OnSnapshots(new[] { S("a", "waiting", now) });       // NeedsAttention, held by the gap
        var shown = view.Bubbles.Count;

        controller.Reload();
        now = now.AddMinutes(5);
        controller.Pump();

        Assert.Equal(shown, view.Bubbles.Count);
        controller.Dispose();
    }

    // A ledger round that was already reading when the buddy was replaced
    // read against the old buddy's cursors; its answer is not applied.
    [AvaloniaFact]
    public async Task ALedgerRoundStartedBeforeReloadIsNotAppliedAfterIt()
    {
        HatchAISettings.BuddyEnabled = true;
        var ledger = new FakeLedgerSource { Tokens = 5_000, Block = new ManualResetEventSlim() };
        var store = new FakeBuddyStore { Stored = BuddyState.Hatch("old", T0) };
        var controller = Controller(new FakeBuddyView(), store, ledger);
        controller.Reapply();

        var round = controller.LedgerRoundAsync();
        Assert.True(ledger.Entered.Wait(TimeSpan.FromSeconds(10)));
        store.Stored = BuddyState.Hatch("imported", T0);
        controller.Reload();
        ledger.Block.Set();
        await round;

        Assert.Equal("imported", controller.State!.Uuid);
        Assert.Equal(0, controller.State.Tokens);
        controller.Dispose();
    }

    // ---- the settings window's import row -----------------------------------

    private string WriteClaudeBuddy(string json)
    {
        var path = Path.Combine(_dir, "claude-buddy.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        SettingsWindow.ClaudeBuddySettingsPathForTests = () => path;
        return path;
    }

    // Two steps: the first click only asks, Cancel leaves everything as it
    // was, and only "Replace and import" imports — after which the running
    // controller holds the imported buddy, not the one it had.
    [AvaloniaFact]
    public void ImportAsksFirstThenReplacesTheRunningBuddy()
    {
        HatchAISettings.BuddyEnabled = true;
        WriteClaudeBuddy("""{ "buddy": { "uuid": "from-claude-buddy", "tokens": 1234, "countingSince": "2026-09-01T00:00:00Z" } }""");
        var view = new FakeBuddyView();
        var controller = Controller(view, new BuddyStore(() => T0));
        controller.Reapply();
        BuddyController.Instance = controller;
        var window = new SettingsWindow();

        Click(window.ImportButton!);
        var confirm = window.ConfirmImportButton!.Parent!.Parent as Control;
        Assert.True(confirm!.IsVisible);
        Click(window.CancelImportButton!);
        Assert.False(confirm.IsVisible);
        Assert.NotEqual("from-claude-buddy", controller.State!.Uuid);

        Click(window.ImportButton!);
        Click(window.ConfirmImportButton!);

        Assert.False(confirm.IsVisible);
        Assert.True(window.ImportStatus!.IsVisible);
        Assert.Equal(SettingsWindow.OutcomeText(HatchAISettings.ImportOutcome.Imported, ""), window.ImportStatus.Text);
        Assert.Equal("from-claude-buddy", controller.State!.Uuid);
        Assert.Equal(1234, controller.State.Tokens);

        // And the controller's next save writes the imported buddy, not the old one.
        controller.Flush();
        controller.OnRebirthRequested();
        Assert.Equal("from-claude-buddy", HatchAISettings.BuddyObject()!["uuid"]!.GetValue<string>());
        controller.Dispose();
    }

    // After an import the buddy window moves to the imported position and
    // the tray re-reads what it allows.
    [AvaloniaFact]
    public void ImportMovesTheBuddyToItsImportedPosition()
    {
        HatchAISettings.BuddyEnabled = true;
        var source = WriteClaudeBuddy("""{ "buddy": { "uuid": "b" }, "orbPositions": { "buddy": { "x": 700, "y": 500 } } }""");
        var window = RealWindow();
        window.ShowBuddy(BuddyControllerTests.Genome, BuddyState.Hatch("a", T0));
        using var tray = new Tray(window, () => null, createIcon: false);
        Tray.Instance = tray;

        SettingsWindow.RunImport(source);

        Assert.Equal(new PixelPoint(700, 500), window.Position);
        window.Close();
    }

    [Theory]
    [InlineData("NotFound", "weren't found at /x.json")]
    [InlineData("NoBuddy", "hasn't hatched a buddy")]
    [InlineData("Unreadable", "couldn't be read")]
    public void EveryImportThatDidNothingSaysNothingChanged(string outcomeName, string says)
    {
        var outcome = Enum.Parse<HatchAISettings.ImportOutcome>(outcomeName);
        var text = SettingsWindow.OutcomeText(outcome, "/x.json");

        Assert.Contains(says, text);
        Assert.Contains("Nothing was changed", text);
    }

    [AvaloniaFact]
    public void AFailedImportLeavesTheRunningBuddyAlone()
    {
        HatchAISettings.BuddyEnabled = true;
        var source = WriteClaudeBuddy("""{ "showOrbs": true }""");
        var controller = Controller(new FakeBuddyView(), new FakeBuddyStore { Stored = BuddyState.Hatch("mine", T0) });
        controller.Reapply();
        BuddyController.Instance = controller;

        var said = SettingsWindow.RunImport(source);

        Assert.Contains("hasn't hatched", said);
        Assert.Equal("mine", controller.State!.Uuid);
        controller.Dispose();
    }

    // Import with no app running around it — no controller, no tray — is
    // just the file operation.
    [AvaloniaFact]
    public void ImportWithNothingRunningStillImports()
    {
        var source = WriteClaudeBuddy("""{ "buddy": { "uuid": "solo" } }""");

        SettingsWindow.RunImport(source);

        Assert.Equal("solo", HatchAISettings.BuddyObject()!["uuid"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public void TheImportRowSaysWhatItCopiesAndThatClaudeBuddyIsOnlyRead()
    {
        Assert.Contains("only read, never changed", SettingsWindow.ImportDescription);
        Assert.Contains("both apps carry the same pet", SettingsWindow.ImportDescription);
        Assert.Contains("replaces the buddy HatchAI has now", SettingsWindow.ImportConfirmText);
    }

    // The production path to Claude Buddy's file is Claude Buddy's own
    // expression; with no seam set, that is what the row would read.
    [AvaloniaFact]
    public void WithNoSeamTheImportReadsClaudeBuddysOwnPath()
    {
        SettingsWindow.ClaudeBuddySettingsPathForTests = null;

        Assert.EndsWith(Path.Combine("ClaudeBuddy", "settings.json"), HatchAISettings.ClaudeBuddySettingsPath);
    }

    [AvaloniaFact]
    public void RebuildReReadsTheSettings()
    {
        HatchAISettings.BuddyBubblesEnabled = true;
        var window = new SettingsWindow();
        HatchAISettings.BuddyBubblesEnabled = false;

        window.Rebuild();

        var toggles = window.AllRows.SelectMany(r => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(r))
            .OfType<ToggleSwitch>().ToArray();
        Assert.Equal(false, toggles[1].IsChecked);
        HatchAISettings.BuddyBubblesEnabled = true;
    }

    // The settings window's two switches that change what the tray shows
    // tell the tray at once rather than waiting for the next scan.
    [AvaloniaFact]
    public void TheSettingsSwitchesRefreshTheTray()
    {
        HatchAISettings.BuddyEnabled = true;
        HatchAISettings.BuddyBubblesEnabled = true;
        using var tray = new Tray(null, () => null, createIcon: false);
        Tray.Instance = tray;
        var rows = new SettingsWindow().BuddyRows();
        ToggleSwitch SwitchOf(Control row) =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(row).OfType<ToggleSwitch>().Single();

        SwitchOf(rows[0]).IsChecked = false;
        SwitchOf(rows[1]).IsChecked = false;

        Assert.False(tray.ShowBuddyItem.IsChecked);
        Assert.False(tray.BubblesItem.IsChecked);
        HatchAISettings.BuddyEnabled = true;
        HatchAISettings.BuddyBubblesEnabled = true;
    }

    // ---- icons -------------------------------------------------------------------

    [AvaloniaFact]
    public void TheEmbeddedIconsLoadAndAMissingOneIsNull()
    {
        Assert.NotNull(App.TrayIcon);
        Assert.NotNull(App.WindowIcon);
        Assert.Null(App.LoadIcon("avares://HatchAI/Assets/not-there.png"));
    }
}
