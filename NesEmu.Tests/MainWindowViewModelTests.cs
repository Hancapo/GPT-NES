using NesEmu.App;

namespace NesEmu.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void ReadyStateShowsOpenOverlayAndDisabledTransport()
    {
        var viewModel = new MainWindowViewModel("v1.2.3", "Windows x64");

        viewModel.Apply(new MainWindowViewState(
            HasRomLoaded: false,
            IsPaused: false,
            IsStopped: false,
            LoadedRomPath: null,
            FramesPerSecond: 0,
            MidiOutputStatus: "Disabled",
            Renderer: VideoRendererKind.Software,
            VolumePercent: 100,
            StatusMessage: "Ready.",
            IsFullscreen: false));

        Assert.Equal("NesEmu", viewModel.WindowTitle);
        Assert.True(viewModel.OverlayVisible);
        Assert.True(viewModel.OverlayOpenButtonVisible);
        Assert.False(viewModel.TransportEnabled);
        Assert.Equal("Pause", viewModel.PauseMenuHeader);
        Assert.Equal("No ROM loaded", viewModel.RomText);
    }

    [Fact]
    public void PausedStateShowsResumeOverlayAndRomTitle()
    {
        var viewModel = new MainWindowViewModel("v1.2.3", "Windows x64");

        viewModel.Apply(new MainWindowViewState(
            HasRomLoaded: true,
            IsPaused: true,
            IsStopped: false,
            LoadedRomPath: @"C:\roms\Mega Game.nes",
            FramesPerSecond: 59.9,
            MidiOutputStatus: "Digital Keyboard",
            Renderer: VideoRendererKind.OpenGl,
            VolumePercent: 80,
            StatusMessage: "Emulation paused.",
            IsFullscreen: false));

        Assert.Equal("NesEmu - Mega Game.nes", viewModel.WindowTitle);
        Assert.True(viewModel.OverlayVisible);
        Assert.False(viewModel.OverlayOpenButtonVisible);
        Assert.Equal("Paused", viewModel.OverlayTitle);
        Assert.Equal("Resume", viewModel.PauseMenuHeader);
        Assert.Equal("OpenGL", viewModel.RendererText);
        Assert.Equal("Digital Keyboard", viewModel.MidiDeviceText);
    }

    [Fact]
    public void RunningFullscreenStateHidesOverlayAndFormatsFooter()
    {
        var viewModel = new MainWindowViewModel("v2.0.0", "Windows x64");

        viewModel.Apply(new MainWindowViewState(
            HasRomLoaded: true,
            IsPaused: false,
            IsStopped: false,
            LoadedRomPath: @"C:\roms\Action.nes",
            FramesPerSecond: 60.04,
            MidiOutputStatus: "",
            Renderer: VideoRendererKind.Software,
            VolumePercent: 137,
            StatusMessage: "Running.",
            IsFullscreen: true));

        Assert.False(viewModel.OverlayVisible);
        Assert.False(viewModel.OverlayOpenButtonVisible);
        Assert.True(viewModel.TransportEnabled);
        Assert.Equal("Pause", viewModel.PauseMenuHeader);
        Assert.Equal("Exit Fullscreen", viewModel.FullscreenMenuHeader);
        Assert.Equal($"{60.04:0.0} FPS", viewModel.FpsText);
        Assert.Equal("Disabled", viewModel.MidiDeviceText);
        Assert.Equal("137%", viewModel.VolumeText);
        Assert.Equal("Running.", viewModel.StatusText);
    }

    [Fact]
    public void PreviewVolumeUpdatesDisplayedVolumeImmediately()
    {
        var viewModel = new MainWindowViewModel("v1.2.3", "Windows x64");

        viewModel.Apply(new MainWindowViewState(
            HasRomLoaded: true,
            IsPaused: false,
            IsStopped: false,
            LoadedRomPath: @"C:\roms\Action.nes",
            FramesPerSecond: 60,
            MidiOutputStatus: "Disabled",
            Renderer: VideoRendererKind.Software,
            VolumePercent: 100,
            StatusMessage: "Running.",
            IsFullscreen: false));

        viewModel.SetPreviewVolume(145);

        Assert.Equal("145%", viewModel.VolumeText);
        Assert.Equal("145%", $"{viewModel.VolumePercent}%");
    }
}
