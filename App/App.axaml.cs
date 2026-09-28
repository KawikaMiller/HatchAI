using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace HatchAI
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        // The icons, loaded once from the embedded assets. Null if an asset
        // cannot be read, which costs the icon rather than the app. The tray
        // icon is Claude Buddy's idle tray glyph and the window icon its app
        // icon, carried over for v1 (same owner); a sprite frame is a
        // follow-up.
        private static WindowIcon? _trayIcon;
        private static WindowIcon? _windowIcon;

        internal static WindowIcon? TrayIcon => _trayIcon ??= LoadIcon("avares://HatchAI/Assets/tray-idle.png");

        internal static WindowIcon? WindowIcon => _windowIcon ??= LoadIcon("avares://HatchAI/Assets/HatchAI.ico");

        internal static WindowIcon? LoadIcon(string uri)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri(uri));
                return new WindowIcon(stream);
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        // Excluded from coverage: everything below the guard is unreachable
        // under test by design. Avalonia's headless lifetime is not an
        // IClassicDesktopStyleApplicationLifetime, so the guard never opens,
        // which is what lets tests/UiTests host the real App class without it
        // starting a status reader against the real temp folder or putting an
        // icon in the notification area of the machine running the suite.
        // Every piece it wires — StatusReader, BuddyController.CreateForApp,
        // Tray, SettingsWindow — is covered on its own.
        [ExcludeFromCodeCoverage]
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // The buddy's window comes and goes with "Show buddy"; the app
                // itself exits only from the tray's Quit.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var window = new BuddyWindow();
                var buddy = BuddyController.CreateForApp(window);
                var tray = new Tray(window);
                Tray.Instance = tray;

                // The reader hands each scan to the controller, exactly as
                // Claude Buddy's SessionManager did, and the tray re-reads
                // what rebirth and the card allow after each one.
                var reader = new StatusReader();
                reader.SnapshotsPublished = snapshots =>
                {
                    buddy.OnSnapshots(snapshots);
                    tray.Refresh();
                };

                // Start() applies "Show buddy", so a buddy switched off never
                // touches settings.json or shows a window. The reader after, so
                // its first scan — the baseline, which announces nothing —
                // lands on a controller that is already showing.
                buddy.Start();
                reader.Start();
                tray.Refresh();

                // Quit: stop reading, then let the controller write what the
                // last rounds earned. Every setter saves synchronously, so
                // there is no other pending write to flush.
                desktop.Exit += (_, _) =>
                {
                    reader.Dispose();
                    buddy.Dispose();
                    tray.Dispose();
                };

                // Development entry point, kept from Claude Buddy:
                // `HatchAI --settings` opens the settings window at launch.
                if (desktop.Args?.Contains("--settings") == true) SettingsWindow.Toggle();

                // Its sibling for the card: `HatchAI --card` opens the buddy's card
                // at launch (when the buddy is shown), so the card — and the
                // rebirth offer on it — can be checked without a click on the
                // buddy itself.
                if (desktop.Args?.Contains("--card") == true) window.OpenCard();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
