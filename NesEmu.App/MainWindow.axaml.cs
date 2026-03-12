using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NesEmu.Core;

namespace NesEmu.App;

public partial class MainWindow : Window
{
    private readonly InputStateSource _inputState = new();
    private readonly MidiOutputService _midiOutput = new();
    private readonly EmulatorHost _emulator;
    private readonly WriteableBitmap _bitmap;
    private readonly object _frameSync = new();
    private readonly uint[] _latestFrame = new uint[NesVideoConstants.PixelsPerFrame];
    private readonly int[] _pixelScratch = new int[NesVideoConstants.PixelsPerFrame];
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();

    private SettingsWindow? _settingsWindow;
    private bool _frameDirty;
    private int _uiFrameScheduled;
    private int _fpsFrameCount;
    private double _lastFps;

    public MainWindow()
    {
        InitializeComponent();

        _bitmap = new WriteableBitmap(
            new PixelSize(NesVideoConstants.Width, NesVideoConstants.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        GameImage.Source = _bitmap;
        _emulator = new EmulatorHost(_inputState.GetCombinedState, OnFrameReady, _midiOutput);

        AddHandler(KeyDownEvent, PreviewKeyDownHandler, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, PreviewKeyUpHandler, RoutingStrategies.Tunnel, handledEventsToo: true);

        Opened += (_, _) => CaptureGameInput();
        Closing += MainWindow_OnClosing;
        Activated += (_, _) => CaptureGameInput();
        Deactivated += (_, _) => _inputState.Clear();

        SetTransportEnabled(false);
        ClearViewport();
        SetStatus("Ready.");
        UpdateUiState();
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _settingsWindow?.Close();
        _emulator.Dispose();
        _midiOutput.Dispose();
    }

    private void PreviewKeyDownHandler(object? sender, KeyEventArgs e)
    {
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

    private void SettingsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _inputState.Clear();

        _settingsWindow = new SettingsWindow(_emulator, _midiOutput, _inputState, SetStatus);
        _settingsWindow.Closed += SettingsWindow_OnClosed;
        _settingsWindow.Show(this);
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Closed -= SettingsWindow_OnClosed;
            _settingsWindow = null;
        }

        CaptureGameInput();
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
            SetTransportEnabled(true);
            UpdateUiState();
            CaptureGameInput();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            SetTransportEnabled(false);
            ClearViewport();
            UpdateUiState();
        }
    }

    private void OnFrameReady(uint[] frame)
    {
        lock (_frameSync)
        {
            Array.Copy(frame, _latestFrame, frame.Length);
            _frameDirty = true;
        }

        if (Interlocked.Exchange(ref _uiFrameScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(UploadPendingFrame, DispatcherPriority.Normal);
        }
    }

    private void UploadPendingFrame()
    {
        lock (_frameSync)
        {
            if (!_frameDirty)
            {
                Interlocked.Exchange(ref _uiFrameScheduled, 0);
                return;
            }

            for (var i = 0; i < _latestFrame.Length; i++)
            {
                _pixelScratch[i] = unchecked((int)_latestFrame[i]);
            }

            _frameDirty = false;
        }

        using (var framebuffer = _bitmap.Lock())
        {
            Marshal.Copy(_pixelScratch, 0, framebuffer.Address, _pixelScratch.Length);
        }

        _fpsFrameCount++;
        var elapsed = _fpsStopwatch.Elapsed.TotalSeconds;
        if (elapsed >= 1.0)
        {
            _lastFps = _fpsFrameCount / elapsed;
            _fpsFrameCount = 0;
            _fpsStopwatch.Restart();
            FpsText.Text = $"FPS: {_lastFps:0.0}";
        }

        GameImage.InvalidateVisual();
        GameViewport.InvalidateVisual();
        OverlayPanel.IsVisible = false;

        Interlocked.Exchange(ref _uiFrameScheduled, 0);

        lock (_frameSync)
        {
            if (_frameDirty && Interlocked.Exchange(ref _uiFrameScheduled, 1) == 0)
            {
                Dispatcher.UIThread.Post(UploadPendingFrame, DispatcherPriority.Normal);
            }
        }
    }

    private void UpdateUiState()
    {
        var hasRom = _emulator.HasRomLoaded;
        var paused = hasRom && _emulator.IsPaused;
        var stopped = hasRom && _emulator.IsStopped;
        var running = hasRom && !paused && !stopped;

        RunStateText.Text = !hasRom
            ? "No ROM"
            : stopped ? "Stopped"
            : paused ? "Paused"
            : "Running";

        RomText.Text = hasRom
            ? $"ROM: {Path.GetFileName(_emulator.LoadedRomPath)}"
            : "ROM: -";

        PauseMenuItem.Header = stopped ? "_Start" : paused ? "_Resume" : "_Pause";

        if (!hasRom)
        {
            ShowOverlay("NesEmu", "Open a NES ROM to get started.", showOpenButton: true);
        }
        else if (stopped)
        {
            ShowOverlay("Stopped", "The session is stopped. Press Start to run the ROM again.", showOpenButton: false);
        }
        else if (paused)
        {
            ShowOverlay("Paused", "The emulation is paused. Press Resume to continue.", showOpenButton: false);
        }
        else
        {
            OverlayPanel.IsVisible = false;
        }

        if (!running && !paused)
        {
            FpsText.Text = $"FPS: {_lastFps:0.0}";
        }

        SetTransportEnabled(hasRom);
        StopMenuItem.IsEnabled = hasRom;
        Title = hasRom ? $"NesEmu - {Path.GetFileName(_emulator.LoadedRomPath)}" : "NesEmu";
    }

    private void ShowOverlay(string title, string body, bool showOpenButton)
    {
        OverlayTitleText.Text = title;
        OverlayBodyText.Text = body;
        OverlayOpenButton.IsVisible = showOpenButton;
        OverlayPanel.IsVisible = true;
    }

    private void ClearViewport()
    {
        Array.Clear(_pixelScratch);
        using var framebuffer = _bitmap.Lock();
        Marshal.Copy(_pixelScratch, 0, framebuffer.Address, _pixelScratch.Length);
        GameImage.InvalidateVisual();
    }

    private void ResetFpsCounter()
    {
        _fpsFrameCount = 0;
        _lastFps = 0;
        _fpsStopwatch.Restart();
        FpsText.Text = "FPS: 0.0";
    }

    private void SetTransportEnabled(bool enabled)
    {
        PauseMenuItem.IsEnabled = enabled;
        StopMenuItem.IsEnabled = enabled;
        ResetMenuItem.IsEnabled = enabled;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void CaptureGameInput()
    {
        if (_settingsWindow is null || !_settingsWindow.IsVisible)
        {
            GameViewport.Focus();
        }
    }
}
