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
    [
        new(
            AudioBackendKind.Sdl,
            "SDL",
            "Default audio backend. Most reliable option for normal desktop builds."),
        new(
            AudioBackendKind.OpenAl,
            "OpenAL",
            "Alternative backend. Use only if it behaves correctly on the target machine.")
    ];
}

public sealed class AudioOutputSettings
{
    public AudioBackendKind Backend { get; set; } = AudioBackendKind.Sdl;

    public int OutputLatencyMilliseconds { get; set; } = 60;

    public int MasterVolumePercent { get; set; } = 100;

    public static AudioOutputSettings CreateDefault()
    {
        return new AudioOutputSettings
        {
            Backend = AudioBackendKind.Sdl
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
