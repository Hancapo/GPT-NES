namespace NesEmu.App;

public interface IVideoOutputSettingsHost
{
    VideoOutputSettings GetVideoSettingsSnapshot();

    bool TryApplyVideoSettings(VideoOutputSettings settings, out string? error);
}
