using Avalonia;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShadWindow = ShadUI.Window;

namespace NesEmu.App;

public partial class SettingsWindow : ShadWindow
{
    private enum SettingsSection
    {
        Video,
        Audio,
        Controls,
        Midi
    }

    private readonly EmulatorHost? _emulator;
    private readonly MidiOutputService? _midiOutput;
    private readonly InputStateSource? _inputState;
    private readonly IVideoOutputSettingsHost? _videoOutput;
    private readonly Action<string>? _statusSink;

    private CancellationTokenSource? _audioSettingsApplyCancellation;
    private CancellationTokenSource? _midiSettingsApplyCancellation;
    private CancellationTokenSource? _inputSettingsApplyCancellation;
    private readonly DispatcherTimer _controllerBindingCaptureTimer;
    private string? _pendingKeyboardBindingAction;
    private string? _pendingControllerBindingAction;
    private SettingsSection _activeSection = SettingsSection.Audio;
    private bool _suppressEvents = true;

    public SettingsWindow()
    {
        _controllerBindingCaptureTimer = CreateControllerBindingCaptureTimer();
        InitializeComponent();
        AddHandler(KeyDownEvent, SettingsWindow_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, SettingsWindow_PointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        SelectSection(SettingsSection.Video);
        InitializeOptions();
        LoadVideoSettings(VideoOutputSettings.CreateDefault());
        LoadAudioSettings(AudioOutputSettings.CreateDefault());
        LoadInputSettings(InputSettings.CreateDefault(), DefaultControllerDevices);
        LoadMidiSettings(MidiOutputSettings.CreateDefault(), [new MidiOutputDeviceInfo(-1, "No MIDI output")]);
        _suppressEvents = false;
    }

    public SettingsWindow(
        EmulatorHost emulator,
        MidiOutputService midiOutput,
        InputStateSource inputState,
        IVideoOutputSettingsHost videoOutput,
        Action<string> statusSink)
    {
        _controllerBindingCaptureTimer = CreateControllerBindingCaptureTimer();
        _emulator = emulator;
        _midiOutput = midiOutput;
        _inputState = inputState;
        _videoOutput = videoOutput;
        _statusSink = statusSink;

        InitializeComponent();
        AddHandler(KeyDownEvent, SettingsWindow_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, SettingsWindow_PointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        SelectSection(SettingsSection.Video);
        InitializeOptions();
        LoadSettingsFromServices();
        _suppressEvents = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _audioSettingsApplyCancellation?.Cancel();
        _audioSettingsApplyCancellation?.Dispose();
        _midiSettingsApplyCancellation?.Cancel();
        _midiSettingsApplyCancellation?.Dispose();
        _inputSettingsApplyCancellation?.Cancel();
        _inputSettingsApplyCancellation?.Dispose();
        _controllerBindingCaptureTimer.Stop();
        _inputState?.CancelControllerBindingCapture();
        base.OnClosed(e);
    }

    private static IReadOnlyList<ControllerDeviceInfo> DefaultControllerDevices =>
        [
            new ControllerDeviceInfo(ControllerDeviceInfo.NoneId, "No controller", ControllerDeviceSource.None, -1),
            new ControllerDeviceInfo(ControllerDeviceInfo.AutoId, "Auto detect", ControllerDeviceSource.Auto, -1)
        ];

    private void InitializeOptions()
    {
        AudioApiComboBox.ItemsSource = AudioBackendCatalog.Options;
        VideoRendererComboBox.ItemsSource = VideoRendererCatalog.Options;
        ControllerAnalogDPadComboBox.ItemsSource = ControllerAnalogDPadCatalog.Options;
        Pulse1ProgramComboBox.ItemsSource = MidiCatalog.Programs;
        Pulse2ProgramComboBox.ItemsSource = MidiCatalog.Programs;
        TriangleProgramComboBox.ItemsSource = MidiCatalog.Programs;
        NoiseDrumComboBox.ItemsSource = MidiCatalog.PercussionNotes;
        DmcDrumComboBox.ItemsSource = MidiCatalog.PercussionNotes;
    }

    private void LoadSettingsFromServices()
    {
        if (_emulator is null || _midiOutput is null || _inputState is null)
        {
            return;
        }

        _suppressEvents = true;

        try
        {
            LoadVideoSettings(_videoOutput?.GetVideoSettingsSnapshot() ?? VideoOutputSettings.CreateDefault());
            LoadAudioSettings(_emulator.GetAudioSettingsSnapshot());
            LoadInputSettings(_inputState.GetSettingsSnapshot(), _inputState.GetAvailableControllers());
            LoadMidiSettings(_midiOutput.GetSettingsSnapshot(), _midiOutput.GetDevices());
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void LoadAudioSettings(AudioOutputSettings settings)
    {
        var selectedBackend = AudioBackendCatalog.Options.FirstOrDefault(option => option.Backend == settings.Backend)
            ?? AudioBackendCatalog.Options.First();

        AudioApiComboBox.SelectedItem = selectedBackend;
        AudioLatencySlider.Value = settings.OutputLatencyMilliseconds;
        AudioVolumeSlider.Value = settings.MasterVolumePercent;
        UpdateAudioLabels();
        UpdateAudioDescription();
    }

    public void RefreshVideoSettings(VideoOutputSettings settings)
    {
        _suppressEvents = true;
        try
        {
            LoadVideoSettings(settings);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void LoadVideoSettings(VideoOutputSettings settings)
    {
        var selected = VideoRendererCatalog.Options.FirstOrDefault(option => option.Renderer == settings.Renderer)
            ?? VideoRendererCatalog.Options.First();

        VideoRendererComboBox.SelectedItem = selected;
        VideoRendererDescriptionText.Text = selected.Description;
    }

    private void LoadInputSettings(InputSettings settings, IReadOnlyList<ControllerDeviceInfo> devices)
    {
        ControllerDeviceComboBox.ItemsSource = devices;
        ControllerDeviceComboBox.SelectedItem = devices.FirstOrDefault(device => device.Id == settings.SelectedControllerId) ?? devices.FirstOrDefault();
        ControllerAnalogDPadComboBox.SelectedItem = ControllerAnalogDPadCatalog.Options.FirstOrDefault(option => option.Mode == settings.AnalogDPadMode)
            ?? ControllerAnalogDPadCatalog.Options.First();

        SetKeyboardBindingButton(KeyboardAButton, settings.KeyboardAKey);
        SetKeyboardBindingButton(KeyboardBButton, settings.KeyboardBKey);
        SetKeyboardBindingButton(KeyboardSelectButton, settings.KeyboardSelectKey);
        SetKeyboardBindingButton(KeyboardStartButton, settings.KeyboardStartKey);
        SetKeyboardBindingButton(KeyboardUpButton, settings.KeyboardUpKey);
        SetKeyboardBindingButton(KeyboardDownButton, settings.KeyboardDownKey);
        SetKeyboardBindingButton(KeyboardLeftButton, settings.KeyboardLeftKey);
        SetKeyboardBindingButton(KeyboardRightButton, settings.KeyboardRightKey);
        UpdateKeyboardBindingButtonStates();
        UpdateKeyboardCaptureText();

        RefreshControllerBindingOptions(settings.SelectedControllerId, settings);
    }

    private void LoadMidiSettings(MidiOutputSettings settings, IReadOnlyList<MidiOutputDeviceInfo> devices)
    {
        OutputDeviceComboBox.ItemsSource = devices;

        EnableMidiCheckBox.IsChecked = settings.Enabled;
        MusicOnlyCheckBox.IsChecked = settings.MusicOnlyFilter;
        PercussionCheckBox.IsChecked = settings.SendPercussion;

        Pulse1EnabledCheckBox.IsChecked = settings.Pulse1Enabled;
        Pulse2EnabledCheckBox.IsChecked = settings.Pulse2Enabled;
        TriangleEnabledCheckBox.IsChecked = settings.TriangleEnabled;
        NoiseEnabledCheckBox.IsChecked = settings.NoiseEnabled;
        DmcEnabledCheckBox.IsChecked = settings.DmcEnabled;

        Pulse1VolumeSlider.Value = settings.Pulse1VolumePercent;
        Pulse2VolumeSlider.Value = settings.Pulse2VolumePercent;
        TriangleVolumeSlider.Value = settings.TriangleVolumePercent;
        NoiseVolumeSlider.Value = settings.NoiseVolumePercent;
        DmcVolumeSlider.Value = settings.DmcVolumePercent;
        MidiSyncOffsetSlider.Value = settings.MidiSyncOffsetMilliseconds;

        OutputDeviceComboBox.SelectedItem = devices.FirstOrDefault(device => device.DeviceIndex == settings.DeviceIndex) ?? devices.FirstOrDefault();
        Pulse1ProgramComboBox.SelectedItem = MidiCatalog.Programs.FirstOrDefault(program => program.ProgramNumber == settings.Pulse1Program) ?? MidiCatalog.Programs.First();
        Pulse2ProgramComboBox.SelectedItem = MidiCatalog.Programs.FirstOrDefault(program => program.ProgramNumber == settings.Pulse2Program) ?? MidiCatalog.Programs.First();
        TriangleProgramComboBox.SelectedItem = MidiCatalog.Programs.FirstOrDefault(program => program.ProgramNumber == settings.TriangleProgram) ?? MidiCatalog.Programs.First();
        NoiseDrumComboBox.SelectedItem = MidiCatalog.PercussionNotes.FirstOrDefault(note => note.NoteNumber == settings.NoiseDrumNote) ?? MidiCatalog.PercussionNotes.First();
        DmcDrumComboBox.SelectedItem = MidiCatalog.PercussionNotes.FirstOrDefault(note => note.NoteNumber == settings.DmcDrumNote) ?? MidiCatalog.PercussionNotes.First();

        UpdateMidiLabels();
    }

    private void AudioSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateAudioLabels();
        ScheduleAudioSettingsApply();
    }

    private void AudioSelection_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        UpdateAudioDescription();
        ScheduleAudioSettingsApply();
    }

    private void VideoSelection_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        UpdateVideoDescription();
        ApplyVideoSettingsFromControls();
    }

    private void ControllerDeviceComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        CancelControllerBindingCapture();
        var currentSettings = BuildInputSettingsFromControls();
        RefreshControllerBindingOptions(GetSelectedControllerId(), currentSettings);
        ScheduleInputSettingsApply();
    }

    private void InputSelection_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleInputSettingsApply();
    }

    private void KeyboardBindingButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
        {
            return;
        }

        CancelControllerBindingCapture();
        _pendingKeyboardBindingAction = action;
        UpdateKeyboardBindingButtonStates();
        UpdateKeyboardCaptureText();
        button.Focus();
    }

