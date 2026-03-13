namespace NesEmu.App;

public enum AudioBackendKind
{
    OpenAl
}

public sealed record AudioBackendOption(AudioBackendKind Backend, string DisplayName, string Description);

public static class AudioBackendCatalog
{
    public static IReadOnlyList<AudioBackendOption> Options { get; } =
    [
        new(
            AudioBackendKind.OpenAl,
            "OpenAL",
            "Cross-platform playback backend used by the emulator audio output.")
    ];
}

public sealed class AudioOutputSettings
{
    public AudioBackendKind Backend { get; set; } = AudioBackendKind.OpenAl;

    public int OutputLatencyMilliseconds { get; set; } = 60;

    public int MasterVolumePercent { get; set; } = 100;

    public static AudioOutputSettings CreateDefault() => new();

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
