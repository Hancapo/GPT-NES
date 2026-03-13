namespace NesEmu.App;

public enum AudioBackendKind
{
    Sdl,
    OpenAl
}

public sealed record AudioBackendOption(AudioBackendKind Backend, string DisplayName, string Description)
{
    public override string ToString() => DisplayName;
}

public static class AudioBackendCatalog
{
    public static IReadOnlyList<AudioBackendOption> Options { get; } =
        OperatingSystem.IsLinux()
            ?
            [
                new(
                    AudioBackendKind.Sdl,
                    "SDL",
                    "Cross-platform audio backend recommended for Linux builds."),
                new(
                    AudioBackendKind.OpenAl,
                    "OpenAL",
                    "Cross-platform playback backend kept as an alternative option.")
            ]
            :
            [
                new(
                    AudioBackendKind.OpenAl,
                    "OpenAL",
                    "Cross-platform playback backend used by the emulator audio output."),
                new(
                    AudioBackendKind.Sdl,
                    "SDL",
                    "Cross-platform audio backend available as an alternative option.")
            ];
}

public sealed class AudioOutputSettings
{
    public AudioBackendKind Backend { get; set; } = OperatingSystem.IsLinux()
        ? AudioBackendKind.Sdl
        : AudioBackendKind.OpenAl;

    public int OutputLatencyMilliseconds { get; set; } = 60;

    public int MasterVolumePercent { get; set; } = 100;

    public static AudioOutputSettings CreateDefault()
    {
        return new AudioOutputSettings
        {
            Backend = OperatingSystem.IsLinux()
                ? AudioBackendKind.Sdl
                : AudioBackendKind.OpenAl
        };
    }

    public AudioOutputSettings Clone()
    {
        return new AudioOutputSettings
        {
            Backend = Backend,
            OutputLatencyMilliseconds = OutputLatencyMilliseconds,
            MasterVolumePercent = MasterVolumePercent
        };
    }
}
