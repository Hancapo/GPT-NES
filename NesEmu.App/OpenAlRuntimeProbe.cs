using System.Runtime.InteropServices;

namespace NesEmu.App;

internal static class OpenAlRuntimeProbe
{
    private static readonly object Sync = new();
    private static readonly string[] CandidateLibraryNames = BuildCandidateLibraryNames();

    private static bool _probed;
    private static bool _isRuntimeAvailable;
    private static string? _resolvedLibraryName;

    public static AudioBackendKind GetDefaultBackendForCurrentPlatform()
    {
        return GetDefaultBackend(
            OperatingSystem.IsLinux(),
            IsRuntimeAvailable());
    }

    internal static AudioBackendKind GetDefaultBackend(bool isLinux, bool isOpenAlRuntimeAvailable)
    {
        return isLinux || !isOpenAlRuntimeAvailable
            ? AudioBackendKind.Sdl
            : AudioBackendKind.OpenAl;
    }

    public static bool IsRuntimeAvailable()
    {
        EnsureProbed();
        return _isRuntimeAvailable;
    }

    public static string GetMissingRuntimeMessage()
    {
        EnsureProbed();

        return _resolvedLibraryName is not null
            ? $"OpenAL runtime could not be initialized from '{_resolvedLibraryName}'."
            : $"OpenAL runtime not found. Install or bundle one of: {string.Join(", ", CandidateLibraryNames)}.";
    }

    private static void EnsureProbed()
    {
        if (_probed)
        {
            return;
        }

        lock (Sync)
        {
            if (_probed)
            {
                return;
            }

            foreach (var candidate in CandidateLibraryNames)
            {
                if (!NativeLibrary.TryLoad(candidate, out var handle))
                {
                    continue;
                }

                try
                {
                    _isRuntimeAvailable = true;
                    _resolvedLibraryName = candidate;
                    return;
                }
                finally
                {
                    NativeLibrary.Free(handle);
                    _probed = true;
                }
            }

            _isRuntimeAvailable = false;
            _resolvedLibraryName = null;
            _probed = true;
        }
    }

    private static string[] BuildCandidateLibraryNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                "OpenAL32.dll",
                "soft_oal.dll"
            ];
        }

        if (OperatingSystem.IsLinux())
        {
            return
            [
                "libopenal.so.1",
                "libopenal.so"
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "/System/Library/Frameworks/OpenAL.framework/OpenAL",
                "libopenal.dylib"
            ];
        }

        return
        [
            "OpenAL32.dll",
            "soft_oal.dll",
            "libopenal.so.1",
            "libopenal.so",
            "libopenal.dylib"
        ];
    }
}
