using Avalonia.Input;
using NesEmu.Core;

namespace NesEmu.App;

public sealed class InputStateSource : IDisposable
{
    private const double DefaultAxisThreshold = 0.35;
    private readonly HashSet<Key> _pressedKeys = [];
    private readonly object _sync = new();
    private readonly SdlGamepadBackend _gamepadBackend = new();
    private InputSettings _settings = InputSettings.CreateDefault();
    private ControllerCaptureSession? _controllerCaptureSession;
    private bool _disposed;

    public void SetKey(Key key, bool pressed)
    {
        lock (_sync)
        {
            if (pressed)
            {
                _pressedKeys.Add(key);
            }
            else
            {
                _pressedKeys.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _pressedKeys.Clear();
        }
    }

    public InputSettings GetSettingsSnapshot()
    {
        lock (_sync)
        {
            return _settings.Clone();
        }
    }

    public void ApplySettings(InputSettings settings)
    {
        lock (_sync)
        {
            _settings = settings.Clone();
        }
    }

    public IReadOnlyList<ControllerDeviceInfo> GetAvailableControllers()
    {
        var devices = new List<ControllerDeviceInfo>
        {
            new(ControllerDeviceInfo.NoneId, "No controller", ControllerDeviceSource.None, -1),
            new(ControllerDeviceInfo.AutoId, "Auto detect", ControllerDeviceSource.Auto, -1)
        };

        devices.AddRange(_gamepadBackend.GetAvailableControllers());
        return devices;
    }

    public IReadOnlyList<ControllerBindingOption> GetControllerBindingOptions(string? controllerId = null)
    {
        return ControllerBindingCatalog.Options;
    }

    public void BeginControllerBindingCapture(string? controllerId)
    {
        var normalizedControllerId = NormalizeControllerId(controllerId);
        var activeBindings = ReadActiveControllerBindings(normalizedControllerId);

        lock (_sync)
        {
            _controllerCaptureSession = new ControllerCaptureSession(normalizedControllerId, activeBindings);
        }
    }

    public void CancelControllerBindingCapture()
    {
        lock (_sync)
        {
            _controllerCaptureSession = null;
        }
    }

    public bool TryCaptureControllerBinding(string? controllerId, out CapturedControllerBinding capture)
    {
        var normalizedControllerId = NormalizeControllerId(controllerId);
        var activeBindings = ReadActiveControllerBindings(normalizedControllerId);

        lock (_sync)
        {
            if (_controllerCaptureSession is null || _controllerCaptureSession.ControllerId != normalizedControllerId)
            {
                _controllerCaptureSession = new ControllerCaptureSession(normalizedControllerId, activeBindings);
                capture = default;
                return false;
            }

            foreach (var option in activeBindings)
            {
                if (_controllerCaptureSession.ActiveBindings.Any(active => active.Binding == option.Binding))
                {
                    continue;
                }

                _controllerCaptureSession = new ControllerCaptureSession(normalizedControllerId, activeBindings);
                capture = new CapturedControllerBinding(option.Binding, option.DisplayName);
                return true;
            }

            _controllerCaptureSession = new ControllerCaptureSession(normalizedControllerId, activeBindings);
        }

        capture = default;
        return false;
    }

    public bool IsControlKey(Key key)
    {
        lock (_sync)
        {
            return key == _settings.KeyboardAKey
                || key == _settings.KeyboardBKey
                || key == _settings.KeyboardSelectKey
                || key == _settings.KeyboardStartKey
                || key == _settings.KeyboardUpKey
                || key == _settings.KeyboardDownKey
                || key == _settings.KeyboardLeftKey
                || key == _settings.KeyboardRightKey;
        }
    }

    public ControllerState GetCombinedState()
    {
        HashSet<Key> pressedKeys;
        InputSettings settings;

        lock (_sync)
        {
            pressedKeys = [.. _pressedKeys];
            settings = _settings.Clone();
        }

        var keyboard = GetKeyboardState(pressedKeys, settings);
        var controller = GetControllerState(settings);

        return new ControllerState(
            keyboard.A || controller.A,
            keyboard.B || controller.B,
            keyboard.Select || controller.Select,
            keyboard.Start || controller.Start,
            keyboard.Up || controller.Up,
            keyboard.Down || controller.Down,
            keyboard.Left || controller.Left,
            keyboard.Right || controller.Right);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gamepadBackend.Dispose();
        _disposed = true;
    }

    private ControllerState GetKeyboardState(HashSet<Key> pressedKeys, InputSettings settings)
    {
        return new ControllerState(
            A: KeyPressed(pressedKeys, settings.KeyboardAKey),
            B: KeyPressed(pressedKeys, settings.KeyboardBKey),
            Select: KeyPressed(pressedKeys, settings.KeyboardSelectKey),
            Start: KeyPressed(pressedKeys, settings.KeyboardStartKey),
            Up: KeyPressed(pressedKeys, settings.KeyboardUpKey),
            Down: KeyPressed(pressedKeys, settings.KeyboardDownKey),
            Left: KeyPressed(pressedKeys, settings.KeyboardLeftKey),
            Right: KeyPressed(pressedKeys, settings.KeyboardRightKey));
    }

    private ControllerState GetControllerState(InputSettings settings)
    {
        return GetControllerSource(settings.SelectedControllerId) switch
        {
            ControllerDeviceSource.None => default,
            ControllerDeviceSource.Gamepad => ReadSelectedGamepad(settings),
            _ => ReadAutoController(settings)
        };
    }

    private ControllerState ReadAutoController(InputSettings settings)
    {
        return _gamepadBackend.TryReadAutoGamepad(out var state)
            ? ReadGamepad(state, settings)
            : default;
    }

    private ControllerState ReadSelectedGamepad(InputSettings settings)
    {
        return _gamepadBackend.TryReadSelectedGamepad(settings.SelectedControllerId, out var state)
            ? ReadGamepad(state, settings)
            : default;
    }

    private static ControllerState ReadGamepad(SdlGamepadState state, InputSettings settings)
    {
        var analogDPad = ReadAnalogDPadState(state, settings.AnalogDPadMode);

        return new ControllerState(
            A: EvaluateGamepadBinding(state, settings.ControllerA),
            B: EvaluateGamepadBinding(state, settings.ControllerB),
            Select: EvaluateGamepadBinding(state, settings.ControllerSelect),
            Start: EvaluateGamepadBinding(state, settings.ControllerStart),
            Up: EvaluateGamepadBinding(state, settings.ControllerUp) || analogDPad.Up,
            Down: EvaluateGamepadBinding(state, settings.ControllerDown) || analogDPad.Down,
            Left: EvaluateGamepadBinding(state, settings.ControllerLeft) || analogDPad.Left,
            Right: EvaluateGamepadBinding(state, settings.ControllerRight) || analogDPad.Right);
    }

    private static bool EvaluateGamepadBinding(SdlGamepadState state, ControllerBinding binding)
    {
        return binding.Kind switch
        {
            ControllerBindingKind.None => false,
            ControllerBindingKind.GamepadButton => (state.Buttons & (GamepadButtons)binding.Index) != 0,
            ControllerBindingKind.GamepadAxis => EvaluateAxis(ReadGamepadAxis(state, binding.Index), binding),
            _ => false
        };
    }

    private static double ReadGamepadAxis(SdlGamepadState state, int axisIndex)
    {
        return axisIndex switch
        {
            0 => state.LeftThumbstickX,
            1 => -state.LeftThumbstickY,
            2 => state.RightThumbstickX,
            3 => -state.RightThumbstickY,
            4 => state.LeftTrigger,
            5 => state.RightTrigger,
            _ => 0.0
        };
    }

    private static bool EvaluateAxis(double value, ControllerBinding binding)
    {
        var threshold = binding.Threshold <= 0 ? DefaultAxisThreshold : binding.Threshold;
        return binding.Value >= 0
            ? value >= threshold
            : value <= -threshold;
    }

    private IReadOnlyList<ControllerBindingOption> ReadActiveControllerBindings(string? controllerId)
    {
        if (!TryReadGamepadState(controllerId, out var state))
        {
            return [];
        }

        return ControllerBindingCatalog.Options
            .Where(option => option.Binding.Kind != ControllerBindingKind.None && EvaluateGamepadBinding(state, option.Binding))
            .ToArray();
    }

    private bool TryReadGamepadState(string? controllerId, out SdlGamepadState state)
    {
        return GetControllerSource(controllerId) switch
        {
            ControllerDeviceSource.Gamepad => _gamepadBackend.TryReadSelectedGamepad(controllerId, out state),
            ControllerDeviceSource.None => ReturnNoGamepad(out state),
            _ => _gamepadBackend.TryReadAutoGamepad(out state)
        };
    }

    private static bool ReturnNoGamepad(out SdlGamepadState state)
    {
        state = default;
        return false;
    }

    private static string NormalizeControllerId(string? controllerId)
    {
        return string.IsNullOrWhiteSpace(controllerId)
            ? ControllerDeviceInfo.AutoId
            : controllerId;
    }

    private static ControllerState ReadAnalogDPadState(SdlGamepadState state, ControllerAnalogDPadMode mode)
    {
        return mode switch
        {
            ControllerAnalogDPadMode.LeftStick => new ControllerState(
                A: false,
                B: false,
                Select: false,
                Start: false,
                Up: state.LeftThumbstickY <= -DefaultAxisThreshold,
                Down: state.LeftThumbstickY >= DefaultAxisThreshold,
                Left: state.LeftThumbstickX <= -DefaultAxisThreshold,
                Right: state.LeftThumbstickX >= DefaultAxisThreshold),
            ControllerAnalogDPadMode.RightStick => new ControllerState(
                A: false,
                B: false,
                Select: false,
                Start: false,
                Up: state.RightThumbstickY <= -DefaultAxisThreshold,
                Down: state.RightThumbstickY >= DefaultAxisThreshold,
                Left: state.RightThumbstickX <= -DefaultAxisThreshold,
                Right: state.RightThumbstickX >= DefaultAxisThreshold),
            _ => default
        };
    }

    private static ControllerDeviceSource GetControllerSource(string? controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId) || controllerId == ControllerDeviceInfo.AutoId)
        {
            return ControllerDeviceSource.Auto;
        }

        if (controllerId == ControllerDeviceInfo.NoneId)
        {
            return ControllerDeviceSource.None;
        }

        return controllerId.StartsWith("gamepad:", StringComparison.OrdinalIgnoreCase)
            ? ControllerDeviceSource.Gamepad
            : ControllerDeviceSource.Auto;
    }

    private static bool KeyPressed(IReadOnlySet<Key> pressedKeys, Key key) => key != Key.None && pressedKeys.Contains(key);

    private sealed record ControllerCaptureSession(string ControllerId, IReadOnlyList<ControllerBindingOption> ActiveBindings);
}

public readonly record struct CapturedControllerBinding(ControllerBinding Binding, string DisplayName);
