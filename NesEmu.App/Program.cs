using Avalonia;
using Avalonia.Threading;

namespace NesEmu.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogger.Initialize();
        AppLogger.Info("Application startup requested.");

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            AppLogger.Info("Application shutdown completed.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Fatal exception during application startup or shutdown.", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode =
                [
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Software
                ]
            });
        }

        return builder.LogToTrace();
    }

    private static void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        AppLogger.Error(
            $"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}.",
            e.ExceptionObject as Exception);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Unobserved task exception.", e.Exception);
    }
}
