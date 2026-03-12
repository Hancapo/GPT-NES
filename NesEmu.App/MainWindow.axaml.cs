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

        Opened += MainWindow_OnOpened;
        Closing += MainWindow_OnClosing;
        Activated += (_, _) => CaptureGameInput();
        Deactivated += (_, _) => _inputState.Clear();

        SetTransportEnabled(false);
        ClearViewport();
        UpdateUiState();
    }

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        CaptureGameInput();

        var romPath = FindDefaultRom();
        if (romPath is not null)
        {
            LoadRom(romPath);
            return;
        }

        StatusText.Text = "Sin ROM cargada.";
        await Task.CompletedTask;
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _emulator.Dispose();
        _midiOutput.Dispose();
    }

    private void PreviewKeyDownHandler(object? sender, KeyEventArgs e)
    {
        _inputState.SetKey(e.Key, true);
        if (IsControlKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void PreviewKeyUpHandler(object? sender, KeyEventArgs e)
    {
        _inputState.SetKey(e.Key, false);
        if (IsControlKey(e.Key))
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
            Title = "Selecciona una ROM NES",
            FileTypeFilter =
            [
                new FilePickerFileType("ROM NES")
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
        StatusText.Text = "Sesión reiniciada.";
        UpdateUiState();
        CaptureGameInput();
    }

    private void ExitMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void MidiSettingsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new MidiSettingsWindow(_midiOutput.GetSettingsSnapshot(), _midiOutput.GetDevices());
            var settings = await dialog.ShowDialog<MidiOutputSettings?>(this);
            if (settings is null)
            {
                CaptureGameInput();
                return;
            }

            if (_midiOutput.TryApplySettings(settings, out var error))
            {
                StatusText.Text = _midiOutput.GetStatusText();
            }
            else
            {
                StatusText.Text = error ?? "No se pudo activar la salida MIDI.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"MIDI: {ex.Message}";
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
            StatusText.Text = $"ROM cargada: {Path.GetFileName(romPath)}";
            SetTransportEnabled(true);
            UpdateUiState();
            CaptureGameInput();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
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
        RenderStatusText.Text = "Imagen crisp";

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
            ? "Sin ROM"
            : stopped ? "Detenido"
            : paused ? "Pausado"
            : "En ejecución";

        RomText.Text = hasRom
            ? $"ROM: {Path.GetFileName(_emulator.LoadedRomPath)}"
            : "ROM: -";

        PauseButton.Content = stopped ? "Iniciar" : paused ? "Reanudar" : "Pausar";
        PauseMenuItem.Header = stopped ? "_Iniciar" : paused ? "_Reanudar" : "_Pausar";

        if (!hasRom)
        {
            StatusText.Text = "Sin ROM cargada.";
            ShowOverlay("NesEmu", "Abre una ROM NES para empezar.", showOpenButton: true);
        }
        else if (stopped)
        {
            StatusText.Text = "Emulación detenida.";
            ShowOverlay("Detenido", "La sesión está detenida. Pulsa Iniciar para volver a ejecutar la ROM.", showOpenButton: false);
        }
        else if (paused)
        {
            StatusText.Text = "Emulación en pausa.";
            ShowOverlay("Pausa", "La emulación está pausada. Pulsa Reanudar para continuar.", showOpenButton: false);
        }
        else
        {
            StatusText.Text = $"Ejecutando {Path.GetFileName(_emulator.LoadedRomPath)}";
            OverlayPanel.IsVisible = false;
        }

        if (!running && !paused)
        {
            FpsText.Text = $"FPS: {_lastFps:0.0}";
        }

        SetTransportEnabled(hasRom);
        StopButton.IsEnabled = hasRom;
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
        PauseButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        ResetButton.IsEnabled = enabled;
        PauseMenuItem.IsEnabled = enabled;
        StopMenuItem.IsEnabled = enabled;
        ResetMenuItem.IsEnabled = enabled;
    }

    private void CaptureGameInput()
    {
        GameViewport.Focus();
    }

    private static bool IsControlKey(Key key) =>
        key is Key.Up or Key.Down or Key.Left or Key.Right
            or Key.W or Key.A or Key.S or Key.D
            or Key.Z or Key.X or Key.J or Key.K
            or Key.Enter or Key.LeftShift or Key.RightShift;

    private static string? FindDefaultRom()
    {
        foreach (var root in CandidateRoots())
        {
            var rom = Directory.EnumerateFiles(root, "*.nes", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (rom is not null)
            {
                return rom;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(root);
            for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }
            }
        }
    }
}
