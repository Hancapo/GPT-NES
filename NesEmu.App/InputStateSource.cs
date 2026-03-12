using Avalonia.Input;
using NesEmu.Core;
using Windows.Gaming.Input;

namespace NesEmu.App;

public sealed class InputStateSource
{
    private const double DefaultAxisThreshold = 0.35;
    private readonly HashSet<Key> _pressedKeys = [];
    private readonly object _sync = new();
    private InputSettings _settings = InputSettings.CreateDefault();

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

        try
        {
            for (var i = 0; i < Gamepad.Gamepads.Count; i++)
            {
                devices.Add(new ControllerDeviceInfo(
                    $"gamepad:{i}",
                    $"Gamepad {i + 1}",
                    ControllerDeviceSource.Gamepad,
                    i));
            }

            for (var i = 0; i < RawGameController.RawGameControllers.Count; i++)
            {
                var controller = RawGameController.RawGameControllers[i];
                var displayName = $"Raw Controller {i + 1} (VID {controller.HardwareVendorId:X4} PID {controller.HardwareProductId:X4})";
                devices.Add(new ControllerDeviceInfo(
                    $"raw:{i}",
                    displayName,
                    ControllerDeviceSource.Raw,
                    i));
            }
        }
        catch
        {
            return devices;
        }

