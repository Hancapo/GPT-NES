namespace NesEmu.App;

public sealed class AudioOutputSettings
{
    public int OutputLatencyMilliseconds { get; set; } = 60;

    public int MasterVolumePercent { get; set; } = 100;

    public static AudioOutputSettings CreateDefault() => new();

    public AudioOutputSettings Clone()
    {
        return new AudioOutputSettings
        {
            OutputLatencyMilliseconds = OutputLatencyMilliseconds,
            MasterVolumePercent = MasterVolumePercent
        };
    }
}
