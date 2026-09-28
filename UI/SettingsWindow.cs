using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HatchAI
{
    // HatchAI's settings: the buddy's five rows from Claude Buddy's settings
    // window, and the one-time import. One window, one column, no sections —
    // Claude Buddy's is four thousand lines because it configures orbs, chat,
    // voices, gateways and profiles, and none of that exists here.
    //
    // The rows, their description text and their test seams are copied from
    // Claude Buddy's SettingsWindow (BuddyRows, AiBubblesRow, BubbleLogRow),
    // so the disclosure a person reads before turning AI bubbles on is word
    // for word the one that was reviewed there. Row and Switch are its helpers
    // without the search and card chrome. Each switch writes the setting and
    // then tells the running controller, because a setting change is not a
    // session change and nothing on the scan path would notice it.
    //
    // No coexistence banner: the owner decided (plan §7.5) to accept a period
    // where Claude Buddy's own buddy and this one both run, since the in-app
    // buddy is being removed from Claude Buddy once this app is proven.
    internal sealed class SettingsWindow : Window
    {
        private static SettingsWindow? _open;

        private readonly StackPanel _rows = new() { Spacing = 2 };

        internal SettingsWindow()
        {
            Title = "HatchAI Settings";
            Width = 520;
            Height = 640;
            MinWidth = 420;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (App.WindowIcon is { } icon) Icon = icon;

            Content = new ScrollViewer
            {
                Content = _rows,
                Padding = new Thickness(0, 8)
            };

            Rebuild();
        }

        // The tray's "Settings…": one window, raised if it is already open.
        [ExcludeFromCodeCoverage(Justification = "Shows a real window; the rows it builds are covered.")]
        internal static void Toggle()
        {
            if (_open is { } existing)
            {
                existing.Activate();
                return;
            }

            _open = new SettingsWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }

        // Re-reads every setting, as opening the window afresh would. After an
        // import, so the rows show what is now on disk.
        internal void Rebuild()
        {
            _rows.Children.Clear();
            _rows.Children.Add(Heading("Buddy"));
            foreach (var row in BuddyRows()) _rows.Children.Add(row);
            _rows.Children.Add(Heading("Sessions"));
            _rows.Children.Add(HooksRow());
            _rows.Children.Add(Heading("Claude Buddy"));
            _rows.Children.Add(ImportRow());
        }

        internal IReadOnlyList<Control> AllRows => _rows.Children.ToList();

        // ---- the buddy's rows (from Claude Buddy) -----------------------------

        internal Control[] BuddyRows() => new[]
        {
            Row("Show buddy",
                Switch(HatchAISettings.BuddyEnabled, value =>
                {
                    HatchAISettings.BuddyEnabled = value;
                    BuddyController.Instance?.Reapply();
                    Tray.Instance?.Refresh();
                }),
                "A small companion that lives on your desktop, reacts to your sessions "
                + "and evolves as you use Claude Code and Codex."),
            Row("Speech bubbles",
                Switch(HatchAISettings.BuddyBubblesEnabled, value =>
                {
                    HatchAISettings.BuddyBubblesEnabled = value;
                    Tray.Instance?.Refresh();
                }),
                "Let the buddy comment when a session starts, finishes or needs you. "
                + "Off, it stays quiet but still grows."),
            Row("Idle bubbles",
                Switch(HatchAISettings.BuddyIdleBubblesEnabled,
                    value => HatchAISettings.BuddyIdleBubblesEnabled = value),
                IdleBubblesDescription),
            AiBubblesRow(),
            BubbleLogRow()
        };

        internal const string IdleBubblesDescription =
            "Lets the buddy speak after a session has been idle for 15 minutes. "
            + "Always a built-in line, never AI tokens. Off by default, because it "
            + "speaks when you're not working.";

        internal const string BubbleLogDescription =
            "Records each bubble's moment, outcome and the text shown. A generated line "
            + "can echo what you were working on. Stays on this computer. Rolls over at "
            + "512 KB, keeping one older file.";

        // Test seam: the Open folder button's action. Production opens the
        // folder in Explorer or Finder, which a headless test must not do.
        internal static Action<string>? RevealFolderForTests;

        // Excluded from coverage: puts a file-manager window on the screen of
        // whatever machine runs it. Off the UI thread. The folder is created
        // first, so the button never opens somewhere else instead.
        [ExcludeFromCodeCoverage]
        private static void RevealInFileManager(string directory) => Task.Run(() =>
        {
            try { Directory.CreateDirectory(directory); } catch { }
            OpenFolder(directory);
        });

        // Claude Buddy's ClaudeDesktopManager.OpenFolder, the only part of that
        // file this app needs.
        [ExcludeFromCodeCoverage]
        private static void OpenFolder(string path)
        {
            try
            {
                using var process = OperatingSystem.IsWindows()
                    ? Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false })
                    : Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { path }, UseShellExecute = false });
            }
            catch { }
        }

        private Control BubbleLogRow()
        {
            var row = (Grid)Row("Bubble log",
                Switch(HatchAISettings.BuddyBubbleLogEnabled,
                    value => HatchAISettings.BuddyBubbleLogEnabled = value),
                BubbleLogDescription);

            var where = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 6, 0, 0)
            };

            var path = new SelectableTextBlock
            {
                Text = BubbleLogFile.DefaultPath,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            where.Children.Add(path);

            var open = new Button { Content = "Open folder", Margin = new Thickness(8, 0, 0, 0) };
            open.Click += (_, _) =>
                (RevealFolderForTests ?? RevealInFileManager)(BubbleLogFile.DefaultDirectory);
            Grid.SetColumn(open, 1);
            where.Children.Add(open);

            row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(where, 2);
            Grid.SetColumnSpan(where, 2);
            row.Children.Add(where);
            return row;
        }

        // Verbatim from Claude Buddy: the one place a person opts in to sending
        // part of their work to a model, so the disclosure lives on the row
        // itself. The figures are Claude Buddy's measurements (about 670 tokens
        // a bubble with the five stats, 660-672 over 11 calls); HatchAI sends
        // the identical prompt through the identical generator.
        internal const string AiBubblesDescription =
            "Off by default. When on, bubbles about your work (a reply finishing, your reply, "
            + "a question for you, long thinking) are written live by Claude Haiku, using your "
            + "Claude Code login.\n\n"
            + "Cost: about 670 tokens each, counted against your plan's usage, and never more "
            + "than three bubbles a minute. A session starting or ending, idle bubbles and "
            + "evolving always use built-in lines, which cost nothing.\n\n"
            + "What's sent: the moment; your buddy's personalities, species, rarity and five "
            + "stats; your project folder's name; and the first 200 characters of your latest "
            + "prompt, with obvious secrets such as keys, tokens and passwords, plus emails and "
            + "your home-folder path, removed.\n\n"
            + "Never sent: Claude's replies, code, or file contents. If Claude doesn't answer "
            + "within a few seconds, the buddy says one of its usual lines.";

        internal const string AiBubblesNoCliReason =
            "Needs the Claude Code CLI, which wasn't found on this computer. "
            + "The buddy keeps using its built-in lines.";

        internal const string AiBubblesNeedsBubblesNote =
            "Has no effect while Speech bubbles is off.";

        // Test seam. Production asks ClaudeBinary.Locate() afresh each time the
        // window builds, so a CLI installed after launch unlocks the switch.
        internal static Func<bool>? ClaudeCliFoundForTests;

        private static bool ClaudeCliFound() =>
            ClaudeCliFoundForTests?.Invoke() ?? ClaudeBinary.Locate() is not null;

        // A saved "on" is shown as on even when the CLI has gone: the switch is
        // only disabled, never flipped, so the choice survives a reinstall.
        private Control AiBubblesRow()
        {
            var toggle = Switch(HatchAISettings.BuddyAiBubblesEnabled,
                value => HatchAISettings.BuddyAiBubblesEnabled = value);
            var row = (Grid)Row("AI speech bubbles", toggle, AiBubblesDescription);

            var notes = new List<string>();
            if (!ClaudeCliFound())
            {
                toggle.IsEnabled = false;
                notes.Add(AiBubblesNoCliReason);
            }
            if (!HatchAISettings.BuddyBubblesEnabled) notes.Add(AiBubblesNeedsBubblesNote);

            if (notes.Count > 0)
            {
                row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var note = new TextBlock
                {
                    Text = string.Join(" ", notes),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                Grid.SetRow(note, 2);
                Grid.SetColumnSpan(note, 2);
                row.Children.Add(note);
            }

            return row;
        }

        // ---- session hooks (new in HatchAI) --------------------------------------

        internal const string HooksDescription =
            "HatchAI sees what your Claude Code, Codex and Grok sessions are doing through a small "
            + "hook each of them runs. This adds HatchAI's hook to their settings, beside whatever is "
            + "already there: your other settings, and other tools' hooks (Claude Buddy's included), "
            + "are left exactly as they are.\n\n"
            + "Safe to click again. It never adds a second copy, and a CLI that isn't installed is skipped.";

        // Test seams: the install itself, and the line describing what is
        // installed now. Production runs HookSetup against the real
        // configuration and reads the real files, neither of which a headless
        // test may do.
        internal static Func<Task<HookSetup.Result>>? InstallHooksForTests;
        internal static Func<string>? HookStateForTests;

        private static string HookState() => HookStateForTests?.Invoke() ?? HookSetup.DescribeThisMachine();

        internal Button? InstallHooksButton { get; private set; }
        internal TextBlock? HooksState { get; private set; }
        internal TextBlock? HooksOutcome { get; private set; }

        // No first-run prompt, on purpose: installing edits other programs'
        // settings, so it happens when the person asks. The row says what is
        // installed now, including whether Claude Buddy's hooks already cover
        // this machine, so the state is one click into Settings.
        private Control HooksRow()
        {
            InstallHooksButton = new Button { Content = "Install hooks" };
            var row = (Grid)Row("Session hooks", InstallHooksButton, HooksDescription);

            HooksState = new TextBlock
            {
                Text = HookState(),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            HooksOutcome = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                IsVisible = false
            };

            InstallHooksButton.Click += async (_, _) => await InstallHooksAsync();

            row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(HooksState, 2);
            Grid.SetColumnSpan(HooksState, 2);
            row.Children.Add(HooksState);
            Grid.SetRow(HooksOutcome, 3);
            Grid.SetColumnSpan(HooksOutcome, 2);
            row.Children.Add(HooksOutcome);
            return row;
        }

        // The button's work: disabled while the installer runs (a second click
        // would only queue a second, identical run), then the outcome and a
        // fresh reading of what is installed.
        internal async Task InstallHooksAsync()
        {
            InstallHooksButton!.IsEnabled = false;
            HooksOutcome!.Text = "Installing…";
            HooksOutcome.IsVisible = true;
            try
            {
                var result = await (InstallHooksForTests ?? (() => HookSetup.RunAsync()))();
                HooksOutcome.Text = HookSetup.OutcomeText(result);
            }
            catch (Exception ex)
            {
                HooksOutcome.Text = HookSetup.OutcomeText(new HookSetup.Result(false, Array.Empty<string>(), ex.Message));
            }
            finally
            {
                HooksState!.Text = HookState();
                InstallHooksButton.IsEnabled = true;
            }
        }

        // ---- import (new in HatchAI) --------------------------------------------

        internal const string ImportDescription =
            "Copy the buddy you grew in Claude Buddy into HatchAI: its progress, history "
            + "and position, and any extra Claude Code or Codex folders it counts. Claude "
            + "Buddy's settings are only read, never changed.\n\n"
            + "Afterwards both apps carry the same pet and grow it separately, until you "
            + "turn off Show buddy in Claude Buddy.";

        internal const string ImportConfirmText =
            "This replaces the buddy HatchAI has now, and its progress cannot be brought back. Import anyway?";

        // Test seam: where Claude Buddy's settings are read from. Production
        // uses Claude Buddy's own path expression.
        internal static Func<string>? ClaudeBuddySettingsPathForTests;

        private static string ClaudeBuddySettingsPath =>
            ClaudeBuddySettingsPathForTests?.Invoke() ?? HatchAISettings.ClaudeBuddySettingsPath;

        // Named so a test can reach each part without walking a template.
        internal Button? ImportButton { get; private set; }
        internal Button? ConfirmImportButton { get; private set; }
        internal Button? CancelImportButton { get; private set; }
        internal TextBlock? ImportStatus { get; private set; }

        // Two steps on purpose: import replaces whatever buddy HatchAI already
        // has, so the first click only asks. The confirmation is inline rather
        // than a dialog, which keeps the whole exchange inside one control a
        // headless test can drive.
        private Control ImportRow()
        {
            ImportButton = new Button { Content = "Import…" };
            var row = (Grid)Row("Import buddy from Claude Buddy", ImportButton, ImportDescription);

            var confirm = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
            confirm.Children.Add(new TextBlock { Text = ImportConfirmText, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            ConfirmImportButton = new Button { Content = "Replace and import" };
            CancelImportButton = new Button { Content = "Cancel" };
            buttons.Children.Add(ConfirmImportButton);
            buttons.Children.Add(CancelImportButton);
            confirm.Children.Add(buttons);

            ImportStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };

            ImportButton.Click += (_, _) =>
            {
                confirm.IsVisible = true;
                ImportStatus.IsVisible = false;
            };
            CancelImportButton.Click += (_, _) => confirm.IsVisible = false;
            ConfirmImportButton.Click += (_, _) =>
            {
                confirm.IsVisible = false;
                ImportStatus.Text = RunImport(ClaudeBuddySettingsPath);
                ImportStatus.IsVisible = true;
            };

            row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(confirm, 2);
            Grid.SetColumnSpan(confirm, 2);
            row.Children.Add(confirm);
            Grid.SetRow(ImportStatus, 3);
            Grid.SetColumnSpan(ImportStatus, 2);
            row.Children.Add(ImportStatus);
            return row;
        }

        // Imports, then makes the running app agree with the file: the
        // controller drops the buddy it held (or its next save would write it
        // straight back), the buddy window moves to the imported position, and
        // the tray re-reads what rebirth now allows. Answers what to tell the
        // person.
        internal static string RunImport(string sourcePath)
        {
            var outcome = HatchAISettings.ImportFromClaudeBuddy(sourcePath);
            if (outcome != HatchAISettings.ImportOutcome.Imported) return OutcomeText(outcome, sourcePath);

            BuddyController.Instance?.Reload();
            Tray.Instance?.BuddyWindow?.PlaceOnScreen();
            Tray.Instance?.Refresh();
            return OutcomeText(outcome, sourcePath);
        }

        internal static string OutcomeText(HatchAISettings.ImportOutcome outcome, string sourcePath) => outcome switch
        {
            HatchAISettings.ImportOutcome.Imported => "Imported. Your Claude Buddy buddy lives here now.",
            HatchAISettings.ImportOutcome.NotFound => "Claude Buddy's settings weren't found at " + sourcePath + ". Nothing was changed.",
            HatchAISettings.ImportOutcome.NoBuddy => "Claude Buddy hasn't hatched a buddy yet. Nothing was changed.",
            _ => "Claude Buddy's settings couldn't be read. Nothing was changed.",
        };

        // ---- helpers (from Claude Buddy, without its search and card chrome) ----

        private static Control Heading(string text) => new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(14, 14, 14, 2)
        };

        private static Control Row(string label, Control control, string? help = null)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions(help is null ? "Auto" : "Auto,Auto"),
                Margin = new Thickness(14, 10)
            };

            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(text);

            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);

            if (help is not null)
            {
                var hint = new TextBlock
                {
                    Text = help,
                    FontSize = 11,
                    Opacity = 0.55,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                Grid.SetRow(hint, 1);
                Grid.SetColumnSpan(hint, 2);
                grid.Children.Add(hint);
            }

            return grid;
        }

        // A bare switch: Fluent's default writes "On"/"Off" beside it. Claude
        // Buddy fell back to a checkbox when the macOS theme had no usable
        // switch template; HatchAI is Fluent everywhere, so it always has one.
        private static ToggleSwitch Switch(bool value, Action<bool> onChange)
        {
            var toggle = new ToggleSwitch
            {
                IsChecked = value,
                OnContent = null,
                OffContent = null
            };
            toggle.IsCheckedChanged += (_, _) => onChange(toggle.IsChecked ?? false);
            return toggle;
        }
    }
}