        return devices;
    }

    public IReadOnlyList<ControllerBindingOption> GetControllerBindingOptions(string? controllerId = null)
    {
        var id = string.IsNullOrWhiteSpace(controllerId)
            ? GetSettingsSnapshot().SelectedControllerId
            : controllerId;
        var source = GetControllerSource(id);

        var options = new List<ControllerBindingOption>
        {
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
        };

        if (source == ControllerDeviceSource.Raw)
        {
            AppendRawOptions(options, id);
        }

        return options;
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
        try
        {
            return ResolveControllerState(settings);
        }
        catch
        {
            return default;
        }
    }

    private ControllerState ResolveControllerState(InputSettings settings)
    {
        return GetControllerSource(settings.SelectedControllerId) switch
        {
            ControllerDeviceSource.None => default,
            ControllerDeviceSource.Gamepad => ReadSelectedGamepad(settings),
            ControllerDeviceSource.Raw => ReadSelectedRawController(settings),
            _ => ReadAutoController(settings)
        };
    }

    private ControllerState ReadAutoController(InputSettings settings)
    {
        if (Gamepad.Gamepads.Count > 0)
        {
            return ReadGamepad(Gamepad.Gamepads[0], settings);
        }

        if (RawGameController.RawGameControllers.Count > 0)
        {
            return ReadRawController(RawGameController.RawGameControllers[0], settings);
        }

        return default;
    }

    private ControllerState ReadSelectedGamepad(InputSettings settings)
    {
        var index = ParseDeviceIndex(settings.SelectedControllerId, "gamepad:");
        return index >= 0 && index < Gamepad.Gamepads.Count
            ? ReadGamepad(Gamepad.Gamepads[index], settings)
            : default;
    }

    private ControllerState ReadSelectedRawController(InputSettings settings)
    {
        var index = ParseDeviceIndex(settings.SelectedControllerId, "raw:");
        return index >= 0 && index < RawGameController.RawGameControllers.Count
            ? ReadRawController(RawGameController.RawGameControllers[index], settings)
            : default;
    }

    private ControllerState ReadGamepad(Gamepad gamepad, InputSettings settings)
    {
        var reading = gamepad.GetCurrentReading();
        return new ControllerState(
            A: EvaluateGamepadBinding(reading, settings.ControllerA),
            B: EvaluateGamepadBinding(reading, settings.ControllerB),
            Select: EvaluateGamepadBinding(reading, settings.ControllerSelect),
            Start: EvaluateGamepadBinding(reading, settings.ControllerStart),
            Up: EvaluateGamepadBinding(reading, settings.ControllerUp),
            Down: EvaluateGamepadBinding(reading, settings.ControllerDown),
            Left: EvaluateGamepadBinding(reading, settings.ControllerLeft),
            Right: EvaluateGamepadBinding(reading, settings.ControllerRight));
    }

    private ControllerState ReadRawController(RawGameController controller, InputSettings settings)
    {
        var buttons = new bool[controller.ButtonCount];
        var switches = new GameControllerSwitchPosition[controller.SwitchCount];
        var axes = new double[controller.AxisCount];
        controller.GetCurrentReading(buttons, switches, axes);

        return new ControllerState(
            A: EvaluateRawBinding(controller, buttons, switches, axes, settings.ControllerA),
            B: EvaluateRawBinding(controller, buttons, switches, axes, settings.ControllerB),
            Select: EvaluateRawBinding(controller, buttons, switches, axes, settings.ControllerSelect),
            Start: EvaluateRawBinding(controller, buttons, switches, axes, settings.ControllerStart),
            Up: EvaluateRawBinding(controller, buttons, switches, axes, settings.ControllerUp),
            Down: EvaluateRawBinding(controller, buttons, switches, axes, settings.ControllerDown),
            Left: EvaluateRawBinding(controller, buttons, switches, axes, settings.ControllerLeft),
            Right: EvaluateRawBinding(controller, buttons, switches, axes, settings.ControllerRight));
    }

    private static bool EvaluateGamepadBinding(GamepadReading reading, ControllerBinding binding)
    {
        return binding.Kind switch
        {
            ControllerBindingKind.None => false,
            ControllerBindingKind.GamepadButton => reading.Buttons.HasFlag((GamepadButtons)binding.Index),
            ControllerBindingKind.GamepadAxis => EvaluateAxis(ReadGamepadAxis(reading, binding.Index), binding),
            _ => false
        };
    }

    private static bool EvaluateRawBinding(
        RawGameController controller,
        IReadOnlyList<bool> buttons,
        IReadOnlyList<GameControllerSwitchPosition> switches,
        IReadOnlyList<double> axes,
        ControllerBinding binding)
    {
        binding = TranslateBindingForRawController(controller, binding);

        return binding.Kind switch
        {
            ControllerBindingKind.None => false,
            ControllerBindingKind.RawButton => ButtonPressed(buttons, binding.Index),
            ControllerBindingKind.RawSwitch => EvaluateSwitch(switches, binding.Index, (ControllerSwitchDirection)binding.Value),
            ControllerBindingKind.RawAxis => EvaluateAxis(ReadAxis(axes, binding.Index), binding),
            _ => false
        };
    }

    private static ControllerBinding TranslateBindingForRawController(RawGameController controller, ControllerBinding binding)
    {
        if (binding.Kind is not (ControllerBindingKind.GamepadButton or ControllerBindingKind.GamepadAxis))
        {
            return binding;
        }

        var isSony = controller.HardwareVendorId == 0x054C;

        return binding.Kind switch
        {
            ControllerBindingKind.GamepadButton => (GamepadButtons)binding.Index switch
            {
                GamepadButtons.A => ControllerBinding.RawButton(isSony ? 1 : 0),
                GamepadButtons.B => ControllerBinding.RawButton(isSony ? 2 : 1),
                GamepadButtons.X => ControllerBinding.RawButton(isSony ? 0 : 2),
                GamepadButtons.Y => ControllerBinding.RawButton(isSony ? 3 : 3),
                GamepadButtons.View => ControllerBinding.RawButton(8),
                GamepadButtons.Menu => ControllerBinding.RawButton(9),
                GamepadButtons.LeftShoulder => ControllerBinding.RawButton(4),
                GamepadButtons.RightShoulder => ControllerBinding.RawButton(5),
                GamepadButtons.DPadUp => ControllerBinding.RawSwitch(0, ControllerSwitchDirection.Up),
                GamepadButtons.DPadDown => ControllerBinding.RawSwitch(0, ControllerSwitchDirection.Down),
                GamepadButtons.DPadLeft => ControllerBinding.RawSwitch(0, ControllerSwitchDirection.Left),
                GamepadButtons.DPadRight => ControllerBinding.RawSwitch(0, ControllerSwitchDirection.Right),
                _ => ControllerBinding.None()
            },
            ControllerBindingKind.GamepadAxis => binding.Index switch
            {
                0 => ControllerBinding.RawAxis(0, positive: binding.Value > 0, binding.Threshold),
                1 => ControllerBinding.RawAxis(1, positive: binding.Value > 0, binding.Threshold),
                _ => ControllerBinding.None()
            },
            _ => binding
        };
    }

    private static double ReadGamepadAxis(GamepadReading reading, int axisIndex)
    {
        return axisIndex switch
        {
            0 => reading.LeftThumbstickX,
            1 => -reading.LeftThumbstickY,
            2 => reading.RightThumbstickX,
            3 => -reading.RightThumbstickY,
            4 => reading.LeftTrigger,
            5 => reading.RightTrigger,
            _ => 0.0
        };
    }

    private static double ReadAxis(IReadOnlyList<double> axes, int index) => index >= 0 && index < axes.Count ? axes[index] : 0.0;

    private static bool EvaluateAxis(double value, ControllerBinding binding)
    {
        var threshold = binding.Threshold <= 0 ? DefaultAxisThreshold : binding.Threshold;
        return binding.Value >= 0
            ? value >= threshold
            : value <= -threshold;
    }

    private static bool EvaluateSwitch(IReadOnlyList<GameControllerSwitchPosition> switches, int switchIndex, ControllerSwitchDirection direction)
    {
        if (switchIndex < 0 || switchIndex >= switches.Count)
        {
            return false;
        }

        return direction switch
        {
            ControllerSwitchDirection.Up => IsUp(switches[switchIndex]),
            ControllerSwitchDirection.Down => IsDown(switches[switchIndex]),
            ControllerSwitchDirection.Left => IsLeft(switches[switchIndex]),
            ControllerSwitchDirection.Right => IsRight(switches[switchIndex]),
            _ => false
        };
    }

    private void AppendRawOptions(List<ControllerBindingOption> options, string controllerId)
    {
        var index = ParseDeviceIndex(controllerId, "raw:");
        if (index < 0 || index >= RawGameController.RawGameControllers.Count)
        {
            return;
        }

        var controller = RawGameController.RawGameControllers[index];
        for (var i = 0; i < controller.ButtonCount; i++)
        {
            options.Add(new ControllerBindingOption($"rb:{i}", $"Button {i + 1}", ControllerBinding.RawButton(i)));
        }

        for (var i = 0; i < controller.SwitchCount; i++)
        {
            var prefix = controller.SwitchCount == 1 ? "D-Pad" : $"Switch {i + 1}";
            options.Add(new ControllerBindingOption($"rs:{i}:up", $"{prefix} Up", ControllerBinding.RawSwitch(i, ControllerSwitchDirection.Up)));
            options.Add(new ControllerBindingOption($"rs:{i}:down", $"{prefix} Down", ControllerBinding.RawSwitch(i, ControllerSwitchDirection.Down)));
            options.Add(new ControllerBindingOption($"rs:{i}:left", $"{prefix} Left", ControllerBinding.RawSwitch(i, ControllerSwitchDirection.Left)));
            options.Add(new ControllerBindingOption($"rs:{i}:right", $"{prefix} Right", ControllerBinding.RawSwitch(i, ControllerSwitchDirection.Right)));
        }

        for (var i = 0; i < controller.AxisCount; i++)
        {
            options.Add(new ControllerBindingOption($"ra:{i}:-", $"Axis {i + 1} -", ControllerBinding.RawAxis(i, positive: false)));
            options.Add(new ControllerBindingOption($"ra:{i}:+", $"Axis {i + 1} +", ControllerBinding.RawAxis(i, positive: true)));
        }
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

        if (controllerId.StartsWith("gamepad:", StringComparison.OrdinalIgnoreCase))
        {
            return ControllerDeviceSource.Gamepad;
        }

        if (controllerId.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
        {
            return ControllerDeviceSource.Raw;
        }

        return ControllerDeviceSource.Auto;
    }

    private static int ParseDeviceIndex(string? controllerId, string prefix)
    {
        return controllerId is not null
               && controllerId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(controllerId[prefix.Length..], out var index)
            ? index
            : -1;
    }

    private static bool KeyPressed(IReadOnlySet<Key> pressedKeys, Key key) => key != Key.None && pressedKeys.Contains(key);

    private static bool ButtonPressed(IReadOnlyList<bool> buttons, int index) => index >= 0 && index < buttons.Count && buttons[index];

    private static bool IsUp(GameControllerSwitchPosition value) =>
        value is GameControllerSwitchPosition.Up or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.UpRight;

    private static bool IsDown(GameControllerSwitchPosition value) =>
        value is GameControllerSwitchPosition.Down or GameControllerSwitchPosition.DownLeft or GameControllerSwitchPosition.DownRight;

    private static bool IsLeft(GameControllerSwitchPosition value) =>
        value is GameControllerSwitchPosition.Left or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.DownLeft;

    private static bool IsRight(GameControllerSwitchPosition value) =>
        value is GameControllerSwitchPosition.Right or GameControllerSwitchPosition.UpRight or GameControllerSwitchPosition.DownRight;
}
