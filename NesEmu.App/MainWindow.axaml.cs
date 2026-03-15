using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NesEmu.Core;
using ShadWindow = ShadUI.Window;

namespace NesEmu.App;

public partial class MainWindow : ShadWindow, IVideoOutputSettingsHost
{
    private static readonly Thickness WindowedRootMargin = new(24, 22, 24, 20);
    private static readonly Thickness FullscreenRootMargin = new(8);
    private static readonly Thickness WindowedViewportShellPadding = new(24);
    private static readonly Thickness FullscreenViewportShellPadding = new(0);
    private static readonly Thickness WindowedGameViewportPadding = new(28);
    private static readonly Thickness FullscreenGameViewportPadding = new(8);
    private static readonly CornerRadius WindowedSectionCornerRadius = new(22);
    private static readonly CornerRadius WindowedGameViewportCornerRadius = new(20);
    private static readonly CornerRadius FullscreenCornerRadius = new(0);
    private static readonly Thickness WindowedBorderThickness = new(1);
    private static readonly Thickness FullscreenBorderThickness = new(0);
    private static readonly string ApplicationVersionText = GetApplicationVersionText();
    private static readonly string OperatingSystemText = GetOperatingSystemText();

    private readonly InputStateSource _inputState = new();
    private readonly MidiOutputService _midiOutput = new();
    private readonly EmulatorHost _emulator;
    private readonly WriteableBitmap _bitmap;
    private readonly object _frameSync = new();
    private readonly uint[] _latestFrame = new uint[NesVideoConstants.PixelsPerFrame];
    private readonly uint[] _openGlUploadScratch = new uint[NesVideoConstants.PixelsPerFrame];
    private readonly int[] _pixelScratch = new int[NesVideoConstants.PixelsPerFrame];
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private readonly MainWindowViewModel _viewModel;
    private VideoOutputSettings _videoSettings = VideoOutputSettings.CreateDefault();

    private SettingsWindow? _settingsWindow;
    private long _frameEpoch;
    private long _latestFrameEpoch;
    private bool _frameDirty;
    private int _uiFrameScheduled;
    private int _fpsFrameCount;
    private double _lastFps;
    private WindowState _windowedStateBeforeFullscreen = WindowState.Normal;
    private string _statusMessage = "Ready.";
    private bool _isClosing;
    private bool _suppressFooterVolumeEvents = true;

    public MainWindow()
    {
        _viewModel = new MainWindowViewModel(ApplicationVersionText, OperatingSystemText);
        InitializeComponent();
        DataContext = _viewModel;
        AppLogger.Info("Main window created.");

        _bitmap = new WriteableBitmap(
            new PixelSize(NesVideoConstants.Width, NesVideoConstants.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        GameImage.Source = _bitmap;
        OpenGlGameView.RendererFailed += OpenGlGameView_OnRendererFailed;
        _emulator = new EmulatorHost(_inputState.GetCombinedState, OnFrameReady, _midiOutput);

        AddHandler(KeyDownEvent, PreviewKeyDownHandler, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, PreviewKeyUpHandler, RoutingStrategies.Tunnel, handledEventsToo: true);

        Opened += (_, _) => CaptureGameInput();
        Closing += MainWindow_OnClosing;
        Activated += (_, _) => CaptureGameInput();
        Deactivated += (_, _) => _inputState.Clear();

        ClearViewport();
        ApplyVideoRenderer();
        UpdateFullscreenPresentation();
        SetStatus("Ready.");
        UpdateUiState();
        _suppressFooterVolumeEvents = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            UpdateFullscreenPresentation();
        }
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _isClosing = true;
        CloseSettingsWindow(prepareForShutdown: true);

        OpenGlGameView.RendererFailed -= OpenGlGameView_OnRendererFailed;
        _inputState.Dispose();
        _emulator.Dispose();
        _midiOutput.Dispose();
    }

    private void PreviewKeyDownHandler(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            ExitFullscreen();
            e.Handled = true;
            return;
        }

        if (!GameViewport.IsKeyboardFocusWithin)
        {
            return;
        }

        _inputState.SetKey(e.Key, true);
        if (_inputState.IsControlKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void PreviewKeyUpHandler(object? sender, KeyEventArgs e)
    {
        if (!GameViewport.IsKeyboardFocusWithin)
        {
            return;
        }

        _inputState.SetKey(e.Key, false);
        if (_inputState.IsControlKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private async void OpenRomButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select a NES ROM",
            FileTypeFilter =
            [
                new FilePickerFileType("NES ROM")
                {
                    Patterns = ["*.nes"]
                }
            ]
        });

        var file = result.Count > 0 ? result[0] : null;
        if (file is not null)
        {
            LoadRom(file.Path.LocalPath);
        }

        CaptureGameInput();
    }

    private void PauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_emulator.HasRomLoaded)
        {
            return;
        }

