using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace HatchAI.Tests;

// The two places a person turns the buddy on and off — the Buddy section of
// the settings window and the tray's "Show buddy" — and that they agree, since
// they write the same setting and tell the same controller.
//
// The controller here is real, over a FakeBuddyView, because what the switch
// has to achieve is the view being shown or hidden, not a setting being
// written. BuddyController.Instance is what the production callers reach it
// through, so each test installs one and removes it again.
[Collection("Settings")]
public class BuddySettingsUiTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static ToggleButton SwitchIn(Control row) =>
        row.GetLogicalDescendants().OfType<ToggleButton>().Single();

    // Runs `body` with a live controller installed and both settings restored
    // afterwards, since they are process-wide and other tests read them.
    private static void WithController(Action<FakeBuddyView> body)
    {
        var buddyWas = HatchAISettings.BuddyEnabled;
        var bubblesWas = HatchAISettings.BuddyBubblesEnabled;
        var view = new FakeBuddyView();
        var store = new FakeBuddyStore { Stored = BuddyState.Hatch("u", DateTimeOffset.UtcNow) };
        var controller = new BuddyController(
            view, store, new FakeLedgerSource(),
            () => HatchAISettings.BuddyEnabled, () => HatchAISettings.BuddyBubblesEnabled,
            rules: BuddyControllerTests.FakeRules);
        try
        {
            BuddyController.Instance = controller;
            body(view);
        }
        finally
        {
            controller.Dispose();
            BuddyController.Instance = null;
            HatchAISettings.BuddyEnabled = buddyWas;
            HatchAISettings.BuddyBubblesEnabled = bubblesWas;
        }
    }

    [AvaloniaFact]
    public void TheBuddySectionHasFiveRowsOpeningOnTheSavedSettings()
    {
        var buddyWas = HatchAISettings.BuddyEnabled;
        var bubblesWas = HatchAISettings.BuddyBubblesEnabled;
        try
        {
            HatchAISettings.BuddyEnabled = true;
            HatchAISettings.BuddyBubblesEnabled = false;

            var rows = NewWindow().BuddyRows();

            Assert.Equal(5, rows.Length);
            Assert.Equal(true, SwitchIn(rows[0]).IsChecked);
            Assert.Equal(false, SwitchIn(rows[1]).IsChecked);
        }
        finally
        {
            HatchAISettings.BuddyEnabled = buddyWas;
            HatchAISettings.BuddyBubblesEnabled = bubblesWas;
        }
    }

    // HatchAI's window has no sections to register; what matters is that the
    // buddy's rows come first and the import row after them.
    [AvaloniaFact]
    public void TheWindowShowsTheBuddyRowsThenTheImportRow()
    {
        var window = NewWindow();

        var texts = window.AllRows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>().Prepend(r as TextBlock))
            .OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.True(texts.IndexOf("Show buddy") < texts.IndexOf("Import buddy from Claude Buddy"));
        Assert.Equal("HatchAI Settings", window.Title);
    }

    [AvaloniaFact]
    public void ShowBuddySwitchWritesTheSettingAndTellsTheController()
    {
        WithController(view =>
        {
            HatchAISettings.BuddyEnabled = false;
            var toggle = SwitchIn(NewWindow().BuddyRows()[0]);

            toggle.IsChecked = true;
            Assert.True(HatchAISettings.BuddyEnabled);
            Assert.Equal(new[] { "show" }, view.Calls.Take(1));
            Assert.Contains("show", view.Calls);

            view.Calls.Clear();
            toggle.IsChecked = false;
            Assert.False(HatchAISettings.BuddyEnabled);
            Assert.Equal(new[] { "hidebubble", "hide" }, view.Calls);
        });
    }

    [AvaloniaFact]
    public void ShowBuddySwitchWithNoRunningControllerOnlyWritesTheSetting()
    {
        var was = HatchAISettings.BuddyEnabled;
        try
        {
            BuddyController.Instance = null;
            HatchAISettings.BuddyEnabled = false;

            SwitchIn(NewWindow().BuddyRows()[0]).IsChecked = true;

            Assert.True(HatchAISettings.BuddyEnabled);
        }
        finally
        {
            HatchAISettings.BuddyEnabled = was;
        }
    }

    [AvaloniaFact]
    public void SpeechBubblesSwitchWritesItsSettingAndLeavesTheBuddyAlone()
    {
        WithController(view =>
        {
            HatchAISettings.BuddyEnabled = false;
            HatchAISettings.BuddyBubblesEnabled = false;
            var toggle = SwitchIn(NewWindow().BuddyRows()[1]);

            toggle.IsChecked = true;
            Assert.True(HatchAISettings.BuddyBubblesEnabled);
            toggle.IsChecked = false;
            Assert.False(HatchAISettings.BuddyBubblesEnabled);

            Assert.Empty(view.Calls);
        });
    }

    [AvaloniaFact]
    public void TheTrayItemFlipsTheSameSettingAndTellsTheController()
    {
        WithController(view =>
        {
            HatchAISettings.BuddyEnabled = false;

            Tray.ToggleBuddyVisible();
            Assert.True(HatchAISettings.BuddyEnabled);
            Assert.Contains("show", view.Calls);

            view.Calls.Clear();
            Tray.ToggleBuddyVisible();
            Assert.False(HatchAISettings.BuddyEnabled);
            Assert.Equal(new[] { "hidebubble", "hide" }, view.Calls);
        });
    }

    [AvaloniaFact]
    public void TheTrayItemWithNoControllerStillFlipsTheSetting()
    {
        var was = HatchAISettings.BuddyEnabled;
        try
        {
            BuddyController.Instance = null;
            HatchAISettings.BuddyEnabled = true;

            Tray.ToggleBuddyVisible();

            Assert.False(HatchAISettings.BuddyEnabled);
        }
        finally
        {
            HatchAISettings.BuddyEnabled = was;
        }
    }

    // Claude Buddy reached into its TrayController's private menu; HatchAI's
    // Tray exposes its items, and is built without a notification-area icon
    // here, since the headless platform has none.
    [AvaloniaFact]
    public void TheTrayMenuCarriesAShowBuddyCheckItemThatFollowsTheSetting()
    {
        var was = HatchAISettings.BuddyEnabled;
        using var tray = new Tray(null, () => null, createIcon: false);
        try
        {
            HatchAISettings.BuddyEnabled = true;
            tray.Refresh();
            var on = tray.Menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Show buddy");
            Assert.Equal(MenuItemToggleType.CheckBox, on.ToggleType);
            Assert.True(on.IsChecked);

            HatchAISettings.BuddyEnabled = false;
            tray.Refresh();
            Assert.False(on.IsChecked);
        }
        finally
        {
            HatchAISettings.BuddyEnabled = was;
        }
    }

    // --- AI speech bubbles (CB-202) -----------------------------------------

    private static TextBlock[] TextsIn(Control row) =>
        row.GetLogicalDescendants().OfType<TextBlock>().ToArray();

    // Runs `body` with the CLI forced found or missing and the three buddy
    // settings restored afterwards.
    private static void WithAiState(bool cliFound, bool ai, bool bubbles, Action body)
    {
        var aiWas = HatchAISettings.BuddyAiBubblesEnabled;
        var bubblesWas = HatchAISettings.BuddyBubblesEnabled;
        var seamWas = SettingsWindow.ClaudeCliFoundForTests;
        try
        {
            SettingsWindow.ClaudeCliFoundForTests = () => cliFound;
            HatchAISettings.BuddyAiBubblesEnabled = ai;
            HatchAISettings.BuddyBubblesEnabled = bubbles;
            body();
        }
        finally
        {
            SettingsWindow.ClaudeCliFoundForTests = seamWas;
            HatchAISettings.BuddyAiBubblesEnabled = aiWas;
            HatchAISettings.BuddyBubblesEnabled = bubblesWas;
        }
    }

    [AvaloniaFact]
    public void TheAiBubblesRowReflectsTheSavedSettingAndCarriesTheDisclosure()
    {
        WithAiState(cliFound: true, ai: true, bubbles: true, () =>
        {
            var row = NewWindow().BuddyRows()[3];

            Assert.Equal(true, SwitchIn(row).IsChecked);
            Assert.True(SwitchIn(row).IsEnabled);
            var texts = TextsIn(row).Select(t => t.Text).ToArray();
            Assert.Contains("AI speech bubbles", texts);
            Assert.Contains(SettingsWindow.AiBubblesDescription, texts);
            // The measured cost since the stats were added (660-672, CB-202).
            Assert.Contains("about 670 tokens each", SettingsWindow.AiBubblesDescription);
            Assert.DoesNotContain("600 tokens", SettingsWindow.AiBubblesDescription);
            Assert.Contains("personalities, species, rarity and five stats", SettingsWindow.AiBubblesDescription);
            Assert.Contains("three bubbles a minute", SettingsWindow.AiBubblesDescription);
            Assert.Contains("Never sent", SettingsWindow.AiBubblesDescription);
            // Which moments are generated, and that the rest cost nothing.
            Assert.Contains("always use built-in lines", SettingsWindow.AiBubblesDescription);
            // Short paragraphs, not one block: what it does, cost, what is
            // sent, what is not.
            Assert.Equal(4, SettingsWindow.AiBubblesDescription.Split("\n\n").Length);
            // Neither note applies: CLI found and bubbles on.
            Assert.DoesNotContain(texts, t => t == SettingsWindow.AiBubblesNeedsBubblesNote
                || t == SettingsWindow.AiBubblesNoCliReason);
        });

        WithAiState(cliFound: true, ai: false, bubbles: true, () =>
            Assert.Equal(false, SwitchIn(NewWindow().BuddyRows()[3]).IsChecked));
    }

    [AvaloniaFact]
    public void TheAiBubblesSwitchWritesItsSettingAndNothingElse()
    {
        WithAiState(cliFound: true, ai: false, bubbles: true, () =>
        {
            var toggle = SwitchIn(NewWindow().BuddyRows()[3]);

            toggle.IsChecked = true;
            Assert.True(HatchAISettings.BuddyAiBubblesEnabled);
            Assert.True(HatchAISettings.BuddyBubblesEnabled);
            toggle.IsChecked = false;
            Assert.False(HatchAISettings.BuddyAiBubblesEnabled);
        });
    }

    [AvaloniaFact]
    public void WithoutTheCliTheAiSwitchIsDisabledWithAReasonAndTheSavedValueSurvives()
    {
        WithAiState(cliFound: false, ai: true, bubbles: true, () =>
        {
            var row = NewWindow().BuddyRows()[3];
            var toggle = SwitchIn(row);

            Assert.False(toggle.IsEnabled);
            // Shown as the user left it, and the stored value is untouched.
            Assert.Equal(true, toggle.IsChecked);
            Assert.True(HatchAISettings.BuddyAiBubblesEnabled);
            Assert.Contains(TextsIn(row), t => t.Text!.Contains(SettingsWindow.AiBubblesNoCliReason));
        });
    }

    [AvaloniaFact]
    public void TheProductionCliCheckIsUsedWhenNoSeamIsSet()
    {
        var aiWas = HatchAISettings.BuddyAiBubblesEnabled;
        var seamWas = SettingsWindow.ClaudeCliFoundForTests;
        try
        {
            SettingsWindow.ClaudeCliFoundForTests = null;
            var toggle = SwitchIn(NewWindow().BuddyRows()[3]);

            // Whatever this machine has, the switch must agree with an
            // uncached ClaudeBinary.Locate().
            Assert.Equal(ClaudeBinary.Locate() is not null, toggle.IsEnabled);
        }
        finally
        {
            SettingsWindow.ClaudeCliFoundForTests = seamWas;
            HatchAISettings.BuddyAiBubblesEnabled = aiWas;
        }
    }

    [AvaloniaFact]
    public void TheAiRowSaysItHasNoEffectWhileSpeechBubblesIsOff()
    {
        WithAiState(cliFound: true, ai: true, bubbles: false, () =>
            Assert.Contains(TextsIn(NewWindow().BuddyRows()[3]),
                t => t.Text == SettingsWindow.AiBubblesNeedsBubblesNote));

        // Both problems at once read together, in one note.
        WithAiState(cliFound: false, ai: false, bubbles: false, () =>
            Assert.Contains(TextsIn(NewWindow().BuddyRows()[3]),
                t => t.Text!.Contains(SettingsWindow.AiBubblesNoCliReason)
                    && t.Text.Contains(SettingsWindow.AiBubblesNeedsBubblesNote)));
    }

    // The disclosure only works if the switch is beside it, so neither the
    // tray nor the buddy's own menu may grow a way to flip it.
    [AvaloniaFact]
    public void NeitherTheTrayNorTheBuddyMenuOffersAnAiBubblesItem()
    {
        var window = new BuddyWindow();
        var menu = window.CardMenuItem.Parent as ItemsControl;
        Assert.NotNull(menu);
        var items = menu!.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(4, items.Length); // card, rebirth, bubbles, hide: nothing added
        foreach (var item in items)
            Assert.DoesNotContain("AI speech", item.Header?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        using var tray = new Tray(window, () => null, createIcon: false);
        foreach (var item in tray.Menu.Items.OfType<NativeMenuItem>())
            Assert.DoesNotContain("AI speech", item.Header ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // --- Idle bubbles and Bubble log (CB-202, after live use) ------------------

    private const int IdleRow = 2;
    private const int LogRow = 4;

    [AvaloniaFact]
    public void TheIdleRowOpensOnTheSettingWritesItAndSaysWhatItCosts()
    {
        var was = HatchAISettings.BuddyIdleBubblesEnabled;
        try
        {
            HatchAISettings.BuddyIdleBubblesEnabled = false;
            var row = NewWindow().BuddyRows()[IdleRow];
            var texts = TextsIn(row).Select(t => t.Text).ToArray();
            Assert.Contains("Idle bubbles", texts);
            Assert.Contains(SettingsWindow.IdleBubblesDescription, texts);
            Assert.Contains("15 minutes", SettingsWindow.IdleBubblesDescription);
            Assert.Contains("never AI tokens", SettingsWindow.IdleBubblesDescription);
            Assert.Contains("Off by default", SettingsWindow.IdleBubblesDescription);

            var toggle = SwitchIn(row);
            Assert.Equal(false, toggle.IsChecked);
            toggle.IsChecked = true;
            Assert.True(HatchAISettings.BuddyIdleBubblesEnabled);
            Assert.Equal(true, SwitchIn(NewWindow().BuddyRows()[IdleRow]).IsChecked);
            toggle.IsChecked = false;
            Assert.False(HatchAISettings.BuddyIdleBubblesEnabled);
        }
        finally
        {
            HatchAISettings.BuddyIdleBubblesEnabled = was;
        }
    }

    [AvaloniaFact]
    public void TheLogRowSaysWhatItHoldsAndShowsWhereItIs()
    {
        var was = HatchAISettings.BuddyBubbleLogEnabled;
        try
        {
            HatchAISettings.BuddyBubbleLogEnabled = true;
            var row = NewWindow().BuddyRows()[LogRow];
            var texts = TextsIn(row).Select(t => t.Text).ToArray();

            Assert.Contains("Bubble log", texts);
            Assert.Contains(SettingsWindow.BubbleLogDescription, texts);
            Assert.Contains("the text shown", SettingsWindow.BubbleLogDescription);
            Assert.Contains("can echo what you were working on", SettingsWindow.BubbleLogDescription);
            Assert.Contains("Stays on this computer", SettingsWindow.BubbleLogDescription);
            Assert.Contains("512 KB", SettingsWindow.BubbleLogDescription);

            // The full path, as the file log will use it, selectable.
            var path = row.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
            Assert.Equal(BubbleLogFile.DefaultPath, path.Text);
            Assert.EndsWith("bubble-log.jsonl", path.Text);

            var toggle = SwitchIn(row);
            Assert.Equal(true, toggle.IsChecked);
            toggle.IsChecked = false;
            Assert.False(HatchAISettings.BuddyBubbleLogEnabled);
            Assert.Equal(false, SwitchIn(NewWindow().BuddyRows()[LogRow]).IsChecked);
        }
        finally
        {
            HatchAISettings.BuddyBubbleLogEnabled = was;
        }
    }

    // The button opens settings.json's folder — through the seam, so no
    // Explorer or Finder window appears on the machine running the suite.
    [AvaloniaFact]
    public void OpenFolderRevealsTheLogsFolder()
    {
        var seamWas = SettingsWindow.RevealFolderForTests;
        var opened = new List<string>();
        try
        {
            SettingsWindow.RevealFolderForTests = opened.Add;
            var button = NewWindow().BuddyRows()[LogRow].GetLogicalDescendants().OfType<Button>()
                .Single(b => (b.Content as string) == "Open folder");

            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(new[] { BubbleLogFile.DefaultDirectory }, opened);
        }
        finally
        {
            SettingsWindow.RevealFolderForTests = seamWas;
        }
    }
}
