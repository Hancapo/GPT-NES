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
        return
        [
            new("none", "None", ControllerBinding.None()),
            new("south", "South / Cross / A", ControllerBinding.GamepadButton(GamepadButtons.A)),
            new("west", "West / Square / X", ControllerBinding.GamepadButton(GamepadButtons.X)),
            new("east", "East / Circle / B", ControllerBinding.GamepadButton(GamepadButtons.B)),
            new("north", "North / Triangle / Y", ControllerBinding.GamepadButton(GamepadButtons.Y)),
            new("view", "View / Share", ControllerBinding.GamepadButton(GamepadButtons.View)),
            new("menu", "Menu / Options", ControllerBinding.GamepadButton(GamepadButtons.Menu)),
            new("l1", "Left Shoulder", ControllerBinding.GamepadButton(GamepadButtons.LeftShoulder)),
            new("r1", "Right Shoulder", ControllerBinding.GamepadButton(GamepadButtons.RightShoulder)),
            new("dup", "D-Pad Up", ControllerBinding.GamepadButton(GamepadButtons.DPadUp)),
            new("ddown", "D-Pad Down", ControllerBinding.GamepadButton(GamepadButtons.DPadDown)),
            new("dleft", "D-Pad Left", ControllerBinding.GamepadButton(GamepadButtons.DPadLeft)),
            new("dright", "D-Pad Right", ControllerBinding.GamepadButton(GamepadButtons.DPadRight)),
            new("lsx+", "Left Stick Right", ControllerBinding.GamepadAxis(0, positive: true, DefaultAxisThreshold)),
            new("lsx-", "Left Stick Left", ControllerBinding.GamepadAxis(0, positive: false, DefaultAxisThreshold)),
            new("lsy+", "Left Stick Down", ControllerBinding.GamepadAxis(1, positive: true, DefaultAxisThreshold)),
            new("lsy-", "Left Stick Up", ControllerBinding.GamepadAxis(1, positive: false, DefaultAxisThreshold))
        ];
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
        return new ControllerState(
            A: EvaluateGamepadBinding(state, settings.ControllerA),
            B: EvaluateGamepadBinding(state, settings.ControllerB),
            Select: EvaluateGamepadBinding(state, settings.ControllerSelect),
            Start: EvaluateGamepadBinding(state, settings.ControllerStart),
            Up: EvaluateGamepadBinding(state, settings.ControllerUp),
            Down: EvaluateGamepadBinding(state, settings.ControllerDown),
            Left: EvaluateGamepadBinding(state, settings.ControllerLeft),
            Right: EvaluateGamepadBinding(state, settings.ControllerRight));
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
}