        _emulator.TogglePause();
        if (!_emulator.IsStopped && !_emulator.IsPaused)
        {
            ResetFpsCounter();
            SetStatus("Emulation resumed.");
        }
        else if (_emulator.IsStopped)
        {
            SetStatus("Emulation restarted.");
        }
        else
        {
            SetStatus("Emulation paused.");
        }

        UpdateUiState();
        CaptureGameInput();
    }

    private void StopButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_emulator.HasRomLoaded)
        {
            return;
        }

        _emulator.Stop();
        ResetFpsCounter();
        ClearViewport();
        SetStatus("Emulation stopped.");
        UpdateUiState();
        CaptureGameInput();
    }

    private void ResetButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_emulator.HasRomLoaded)
        {
            return;
        }

        _emulator.Reset();
        ResetFpsCounter();
        SetStatus("Session reset.");
        UpdateUiState();
        CaptureGameInput();
    }

    private void ExitMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void FullscreenMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void SettingsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _inputState.Clear();

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        CloseSettingsWindow(prepareForShutdown: true);
        _settingsWindow = new SettingsWindow(_emulator, _midiOutput, _inputState, this, SetStatus);
        _settingsWindow.MasterVolumePreviewChanged += SettingsWindow_OnMasterVolumePreviewChanged;
        _settingsWindow.Closed += SettingsWindow_OnClosed;

        AppLogger.Info($"Opening settings window. {AppLogger.GetProcessResourceSummary()}");
        _settingsWindow.ShowForOwner(this);
    }

    private void OpenLogFolderMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            AppLogger.OpenLogDirectory();
            SetStatus($"Opened log folder: {AppLogger.LogDirectoryPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not open log folder.", ex);
            SetStatus($"Could not open log folder. Path: {AppLogger.LogDirectoryPath}");
        }
    }

    private void GameViewport_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CaptureGameInput();
    }

    private void LoadRom(string romPath)
    {
        try
        {
            _emulator.LoadRom(romPath);
            ResetFpsCounter();
            SetStatus($"Loaded {Path.GetFileName(romPath)}.");
            UpdateUiState();
            CaptureGameInput();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Could not load ROM '{romPath}'.", ex);
            SetStatus(ex.Message);
            ClearViewport();
            UpdateUiState();
        }
    }

    private void OnFrameReady(uint[] frame)
    {
        if (_isClosing || _emulator.IsStopped)
        {
            return;
        }

        var epoch = Interlocked.Read(ref _frameEpoch);
        lock (_frameSync)
        {
            Array.Copy(frame, _latestFrame, frame.Length);
            _frameDirty = true;
            _latestFrameEpoch = epoch;
        }

        if (Interlocked.Exchange(ref _uiFrameScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(() => UploadPendingFrame(epoch), DispatcherPriority.Normal);
        }
    }

    private void UploadPendingFrame(long epoch)
    {
        if (_isClosing)
        {
            Interlocked.Exchange(ref _uiFrameScheduled, 0);
            return;
        }

        var renderWithOpenGl = _videoSettings.Renderer == VideoRendererKind.OpenGl;

        lock (_frameSync)
        {
            if (!_frameDirty || epoch != Interlocked.Read(ref _frameEpoch) || _latestFrameEpoch != epoch)
            {
                Interlocked.Exchange(ref _uiFrameScheduled, 0);
                return;
            }

            if (renderWithOpenGl)
            {
                Array.Copy(_latestFrame, _openGlUploadScratch, _latestFrame.Length);
            }
            else
            {
                for (var i = 0; i < _latestFrame.Length; i++)
                {
                    _pixelScratch[i] = unchecked((int)_latestFrame[i]);
                }
            }

            _frameDirty = false;
        }

        if (renderWithOpenGl)
        {
            OpenGlGameView.SubmitFrame(_openGlUploadScratch);
        }
        else
        {
            UploadSoftwareFrameFromScratch();
        }

        _fpsFrameCount++;
        var elapsed = _fpsStopwatch.Elapsed.TotalSeconds;
        if (elapsed >= 1.0)
        {
            _lastFps = _fpsFrameCount / elapsed;
            _fpsFrameCount = 0;
            _fpsStopwatch.Restart();
            RefreshViewModel();
        }

        GameViewport.InvalidateVisual();

        Interlocked.Exchange(ref _uiFrameScheduled, 0);

        lock (_frameSync)
        {
            var currentEpoch = Interlocked.Read(ref _frameEpoch);
            if (_frameDirty
                && _latestFrameEpoch == currentEpoch
                && Interlocked.Exchange(ref _uiFrameScheduled, 1) == 0)
            {
                Dispatcher.UIThread.Post(() => UploadPendingFrame(currentEpoch), DispatcherPriority.Normal);
            }
        }
    }

    private void UpdateUiState()
    {
        RefreshViewModel();
    }

    public VideoOutputSettings GetVideoSettingsSnapshot() => _videoSettings.Clone();

    public bool TryApplyVideoSettings(VideoOutputSettings settings, out string? error)
    {
        _videoSettings = settings.Clone();
        ApplyVideoRenderer();
        error = null;
        return true;
    }

    private void ClearViewport()
    {
        var epoch = Interlocked.Increment(ref _frameEpoch);

        lock (_frameSync)
        {
            Array.Clear(_latestFrame);
            Array.Clear(_openGlUploadScratch);
            Array.Clear(_pixelScratch);
            _frameDirty = false;
            _latestFrameEpoch = epoch;
        }

        Interlocked.Exchange(ref _uiFrameScheduled, 0);

        UploadSoftwareFrameFromScratch();
        OpenGlGameView.ClearFrame();
        GameViewport.InvalidateVisual();
    }

    private void ResetFpsCounter()
    {
        _fpsFrameCount = 0;
        _lastFps = 0;
        _fpsStopwatch.Restart();
        RefreshViewModel();
    }

    private void SetStatus(string message)
    {
        _statusMessage = message;
        RefreshViewModel();
    }

    private void RefreshViewModel()
    {
        if (_isClosing)
        {
            return;
        }

        var audioSettings = _emulator.GetAudioSettingsSnapshot();
        _viewModel.Apply(new MainWindowViewState(
            _emulator.HasRomLoaded,
            _emulator.IsPaused,
            _emulator.IsStopped,
            _emulator.LoadedRomPath,
            _lastFps,
            GetMidiFooterText(),
            _videoSettings.Renderer,
            audioSettings.MasterVolumePercent,
            _statusMessage,
            WindowState == WindowState.FullScreen));

        _suppressFooterVolumeEvents = true;
        try
        {
            FooterVolumeSlider.Value = _viewModel.VolumePercent;
        }
        finally
        {
            _suppressFooterVolumeEvents = false;
        }
    }

    private string GetMidiFooterText()
    {
        var status = _midiOutput.GetStatusText();
        if (string.Equals(status, "MIDI disabled", StringComparison.Ordinal))
        {
            return "Disabled";
        }

        return status.StartsWith("MIDI: ", StringComparison.Ordinal)
            ? status[6..]
            : status;
    }

    private void ApplyVideoRenderer()
    {
        if (_isClosing)
        {
            return;
        }

        var useOpenGl = _videoSettings.Renderer == VideoRendererKind.OpenGl;
        GameImage.IsVisible = !useOpenGl;
        OpenGlGameView.IsVisible = useOpenGl;

        if (useOpenGl)
        {
            lock (_frameSync)
            {
                Array.Copy(_latestFrame, _openGlUploadScratch, _latestFrame.Length);
            }

            OpenGlGameView.SubmitFrame(_openGlUploadScratch);
        }
        else
        {
            CopyLatestFrameToSoftwareScratch();
            UploadSoftwareFrameFromScratch();
        }

        RefreshViewModel();
    }

    private void CopyLatestFrameToSoftwareScratch()
    {
        lock (_frameSync)
        {
            for (var i = 0; i < _latestFrame.Length; i++)
            {
                _pixelScratch[i] = unchecked((int)_latestFrame[i]);
            }
        }
    }

    private void UploadSoftwareFrameFromScratch()
    {
        using var framebuffer = _bitmap.Lock();
        Marshal.Copy(_pixelScratch, 0, framebuffer.Address, _pixelScratch.Length);
        GameImage.InvalidateVisual();
    }

    private void OpenGlGameView_OnRendererFailed(string message)
    {
        if (_isClosing || _videoSettings.Renderer != VideoRendererKind.OpenGl)
        {
            return;
        }

        _videoSettings = VideoOutputSettings.CreateDefault();
        ApplyVideoRenderer();
        _settingsWindow?.RefreshVideoSettings(_videoSettings);
        SetStatus($"{message} Falling back to software rendering.");
    }

    private void CaptureGameInput()
    {
        if (_settingsWindow is null || !_settingsWindow.IsVisible)
        {
            GameViewport.Focus();
        }
    }

    private void FooterVolumeSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressFooterVolumeEvents)
        {
            return;
        }

        ApplyFooterVolume(Math.Clamp((int)Math.Round(e.NewValue), 0, 200));
    }

    private void VolumeDownButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ApplyFooterVolume(Math.Max(0, _emulator.GetAudioSettingsSnapshot().MasterVolumePercent - 5));
    }

    private void VolumeUpButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ApplyFooterVolume(Math.Min(200, _emulator.GetAudioSettingsSnapshot().MasterVolumePercent + 5));
    }

    private void ApplyFooterVolume(int volumePercent)
    {
        var settings = _emulator.GetAudioSettingsSnapshot();
        volumePercent = Math.Clamp(volumePercent, 0, 200);

        if (settings.MasterVolumePercent == volumePercent)
        {
            RefreshViewModel();
            return;
        }

        settings.MasterVolumePercent = volumePercent;
        if (_emulator.TryApplyAudioSettings(settings, out var error))
        {
            _settingsWindow?.RefreshAudioSettings(_emulator.GetAudioSettingsSnapshot());
            RefreshViewModel();
            return;
        }

        SetStatus(error ?? "Could not update the volume.");
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        _windowedStateBeforeFullscreen = WindowState == WindowState.Minimized
            ? WindowState.Normal
            : WindowState;
        WindowState = WindowState.FullScreen;
        CaptureGameInput();
    }

    private void ExitFullscreen()
    {
        WindowState = _windowedStateBeforeFullscreen == WindowState.FullScreen
            ? WindowState.Normal
            : _windowedStateBeforeFullscreen;
        CaptureGameInput();
    }

    private void UpdateFullscreenPresentation()
    {
        var isFullscreen = WindowState == WindowState.FullScreen;

        IsMenuVisible = !isFullscreen;

        if (RootLayoutGrid is not null)
        {
            RootLayoutGrid.Margin = isFullscreen ? FullscreenRootMargin : WindowedRootMargin;
        }

        if (StatusPanelBorder is not null)
        {
            StatusPanelBorder.IsVisible = !isFullscreen;
        }

        if (ViewportShellBorder is not null)
        {
            ViewportShellBorder.Padding = isFullscreen ? FullscreenViewportShellPadding : WindowedViewportShellPadding;
            ViewportShellBorder.CornerRadius = isFullscreen ? FullscreenCornerRadius : WindowedSectionCornerRadius;
        }

        if (GameViewport is not null)
        {
            GameViewport.Padding = isFullscreen ? FullscreenGameViewportPadding : WindowedGameViewportPadding;
            GameViewport.CornerRadius = isFullscreen ? FullscreenCornerRadius : WindowedGameViewportCornerRadius;
            GameViewport.BorderThickness = isFullscreen ? FullscreenBorderThickness : WindowedBorderThickness;
        }

        RefreshViewModel();
    }

    private void CloseSettingsWindow(bool prepareForShutdown)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.MasterVolumePreviewChanged -= SettingsWindow_OnMasterVolumePreviewChanged;
        _settingsWindow.Closed -= SettingsWindow_OnClosed;
        if (prepareForShutdown)
        {
            _settingsWindow.PrepareForShutdown();
        }

        _settingsWindow.Close();
        _settingsWindow = null;
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow settingsWindow && ReferenceEquals(_settingsWindow, settingsWindow))
        {
            settingsWindow.MasterVolumePreviewChanged -= SettingsWindow_OnMasterVolumePreviewChanged;
            settingsWindow.Closed -= SettingsWindow_OnClosed;
            _settingsWindow = null;
        }

        CaptureGameInput();
    }

    private void SettingsWindow_OnMasterVolumePreviewChanged(int volumePercent)
    {
        if (_isClosing)
        {
            return;
        }

        _viewModel.SetPreviewVolume(volumePercent);

        _suppressFooterVolumeEvents = true;
        try
        {
            FooterVolumeSlider.Value = Math.Clamp(volumePercent, 0, 200);
        }
        finally
        {
            _suppressFooterVolumeEvents = false;
        }
    }

    private static string GetApplicationVersionText()
    {
        var version =
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "1.0.0";

        var normalized = version.Split('+', 2)[0];
        return normalized.StartsWith('v') ? normalized : $"v{normalized}";
    }

    private static string GetOperatingSystemText()
    {
        var platform = OperatingSystem.IsWindows()
            ? "Windows"
            : OperatingSystem.IsLinux()
                ? "Linux"
                : OperatingSystem.IsMacOS()
                    ? "macOS"
                    : RuntimeInformation.OSDescription;

        return $"{platform} {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }
}
