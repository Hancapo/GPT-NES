using Avalonia;

namespace NesEmu.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

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
}
