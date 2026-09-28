using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace HatchAI
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        // Excluded from coverage: everything below the guard is unreachable
        // under test by design. Avalonia's headless lifetime is not an
        // IClassicDesktopStyleApplicationLifetime, so the guard never opens,
        // which is what lets tests/UiTests host the real App class.
        [ExcludeFromCodeCoverage]
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
