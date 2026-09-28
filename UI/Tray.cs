using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace HatchAI
{
    // The notification-area icon and its menu.
    //
    // Essential rather than decorative: after "Hide buddy" on the buddy's own
    // menu, this and the settings window are the only ways to bring it back.
    // Claude Buddy's tray carries exactly one buddy item ("Show buddy"); this
    // one is the buddy's whole menu, since the buddy is the whole app.
    //
    //   Show buddy       the same preference the settings switch writes
    //   Open card        the buddy's card; disabled while it is hidden
    //   Rebirth…         opens the card *with the rebirth offer*, so the card's
    //                    own confirmation stays the only path to a rebirth;
    //                    enabled only once BuddyProgress says it is earned
    //   Speech bubbles   the same preference as the buddy menu's item
    //   Settings…
    //   Quit HatchAI
    //
    // The checks and the enabled states are re-read by Refresh, which App
    // calls after every status scan and the settings window calls after a
    // switch, and which also runs as the menu opens where the platform raises
    // that. A menu that is a couple of seconds stale for a tray the user is
    // not looking at costs nothing.
    internal sealed class Tray : IDisposable
    {
        // The running app's tray, so the settings window can ask it to refresh
        // without holding a reference. Null outside the app and in tests that
        // do not install one.
        internal static Tray? Instance { get; set; }

        private readonly TrayIcon? _icon;
        private readonly Func<BuddyController?> _controller;

        internal NativeMenu Menu { get; }
        internal NativeMenuItem ShowBuddyItem { get; }
        internal NativeMenuItem OpenCardItem { get; }
        internal NativeMenuItem RebirthItem { get; }
        internal NativeMenuItem BubblesItem { get; }
        internal NativeMenuItem SettingsItem { get; }
        internal NativeMenuItem QuitItem { get; }

        // The window the card lives on. Null in a test that has no buddy.
        internal BuddyWindow? BuddyWindow { get; }

        // Test seams for the two items that would otherwise open a real window
        // or end the test host.
        internal Action? OpenSettings { get; set; }
        internal Action? Quit { get; set; }

        // `createIcon` false for the headless suite, which has no notification
        // area; everything else — the menu, its items, what they do — is the
        // same object either way.
        internal Tray(BuddyWindow? buddyWindow, Func<BuddyController?>? controller = null, bool createIcon = true)
        {
            BuddyWindow = buddyWindow;
            _controller = controller ?? (() => BuddyController.Instance);

            Menu = new NativeMenu();

            ShowBuddyItem = new NativeMenuItem("Show buddy") { ToggleType = MenuItemToggleType.CheckBox };
            ShowBuddyItem.Click += (_, _) => ToggleBuddyVisible();

            OpenCardItem = new NativeMenuItem("Open card");
            OpenCardItem.Click += (_, _) => BuddyWindow?.OpenCard();

            RebirthItem = new NativeMenuItem("Rebirth…");
            RebirthItem.Click += (_, _) => BuddyWindow?.OpenCard(offerRebirth: true);

            BubblesItem = new NativeMenuItem("Speech bubbles") { ToggleType = MenuItemToggleType.CheckBox };
            BubblesItem.Click += (_, _) => ToggleBubbles();

            SettingsItem = new NativeMenuItem("Settings…");
            SettingsItem.Click += (_, _) => (OpenSettings ?? SettingsWindow.Toggle)();

            QuitItem = new NativeMenuItem("Quit HatchAI");
            QuitItem.Click += (_, _) => (Quit ?? ShutDown)();

            Menu.Add(ShowBuddyItem);
            Menu.Add(OpenCardItem);
            Menu.Add(RebirthItem);
            Menu.Add(new NativeMenuItemSeparator());
            Menu.Add(BubblesItem);
            Menu.Add(SettingsItem);
            Menu.Add(new NativeMenuItemSeparator());
            Menu.Add(QuitItem);

            Menu.Opening += (_, _) => Refresh();
            Refresh();

            if (createIcon) _icon = CreateIcon(Menu);
        }

        // Excluded from coverage: puts a real icon in the notification area of
        // whatever machine runs it, which the headless suite has none of.
        [ExcludeFromCodeCoverage]
        private static TrayIcon CreateIcon(NativeMenu menu)
        {
            var icon = new TrayIcon
            {
                ToolTipText = "HatchAI",
                Menu = menu,
                Icon = App.TrayIcon,
                IsVisible = true
            };
            // A left click on the icon opens the card, the thing a person most
            // likely wants from it; the menu is on the right button.
            icon.Clicked += (_, _) => Instance?.BuddyWindow?.OpenCard();
            return icon;
        }

        // Makes every check and enabled state agree with the settings and the
        // controller as they are now.
        internal void Refresh()
        {
            var shown = HatchAISettings.BuddyEnabled;
            ShowBuddyItem.IsChecked = shown;
            BubblesItem.IsChecked = HatchAISettings.BuddyBubblesEnabled;

            var state = _controller()?.State;
            var live = shown && state is not null && BuddyWindow is not null;
            OpenCardItem.IsEnabled = live;
            RebirthItem.IsEnabled = live && BuddyProgress.CanRebirth(state!.Tokens);
        }

        // The same setting the settings window's switch flips, so the two
        // cannot disagree, then said to the running controller. Verbatim from
        // Claude Buddy's TrayController.ToggleBuddyVisible, plus the refresh.
        internal static void ToggleBuddyVisible()
        {
            HatchAISettings.BuddyEnabled = !HatchAISettings.BuddyEnabled;
            BuddyController.Instance?.Reapply();
            Instance?.Refresh();
        }

        // The buddy menu's own "Speech bubbles" item, reached from here too.
        // Turning bubbles off also takes down one already showing, as that
        // item does.
        internal static void ToggleBubbles()
        {
            var on = !HatchAISettings.BuddyBubblesEnabled;
            HatchAISettings.BuddyBubblesEnabled = on;
            if (!on) Instance?.BuddyWindow?.Bubble?.Dismiss();
            Instance?.BuddyWindow?.UpdateBubblesCheck();
            Instance?.Refresh();
        }

        // Excluded from coverage: ends the process. Under the headless
        // lifetime the guard is never true, and tests replace it with Quit.
        [ExcludeFromCodeCoverage]
        private static void ShutDown()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        public void Dispose()
        {
            if (_icon is not null) RemoveIcon(_icon);
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        // Excluded from coverage for CreateIcon's reason: there is only an icon
        // to remove where there is a notification area to have put it in.
        [ExcludeFromCodeCoverage]
        private static void RemoveIcon(TrayIcon icon)
        {
            icon.IsVisible = false;
            icon.Dispose();
        }
    }
}
