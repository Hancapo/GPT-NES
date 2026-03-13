using System.Runtime.InteropServices;
using Avalonia.Input;
using NesEmu.Core;

namespace NesEmu.App;

public sealed class InputStateSource
{
    private const double DefaultAxisThreshold = 0.35;
    private const uint ErrorSuccess = 0;
    private static readonly bool IsGamepadRuntimeAvailable = OperatingSystem.IsWindows();
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

        if (!IsGamepadRuntimeAvailable)
        {
            return devices;
        }

        for (var i = 0; i < 4; i++)
        {
            if (TryGetXInputState((uint)i, out _))
            {
                devices.Add(new ControllerDeviceInfo(
                    $"gamepad:{i}",
                    $"Gamepad {i + 1}",
                    ControllerDeviceSource.Gamepad,
                    i));
            }
        }

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
        if (!IsGamepadRuntimeAvailable)
        {
            return default;
        }

        for (var i = 0; i < 4; i++)
        {
            if (TryGetXInputState((uint)i, out var state))
            {
                return ReadGamepad(state, settings);
            }
        }

        return default;
    }

    private ControllerState ReadSelectedGamepad(InputSettings settings)
    {
        if (!IsGamepadRuntimeAvailable)
        {
            return default;
        }

        var index = ParseDeviceIndex(settings.SelectedControllerId, "gamepad:");
        return index >= 0 && TryGetXInputState((uint)index, out var state)
            ? ReadGamepad(state, settings)
            : default;
    }

    private static ControllerState ReadGamepad(XInputState state, InputSettings settings)
    {
        var gamepad = CreateGamepadState(state.Gamepad);
        return new ControllerState(
            A: EvaluateGamepadBinding(gamepad, settings.ControllerA),
            B: EvaluateGamepadBinding(gamepad, settings.ControllerB),
            Select: EvaluateGamepadBinding(gamepad, settings.ControllerSelect),
            Start: EvaluateGamepadBinding(gamepad, settings.ControllerStart),
            Up: EvaluateGamepadBinding(gamepad, settings.ControllerUp),
            Down: EvaluateGamepadBinding(gamepad, settings.ControllerDown),
            Left: EvaluateGamepadBinding(gamepad, settings.ControllerLeft),
            Right: EvaluateGamepadBinding(gamepad, settings.ControllerRight));
    }

    private static bool EvaluateGamepadBinding(GamepadState state, ControllerBinding binding)
    {
        return binding.Kind switch
        {
            ControllerBindingKind.None => false,
            ControllerBindingKind.GamepadButton => (state.Buttons & (GamepadButtons)binding.Index) != 0,
            ControllerBindingKind.GamepadAxis => EvaluateAxis(ReadGamepadAxis(state, binding.Index), binding),
            _ => false
        };
    }

    private static double ReadGamepadAxis(GamepadState state, int axisIndex)
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

        if (controllerId.StartsWith("gamepad:", StringComparison.OrdinalIgnoreCase))
        {
            return ControllerDeviceSource.Gamepad;
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

    private static bool TryGetXInputState(uint userIndex, out XInputState state)
    {
        try
        {
            if (XInputGetState(userIndex, out state) == ErrorSuccess)
            {
                return true;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        state = default;
        return false;
    }

    private static GamepadState CreateGamepadState(XInputGamepad gamepad)
    {
        var buttons = GamepadButtons.None;
        if ((gamepad.Buttons & 0x1000) != 0) buttons |= GamepadButtons.A;
        if ((gamepad.Buttons & 0x2000) != 0) buttons |= GamepadButtons.B;
        if ((gamepad.Buttons & 0x4000) != 0) buttons |= GamepadButtons.X;
        if ((gamepad.Buttons & 0x8000) != 0) buttons |= GamepadButtons.Y;
        if ((gamepad.Buttons & 0x0020) != 0) buttons |= GamepadButtons.View;
        if ((gamepad.Buttons & 0x0010) != 0) buttons |= GamepadButtons.Menu;
        if ((gamepad.Buttons & 0x0100) != 0) buttons |= GamepadButtons.LeftShoulder;
        if ((gamepad.Buttons & 0x0200) != 0) buttons |= GamepadButtons.RightShoulder;
        if ((gamepad.Buttons & 0x0001) != 0) buttons |= GamepadButtons.DPadUp;
        if ((gamepad.Buttons & 0x0002) != 0) buttons |= GamepadButtons.DPadDown;
        if ((gamepad.Buttons & 0x0004) != 0) buttons |= GamepadButtons.DPadLeft;
        if ((gamepad.Buttons & 0x0008) != 0) buttons |= GamepadButtons.DPadRight;

        return new GamepadState(
            buttons,
            NormalizeStick(gamepad.LeftThumbX),
            NormalizeStick(gamepad.LeftThumbY),
            NormalizeStick(gamepad.RightThumbX),
            NormalizeStick(gamepad.RightThumbY),
            NormalizeTrigger(gamepad.LeftTrigger),
            NormalizeTrigger(gamepad.RightTrigger));
    }

    private static double NormalizeStick(short value)
    {
        return value < 0
            ? Math.Max(value / 32768.0, -1.0)
            : Math.Min(value / 32767.0, 1.0);
    }

    private static double NormalizeTrigger(byte value)
    {
        return value / 255.0;
    }

    private static bool KeyPressed(IReadOnlySet<Key> pressedKeys, Key key) => key != Key.None && pressedKeys.Contains(key);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.Winapi)]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }

    private readonly record struct GamepadState(
        GamepadButtons Buttons,
        double LeftThumbstickX,
        double LeftThumbstickY,
        double RightThumbstickX,
        double RightThumbstickY,
        double LeftTrigger,
        double RightTrigger);
}