    private void ControllerBindingButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
        {
            return;
        }

        _pendingKeyboardBindingAction = null;
        UpdateKeyboardBindingButtonStates();
        UpdateKeyboardCaptureText();

        _pendingControllerBindingAction = action;
        _inputState?.BeginControllerBindingCapture(GetSelectedControllerId());
        UpdateControllerBindingButtonStates();
        UpdateControllerCaptureText();
        button.Focus();
    }

    private void MidiToggle_OnChanged(object? sender, RoutedEventArgs e)
    {
        ScheduleMidiSettingsApply();
    }

    private void MidiSelection_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleMidiSettingsApply();
    }

    private void MidiSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateMidiLabels();
        ScheduleMidiSettingsApply();
    }

    private void RestoreDefaultsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _audioSettingsApplyCancellation?.Cancel();
        _midiSettingsApplyCancellation?.Cancel();
        _inputSettingsApplyCancellation?.Cancel();

        _suppressEvents = true;
        try
        {
            LoadVideoSettings(VideoOutputSettings.CreateDefault());
            LoadAudioSettings(AudioOutputSettings.CreateDefault());
            LoadInputSettings(InputSettings.CreateDefault(), _inputState?.GetAvailableControllers() ?? DefaultControllerDevices);
            LoadMidiSettings(MidiOutputSettings.CreateDefault(), _midiOutput?.GetDevices() ?? [new MidiOutputDeviceInfo(-1, "No MIDI output")]);
        }
        finally
        {
            _suppressEvents = false;
        }

        ApplyVideoSettingsFromControls();
        ApplyAudioSettingsFromControls();
        ApplyInputSettingsFromControls();
        ApplyMidiSettingsFromControls();
        _statusSink?.Invoke("Settings restored to defaults.");
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SettingsWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_pendingKeyboardBindingAction is null && _pendingControllerBindingAction is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelPendingBindingCapture();
            e.Handled = true;
            return;
        }

        if (_pendingKeyboardBindingAction is null)
        {
            return;
        }

        AssignPendingKeyboardBinding(e.Key);
        e.Handled = true;
    }

    private void SettingsSectionItem_OnChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string tag })
        {
            return;
        }

        var section = tag switch
        {
            "Video" => SettingsSection.Video,
            "Audio" => SettingsSection.Audio,
            "Controls" => SettingsSection.Controls,
            "MIDI" => SettingsSection.Midi,
            _ => SettingsSection.Video
        };

        SelectSection(section);
    }

    private void SettingsWindow_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < double.Epsilon)
        {
            return;
        }

        if (e.Source is Visual source
            && (source.FindAncestorOfType<ComboBoxItem>() is not null
                || source.FindAncestorOfType<ScrollBar>() is not null))
        {
            return;
        }

        var scrollViewer = GetActiveScrollViewer();
        if (scrollViewer is null || !scrollViewer.IsVisible)
        {
            return;
        }

        var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        if (maxOffsetY <= 0)
        {
            return;
        }

        var proposedOffsetY = Math.Clamp(scrollViewer.Offset.Y - (e.Delta.Y * 56.0), 0, maxOffsetY);
        if (Math.Abs(proposedOffsetY - scrollViewer.Offset.Y) < 0.5)
        {
            return;
        }

        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, proposedOffsetY);
        e.Handled = true;
    }

    private void UpdateAudioLabels()
    {
        if (AudioLatencySlider is null || AudioLatencyText is null || AudioVolumeSlider is null || AudioVolumeText is null)
        {
            return;
        }

        AudioLatencyText.Text = $"{ReadAudioLatency(AudioLatencySlider)} ms";
        AudioVolumeText.Text = FormatPercent(AudioVolumeSlider.Value);
    }

    private void UpdateAudioDescription()
    {
        if (AudioApiDescriptionText is null)
        {
            return;
        }

        var selected = AudioApiComboBox.SelectedItem as AudioBackendOption;
        AudioApiDescriptionText.Text = selected?.Description ?? string.Empty;
    }

    private void UpdateVideoDescription()
    {
        if (VideoRendererDescriptionText is null)
        {
            return;
        }

        var selected = VideoRendererComboBox.SelectedItem as VideoRendererOption;
        VideoRendererDescriptionText.Text = selected?.Description ?? string.Empty;
    }

    private void UpdateMidiLabels()
    {
        if (Pulse1VolumeSlider is null
            || Pulse1VolumeText is null
            || Pulse2VolumeSlider is null
            || Pulse2VolumeText is null
            || TriangleVolumeSlider is null
            || TriangleVolumeText is null
            || NoiseVolumeSlider is null
            || NoiseVolumeText is null
            || DmcVolumeSlider is null
            || DmcVolumeText is null
            || MidiSyncOffsetSlider is null
            || MidiSyncOffsetText is null)
        {
            return;
        }

        Pulse1VolumeText.Text = FormatPercent(Pulse1VolumeSlider.Value);
        Pulse2VolumeText.Text = FormatPercent(Pulse2VolumeSlider.Value);
        TriangleVolumeText.Text = FormatPercent(TriangleVolumeSlider.Value);
        NoiseVolumeText.Text = FormatPercent(NoiseVolumeSlider.Value);
        DmcVolumeText.Text = FormatPercent(DmcVolumeSlider.Value);
        MidiSyncOffsetText.Text = FormatOffset(MidiSyncOffsetSlider.Value);
    }

    private void RefreshControllerBindingOptions(string? controllerId, InputSettings desiredSettings)
    {
        SetControllerBindingButton(ControllerAButton, desiredSettings.ControllerA);
        SetControllerBindingButton(ControllerBButton, desiredSettings.ControllerB);
        SetControllerBindingButton(ControllerSelectButton, desiredSettings.ControllerSelect);
        SetControllerBindingButton(ControllerStartButton, desiredSettings.ControllerStart);
        SetControllerBindingButton(ControllerUpButton, desiredSettings.ControllerUp);
        SetControllerBindingButton(ControllerDownButton, desiredSettings.ControllerDown);
        SetControllerBindingButton(ControllerLeftButton, desiredSettings.ControllerLeft);
        SetControllerBindingButton(ControllerRightButton, desiredSettings.ControllerRight);
        UpdateControllerBindingButtonStates();
        UpdateControllerCaptureText();
    }

    private void AssignPendingKeyboardBinding(Key key)
    {
        if (_pendingKeyboardBindingAction is null)
        {
            return;
        }

        var button = GetKeyboardBindingButton(_pendingKeyboardBindingAction);
        if (button is null)
        {
            _pendingKeyboardBindingAction = null;
            UpdateKeyboardCaptureText();
            return;
        }

        SetKeyboardBindingButton(button, key);
        _pendingKeyboardBindingAction = null;
        UpdateKeyboardBindingButtonStates();
        UpdateKeyboardCaptureText();
        ScheduleInputSettingsApply();
    }

    private void AssignPendingControllerBinding(ControllerBinding binding)
    {
        if (_pendingControllerBindingAction is null)
        {
            return;
        }

        var button = GetControllerBindingButton(_pendingControllerBindingAction);
        if (button is null)
        {
            CancelControllerBindingCapture();
            return;
        }

        SetControllerBindingButton(button, binding);
        CancelControllerBindingCapture();
        ScheduleInputSettingsApply();
    }

    private void SetKeyboardBindingButton(Button button, Key key)
    {
        button.Tag = button.Tag is string action
            ? action
            : (button.Name ?? string.Empty).Replace("Keyboard", string.Empty).Replace("Button", string.Empty);
        button.Content = FormatKey(key);
        button.CommandParameter = key;
    }

    private void SetControllerBindingButton(Button button, ControllerBinding binding)
    {
        button.Tag = button.Tag is string action
            ? action
            : (button.Name ?? string.Empty).Replace("Controller", string.Empty).Replace("Button", string.Empty);
        button.Content = FormatControllerBinding(binding);
        button.CommandParameter = binding;
    }

    private void UpdateKeyboardBindingButtonStates()
    {
        UpdateKeyboardBindingButtonState(KeyboardAButton, "A");
        UpdateKeyboardBindingButtonState(KeyboardBButton, "B");
        UpdateKeyboardBindingButtonState(KeyboardSelectButton, "Select");
        UpdateKeyboardBindingButtonState(KeyboardStartButton, "Start");
        UpdateKeyboardBindingButtonState(KeyboardUpButton, "Up");
        UpdateKeyboardBindingButtonState(KeyboardDownButton, "Down");
        UpdateKeyboardBindingButtonState(KeyboardLeftButton, "Left");
        UpdateKeyboardBindingButtonState(KeyboardRightButton, "Right");
    }

    private void UpdateControllerBindingButtonStates()
    {
        UpdateControllerBindingButtonState(ControllerAButton, "A");
        UpdateControllerBindingButtonState(ControllerBButton, "B");
        UpdateControllerBindingButtonState(ControllerSelectButton, "Select");
        UpdateControllerBindingButtonState(ControllerStartButton, "Start");
        UpdateControllerBindingButtonState(ControllerUpButton, "Up");
        UpdateControllerBindingButtonState(ControllerDownButton, "Down");
        UpdateControllerBindingButtonState(ControllerLeftButton, "Left");
        UpdateControllerBindingButtonState(ControllerRightButton, "Right");
    }

    private void UpdateKeyboardBindingButtonState(Button button, string action)
    {
        var waiting = string.Equals(_pendingKeyboardBindingAction, action, StringComparison.Ordinal);
        button.Content = waiting ? "Press a key..." : FormatKey(ReadButtonKey(button));
    }

    private void UpdateControllerBindingButtonState(Button button, string action)
    {
        var waiting = string.Equals(_pendingControllerBindingAction, action, StringComparison.Ordinal);
        button.Content = waiting ? "Press a button..." : FormatControllerBinding(ReadControllerBinding(button));
    }

    private void UpdateKeyboardCaptureText()
    {
        KeyboardCaptureText.Text = _pendingKeyboardBindingAction is null
            ? "Click a command, then press the key you want to assign."
            : $"Press a key for {_pendingKeyboardBindingAction}. Press Esc to cancel.";
    }

    private void UpdateControllerCaptureText()
    {
        ControllerCaptureText.Text = _pendingControllerBindingAction is null
            ? "Click a command, then press a controller button or move a stick."
            : $"Press a controller input for {_pendingControllerBindingAction}. Press Esc to cancel.";
    }

    private void ScheduleAudioSettingsApply()
    {
        if (_suppressEvents)
        {
            return;
        }

        _audioSettingsApplyCancellation?.Cancel();
        _audioSettingsApplyCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _audioSettingsApplyCancellation = cancellation;
        _ = ApplyAudioSettingsDelayedAsync(cancellation.Token);
    }

    private async Task ApplyAudioSettingsDelayedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            ApplyAudioSettingsFromControls();
        }
    }

    private void ApplyAudioSettingsFromControls()
    {
        if (_emulator is null)
        {
            return;
        }

        var settings = new AudioOutputSettings
        {
            Backend = (AudioApiComboBox.SelectedItem as AudioBackendOption)?.Backend ?? AudioBackendKind.OpenAl,
            OutputLatencyMilliseconds = ReadAudioLatency(AudioLatencySlider),
            MasterVolumePercent = ReadPercent(AudioVolumeSlider)
        };

        if (_emulator.TryApplyAudioSettings(settings, out var error))
        {
            var backendName = (AudioApiComboBox.SelectedItem as AudioBackendOption)?.DisplayName ?? "OpenAL";
            _statusSink?.Invoke($"Audio updated: {backendName}, {settings.OutputLatencyMilliseconds} ms, {settings.MasterVolumePercent}% volume.");
            return;
        }

        _statusSink?.Invoke(error ?? "Could not apply audio settings.");

        _suppressEvents = true;
        try
        {
            LoadAudioSettings(_emulator.GetAudioSettingsSnapshot());
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void ApplyVideoSettingsFromControls()
    {
        if (_videoOutput is null)
        {
            return;
        }

        var selected = VideoRendererComboBox.SelectedItem as VideoRendererOption;
        var settings = new VideoOutputSettings
        {
            Renderer = selected?.Renderer ?? VideoRendererKind.Software
        };

        if (_videoOutput.TryApplyVideoSettings(settings, out var error))
        {
            _statusSink?.Invoke($"Video renderer updated: {selected?.DisplayName ?? "Software"}.");
            return;
        }

        _statusSink?.Invoke(error ?? "Could not apply video renderer.");
        RefreshVideoSettings(_videoOutput.GetVideoSettingsSnapshot());
    }

    private void ScheduleInputSettingsApply()
    {
        if (_suppressEvents)
        {
            return;
        }

        _inputSettingsApplyCancellation?.Cancel();
        _inputSettingsApplyCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _inputSettingsApplyCancellation = cancellation;
        _ = ApplyInputSettingsDelayedAsync(cancellation.Token);
    }

    private async Task ApplyInputSettingsDelayedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            ApplyInputSettingsFromControls();
        }
    }

    private void ApplyInputSettingsFromControls()
    {
        if (_inputState is null)
        {
            return;
        }

        var settings = BuildInputSettingsFromControls();
        _inputState.ApplySettings(settings);

        var deviceName = (ControllerDeviceComboBox.SelectedItem as ControllerDeviceInfo)?.DisplayName ?? "Auto detect";
        _statusSink?.Invoke($"Controls updated. Active controller: {deviceName}.");
    }

    private InputSettings BuildInputSettingsFromControls()
    {
        var defaults = InputSettings.CreateDefault();

        return new InputSettings
        {
            SelectedControllerId = GetSelectedControllerId(),
            AnalogDPadMode = (ControllerAnalogDPadComboBox.SelectedItem as ControllerAnalogDPadOption)?.Mode ?? defaults.AnalogDPadMode,
            KeyboardAKey = ReadButtonKey(KeyboardAButton, defaults.KeyboardAKey),
            KeyboardBKey = ReadButtonKey(KeyboardBButton, defaults.KeyboardBKey),
            KeyboardSelectKey = ReadButtonKey(KeyboardSelectButton, defaults.KeyboardSelectKey),
            KeyboardStartKey = ReadButtonKey(KeyboardStartButton, defaults.KeyboardStartKey),
            KeyboardUpKey = ReadButtonKey(KeyboardUpButton, defaults.KeyboardUpKey),
            KeyboardDownKey = ReadButtonKey(KeyboardDownButton, defaults.KeyboardDownKey),
            KeyboardLeftKey = ReadButtonKey(KeyboardLeftButton, defaults.KeyboardLeftKey),
            KeyboardRightKey = ReadButtonKey(KeyboardRightButton, defaults.KeyboardRightKey),
            ControllerA = ReadControllerBinding(ControllerAButton, defaults.ControllerA),
            ControllerB = ReadControllerBinding(ControllerBButton, defaults.ControllerB),
            ControllerSelect = ReadControllerBinding(ControllerSelectButton, defaults.ControllerSelect),
            ControllerStart = ReadControllerBinding(ControllerStartButton, defaults.ControllerStart),
            ControllerUp = ReadControllerBinding(ControllerUpButton, defaults.ControllerUp),
            ControllerDown = ReadControllerBinding(ControllerDownButton, defaults.ControllerDown),
            ControllerLeft = ReadControllerBinding(ControllerLeftButton, defaults.ControllerLeft),
            ControllerRight = ReadControllerBinding(ControllerRightButton, defaults.ControllerRight)
        };
    }

    private void ScheduleMidiSettingsApply()
    {
        if (_suppressEvents)
        {
            return;
        }

        _midiSettingsApplyCancellation?.Cancel();
        _midiSettingsApplyCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _midiSettingsApplyCancellation = cancellation;
        _ = ApplyMidiSettingsDelayedAsync(cancellation.Token);
    }

    private async Task ApplyMidiSettingsDelayedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            ApplyMidiSettingsFromControls();
        }
    }

    private void ApplyMidiSettingsFromControls()
    {
        if (_midiOutput is null)
        {
            return;
        }

        var settings = new MidiOutputSettings
        {
            Enabled = EnableMidiCheckBox.IsChecked == true,
            DeviceIndex = (OutputDeviceComboBox.SelectedItem as MidiOutputDeviceInfo)?.DeviceIndex ?? -1,
            MusicOnlyFilter = MusicOnlyCheckBox.IsChecked != false,
            SendPercussion = PercussionCheckBox.IsChecked != false,
            Pulse1Enabled = Pulse1EnabledCheckBox.IsChecked != false,
            Pulse2Enabled = Pulse2EnabledCheckBox.IsChecked != false,
            TriangleEnabled = TriangleEnabledCheckBox.IsChecked != false,
            NoiseEnabled = NoiseEnabledCheckBox.IsChecked != false,
            DmcEnabled = DmcEnabledCheckBox.IsChecked != false,
            Pulse1Program = (Pulse1ProgramComboBox.SelectedItem as MidiProgramOption)?.ProgramNumber ?? 80,
            Pulse2Program = (Pulse2ProgramComboBox.SelectedItem as MidiProgramOption)?.ProgramNumber ?? 81,
            TriangleProgram = (TriangleProgramComboBox.SelectedItem as MidiProgramOption)?.ProgramNumber ?? 33,
            Pulse1VolumePercent = ReadPercent(Pulse1VolumeSlider),
            Pulse2VolumePercent = ReadPercent(Pulse2VolumeSlider),
            TriangleVolumePercent = ReadPercent(TriangleVolumeSlider),
            NoiseVolumePercent = ReadPercent(NoiseVolumeSlider),
            DmcVolumePercent = ReadPercent(DmcVolumeSlider),
            NoiseDrumNote = (NoiseDrumComboBox.SelectedItem as MidiPercussionOption)?.NoteNumber ?? -1,
            DmcDrumNote = (DmcDrumComboBox.SelectedItem as MidiPercussionOption)?.NoteNumber ?? -1,
            MidiSyncOffsetMilliseconds = ReadOffset(MidiSyncOffsetSlider)
        };

        if (_midiOutput.TryApplySettings(settings, out var error))
        {
            _statusSink?.Invoke(_midiOutput.GetStatusText());
            return;
        }

        _statusSink?.Invoke(error ?? "Could not apply MIDI settings.");

        _suppressEvents = true;
        try
        {
            LoadMidiSettings(_midiOutput.GetSettingsSnapshot(), _midiOutput.GetDevices());
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private string GetSelectedControllerId()
    {
        return (ControllerDeviceComboBox.SelectedItem as ControllerDeviceInfo)?.Id ?? ControllerDeviceInfo.AutoId;
    }

    private static int ReadAudioLatency(Slider slider)
    {
        return Math.Clamp((int)Math.Round(slider.Value), 40, 240);
    }

    private static int ReadPercent(Slider slider)
    {
        return Math.Clamp((int)Math.Round(slider.Value), 0, 200);
    }

    private static int ReadOffset(Slider slider)
    {
        return Math.Clamp((int)Math.Round(slider.Value), -40, 40);
    }

    private static string FormatPercent(double value)
    {
        return $"{Math.Clamp((int)Math.Round(value), 0, 200)}%";
    }

    private static string FormatOffset(double value)
    {
        var rounded = Math.Clamp((int)Math.Round(value), -40, 40);
        return $"{rounded:+#;-#;0} ms";
    }

    private ScrollViewer? GetActiveScrollViewer()
    {
        return _activeSection switch
        {
            SettingsSection.Video => VideoScrollViewer,
            SettingsSection.Audio => AudioScrollViewer,
            SettingsSection.Controls => ControlsScrollViewer,
            SettingsSection.Midi => MidiScrollViewer,
            _ => null
        };
    }

    private void SelectSection(SettingsSection section)
    {
        _activeSection = section;

        if (VideoScrollViewer is not null)
        {
            VideoScrollViewer.IsVisible = section == SettingsSection.Video;
        }

        if (AudioScrollViewer is not null)
        {
            AudioScrollViewer.IsVisible = section == SettingsSection.Audio;
        }

        if (ControlsScrollViewer is not null)
        {
            ControlsScrollViewer.IsVisible = section == SettingsSection.Controls;
        }

        if (MidiScrollViewer is not null)
        {
            MidiScrollViewer.IsVisible = section == SettingsSection.Midi;
        }
    }

    private Button? GetKeyboardBindingButton(string action)
    {
        return action switch
        {
            "A" => KeyboardAButton,
            "B" => KeyboardBButton,
            "Select" => KeyboardSelectButton,
            "Start" => KeyboardStartButton,
            "Up" => KeyboardUpButton,
            "Down" => KeyboardDownButton,
            "Left" => KeyboardLeftButton,
            "Right" => KeyboardRightButton,
            _ => null
        };
    }

    private Button? GetControllerBindingButton(string action)
    {
        return action switch
        {
            "A" => ControllerAButton,
            "B" => ControllerBButton,
            "Select" => ControllerSelectButton,
            "Start" => ControllerStartButton,
            "Up" => ControllerUpButton,
            "Down" => ControllerDownButton,
            "Left" => ControllerLeftButton,
            "Right" => ControllerRightButton,
            _ => null
        };
    }

    private static Key ReadButtonKey(Button button, Key fallback = Key.None)
    {
        return button.CommandParameter is Key key ? key : fallback;
    }

    private static ControllerBinding ReadControllerBinding(Button button, ControllerBinding? fallback = null)
    {
        return button.CommandParameter is ControllerBinding binding
            ? binding
            : fallback ?? ControllerBinding.None();
    }

    private static string FormatKey(Key key)
    {
        return key switch
        {
            Key.None => "None",
            Key.Up => "Up Arrow",
            Key.Down => "Down Arrow",
            Key.Left => "Left Arrow",
            Key.Right => "Right Arrow",
            Key.LeftShift => "Left Shift",
            Key.RightShift => "Right Shift",
            Key.LeftCtrl => "Left Ctrl",
            Key.RightCtrl => "Right Ctrl",
            Key.LeftAlt => "Left Alt",
            Key.RightAlt => "Right Alt",
            Key.Space => "Space",
            _ => key.ToString()
        };
    }

    private static string FormatControllerBinding(ControllerBinding binding)
    {
        return ControllerBindingCatalog.GetDisplayName(binding);
    }

    private DispatcherTimer CreateControllerBindingCaptureTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };

        timer.Tick += ControllerBindingCaptureTimer_OnTick;
        timer.Start();
        return timer;
    }

    private void ControllerBindingCaptureTimer_OnTick(object? sender, EventArgs e)
    {
        if (_pendingControllerBindingAction is null || _inputState is null)
        {
            return;
        }

        if (!_inputState.TryCaptureControllerBinding(GetSelectedControllerId(), out var capture))
        {
            return;
        }

        AssignPendingControllerBinding(capture.Binding);
    }

    private void CancelPendingBindingCapture()
    {
        _pendingKeyboardBindingAction = null;
        UpdateKeyboardBindingButtonStates();
        UpdateKeyboardCaptureText();
        CancelControllerBindingCapture();
    }

    private void CancelControllerBindingCapture()
    {
        _pendingControllerBindingAction = null;
        _inputState?.CancelControllerBindingCapture();
        UpdateControllerBindingButtonStates();
        UpdateControllerCaptureText();
    }
}
