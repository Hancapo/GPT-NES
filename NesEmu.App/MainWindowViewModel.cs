using NesEmu.Core;

namespace NesEmu.App;

public readonly record struct MainWindowViewState(
    bool HasRomLoaded,
    bool IsPaused,
    bool IsStopped,
    string? LoadedRomPath,
    double FramesPerSecond,
    string MidiOutputStatus,
    VideoRendererKind Renderer,
    int VolumePercent,
    string StatusMessage,
    bool IsFullscreen);

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly string _applicationVersionText;
    private readonly string _operatingSystemText;

    private string _windowTitle = "NesEmu";
    private string _pauseMenuHeader = "Pause";
    private string _fullscreenMenuHeader = "Enter Fullscreen";
    private bool _transportEnabled;
    private bool _stopEnabled;
    private bool _resetEnabled;
    private string _overlayTitle = "NesEmu";
    private string _overlayBody = "Open a NES ROM to get started.";
    private bool _overlayVisible = true;
    private bool _overlayOpenButtonVisible = true;
    private string _romText = "No ROM loaded";
    private string _fpsText = "0.0 FPS";
    private string _midiDeviceText = "Disabled";
    private string _rendererText = "Software";
    private string _volumeText = "100%";
    private int _volumePercent = 100;
    private string _statusText = "Ready.";
    private string _osText;
    private string _versionText;

    public MainWindowViewModel(string applicationVersionText, string operatingSystemText)
    {
        _applicationVersionText = applicationVersionText;
        _operatingSystemText = operatingSystemText;
        _osText = operatingSystemText;
        _versionText = applicationVersionText;
    }

    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetProperty(ref _windowTitle, value);
    }

    public string PauseMenuHeader
    {
        get => _pauseMenuHeader;
        private set => SetProperty(ref _pauseMenuHeader, value);
    }

    public string FullscreenMenuHeader
    {
        get => _fullscreenMenuHeader;
        private set => SetProperty(ref _fullscreenMenuHeader, value);
    }

    public bool TransportEnabled
    {
        get => _transportEnabled;
        private set => SetProperty(ref _transportEnabled, value);
    }

    public bool StopEnabled
    {
        get => _stopEnabled;
        private set => SetProperty(ref _stopEnabled, value);
    }

    public bool ResetEnabled
    {
        get => _resetEnabled;
        private set => SetProperty(ref _resetEnabled, value);
    }

    public string OverlayTitle
    {
        get => _overlayTitle;
        private set => SetProperty(ref _overlayTitle, value);
    }

    public string OverlayBody
    {
        get => _overlayBody;
        private set => SetProperty(ref _overlayBody, value);
    }

    public bool OverlayVisible
    {
        get => _overlayVisible;
        private set => SetProperty(ref _overlayVisible, value);
    }

    public bool OverlayOpenButtonVisible
    {
        get => _overlayOpenButtonVisible;
        private set => SetProperty(ref _overlayOpenButtonVisible, value);
    }

    public string RomText
    {
        get => _romText;
        private set => SetProperty(ref _romText, value);
    }

    public string FpsText
    {
        get => _fpsText;
        private set => SetProperty(ref _fpsText, value);
    }

    public string MidiDeviceText
    {
        get => _midiDeviceText;
        private set => SetProperty(ref _midiDeviceText, value);
    }

    public string RendererText
    {
        get => _rendererText;
        private set => SetProperty(ref _rendererText, value);
    }

    public string VolumeText
    {
        get => _volumeText;
        private set => SetProperty(ref _volumeText, value);
    }

    public int VolumePercent
    {
        get => _volumePercent;
        private set => SetProperty(ref _volumePercent, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string OsText
    {
        get => _osText;
        private set => SetProperty(ref _osText, value);
    }

    public string VersionText
    {
        get => _versionText;
        private set => SetProperty(ref _versionText, value);
    }

    public void Apply(MainWindowViewState state)
    {
        var hasRom = state.HasRomLoaded;
        var paused = hasRom && state.IsPaused;
        var stopped = hasRom && state.IsStopped;
        var running = hasRom && !paused && !stopped;
        var romName = hasRom && !string.IsNullOrWhiteSpace(state.LoadedRomPath)
            ? Path.GetFileName(state.LoadedRomPath)
            : null;

        WindowTitle = romName is null ? "NesEmu" : $"NesEmu - {romName}";
        PauseMenuHeader = stopped ? "Start" : paused ? "Resume" : "Pause";
        FullscreenMenuHeader = state.IsFullscreen ? "Exit Fullscreen" : "Enter Fullscreen";

        TransportEnabled = hasRom;
        StopEnabled = hasRom;
        ResetEnabled = hasRom;

        if (!hasRom)
        {
            OverlayTitle = "NesEmu";
            OverlayBody = "Open a NES ROM to get started.";
            OverlayVisible = true;
            OverlayOpenButtonVisible = true;
        }
        else if (stopped)
        {
            OverlayTitle = "Stopped";
            OverlayBody = "The session is stopped. Press Start to run the ROM again.";
            OverlayVisible = true;
            OverlayOpenButtonVisible = false;
        }
        else if (paused)
        {
            OverlayTitle = "Paused";
            OverlayBody = "The emulation is paused. Press Resume to continue.";
            OverlayVisible = true;
            OverlayOpenButtonVisible = false;
        }
        else
        {
            OverlayVisible = false;
            OverlayOpenButtonVisible = false;
        }

        RomText = romName ?? "No ROM loaded";
        FpsText = $"{state.FramesPerSecond:0.0} FPS";
        MidiDeviceText = string.IsNullOrWhiteSpace(state.MidiOutputStatus) ? "Disabled" : state.MidiOutputStatus;
        RendererText = state.Renderer == VideoRendererKind.OpenGl ? "OpenGL" : "Software";

        VolumePercent = Math.Clamp(state.VolumePercent, 0, 200);
        VolumeText = $"{VolumePercent}%";
        StatusText = string.IsNullOrWhiteSpace(state.StatusMessage) ? "Ready." : state.StatusMessage;
        OsText = _operatingSystemText;
        VersionText = _applicationVersionText;

        if (!running && paused)
        {
            FpsText = $"{state.FramesPerSecond:0.0} FPS";
        }
    }

    public void SetPreviewVolume(int volumePercent)
    {
        VolumePercent = Math.Clamp(volumePercent, 0, 200);
        VolumeText = $"{VolumePercent}%";
    }
}
