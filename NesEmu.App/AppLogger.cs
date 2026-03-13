using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NesEmu.App;

internal static class AppLogger
{
    private static readonly object Sync = new();
    private static string? _logDirectoryPath;
    private static string? _logFilePath;
    private static bool _initialized;

    public static string LogDirectoryPath
    {
        get
        {
            EnsureInitialized();
            return _logDirectoryPath!;
        }
    }

    public static string LogFilePath
    {
        get
        {
            EnsureInitialized();
            return _logFilePath!;
        }
    }

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = AppContext.BaseDirectory;
            }

            _logDirectoryPath = Path.Combine(baseDirectory, "NesEmu", "logs");
            Directory.CreateDirectory(_logDirectoryPath);
            CleanupOldLogs(_logDirectoryPath);

            _logFilePath = Path.Combine(_logDirectoryPath, $"nesemu-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            _initialized = true;

            WriteLocked(
                "INFO",
                "Logger initialized.",
                new StringBuilder()
                    .AppendLine($"App version: {typeof(AppLogger).Assembly.GetName().Version}")
                    .AppendLine($"OS: {RuntimeInformation.OSDescription}")
                    .AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}")
                    .AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}")
                    .Append($"Base directory: {AppContext.BaseDirectory}")
                    .ToString());
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Warning(string message)
    {
        Write("WARN", message, null);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    public static void OpenLogDirectory()
    {
        EnsureInitialized();

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_logDirectoryPath}\"",
                UseShellExecute = true
            });
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{_logDirectoryPath}\"",
                UseShellExecute = false
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _logDirectoryPath!,
            UseShellExecute = true
        });
    }

    private static void Write(string level, string message, Exception? exception)
    {
        lock (Sync)
        {
            try
            {
                EnsureInitialized();
                WriteLocked(level, message, exception?.ToString());
            }
            catch
            {
                try
                {
                    Trace.WriteLine($"[{level}] {message}");
                    if (exception is not null)
                    {
                        Trace.WriteLine(exception);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    private static void WriteLocked(string level, string message, string? detail)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] [T{Environment.CurrentManagedThreadId}] {message}";
        File.AppendAllText(_logFilePath!, line + Environment.NewLine, Encoding.UTF8);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            File.AppendAllText(_logFilePath!, detail.TrimEnd() + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static void CleanupOldLogs(string directoryPath)
    {
        try
        {
            var logFiles = new DirectoryInfo(directoryPath)
                .EnumerateFiles("*.log")
                .OrderByDescending(file => file.CreationTimeUtc)
                .Skip(9)
                .ToArray();

            foreach (var file in logFiles)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }
}
