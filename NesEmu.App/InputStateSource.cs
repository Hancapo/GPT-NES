using Avalonia.Input;
using NesEmu.Core;
using Windows.Gaming.Input;

namespace NesEmu.App;

public sealed class InputStateSource
{
    private readonly HashSet<Key> _pressedKeys = [];
    private readonly object _sync = new();

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

    public ControllerState GetCombinedState()
    {
        var keyboard = GetKeyboardState();
        var gamepad = GetGamepadState();

        return new ControllerState(
            keyboard.A || gamepad.A,
            keyboard.B || gamepad.B,
            keyboard.Select || gamepad.Select,
            keyboard.Start || gamepad.Start,
            keyboard.Up || gamepad.Up,
            keyboard.Down || gamepad.Down,
            keyboard.Left || gamepad.Left,
            keyboard.Right || gamepad.Right);
    }

    private ControllerState GetKeyboardState()
    {
        lock (_sync)
        {
            return new ControllerState(
                A: IsPressed(Key.X) || IsPressed(Key.K),
                B: IsPressed(Key.Z) || IsPressed(Key.J),
                Select: IsPressed(Key.RightShift) || IsPressed(Key.LeftShift),
                Start: IsPressed(Key.Enter),
                Up: IsPressed(Key.Up) || IsPressed(Key.W),
                Down: IsPressed(Key.Down) || IsPressed(Key.S),
                Left: IsPressed(Key.Left) || IsPressed(Key.A),
                Right: IsPressed(Key.Right) || IsPressed(Key.D));
        }
    }

    private ControllerState GetGamepadState()
    {
        try
        {
            if (Gamepad.Gamepads.Count > 0)
            {
                var reading = Gamepad.Gamepads[0].GetCurrentReading();
                return new ControllerState(
                    A: reading.Buttons.HasFlag(GamepadButtons.A),
                    B: reading.Buttons.HasFlag(GamepadButtons.X),
                    Select: reading.Buttons.HasFlag(GamepadButtons.View),
                    Start: reading.Buttons.HasFlag(GamepadButtons.Menu),
                    Up: reading.Buttons.HasFlag(GamepadButtons.DPadUp) || reading.LeftThumbstickY > 0.35,
                    Down: reading.Buttons.HasFlag(GamepadButtons.DPadDown) || reading.LeftThumbstickY < -0.35,
                    Left: reading.Buttons.HasFlag(GamepadButtons.DPadLeft) || reading.LeftThumbstickX < -0.35,
                    Right: reading.Buttons.HasFlag(GamepadButtons.DPadRight) || reading.LeftThumbstickX > 0.35);
            }

            if (RawGameController.RawGameControllers.Count > 0)
            {
                var controller = RawGameController.RawGameControllers[0];
                var buttons = new bool[controller.ButtonCount];
                var switches = new GameControllerSwitchPosition[controller.SwitchCount];
                var axes = new double[controller.AxisCount];
                controller.GetCurrentReading(buttons, switches, axes);

                var dpad = switches.Length > 0 ? switches[0] : GameControllerSwitchPosition.Center;
                var leftX = axes.Length > 0 ? axes[0] : 0.0;
                var leftY = axes.Length > 1 ? axes[1] : 0.0;

                var crossIndex = controller.HardwareVendorId == 0x054C ? 1 : 0;
                var squareIndex = controller.HardwareVendorId == 0x054C ? 0 : 2;
                var selectIndex = controller.HardwareVendorId == 0x054C ? 8 : 8;
                var startIndex = controller.HardwareVendorId == 0x054C ? 9 : 9;

                return new ControllerState(
                    A: ButtonPressed(buttons, crossIndex),
                    B: ButtonPressed(buttons, squareIndex),
                    Select: ButtonPressed(buttons, selectIndex),
                    Start: ButtonPressed(buttons, startIndex),
                    Up: IsUp(dpad) || leftY < -0.35,
                    Down: IsDown(dpad) || leftY > 0.35,
                    Left: IsLeft(dpad) || leftX < -0.35,
                    Right: IsRight(dpad) || leftX > 0.35);
            }
        }
        catch
        {
            return default;
        }

        return default;
    }

    private bool IsPressed(Key key) => _pressedKeys.Contains(key);

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
