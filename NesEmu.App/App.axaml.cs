using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using ShadUI;

namespace NesEmu.App;

public partial class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new ShadTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += UiThread_UnhandledException;
        AppLogger.Info("Avalonia framework initialization completed.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void UiThread_UnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled UI thread exception.", e.Exception);
    }
}
