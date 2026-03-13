using Avalonia.Input;

namespace NesEmu.App;

[Flags]
public enum GamepadButtons
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    View = 1 << 4,
    Menu = 1 << 5,
    LeftShoulder = 1 << 6,
    RightShoulder = 1 << 7,
    DPadUp = 1 << 8,
    DPadDown = 1 << 9,
    DPadLeft = 1 << 10,
    DPadRight = 1 << 11
}

public enum ControllerDeviceSource
{
    None,
    Auto,
    Gamepad,
    Raw
}

public enum ControllerAnalogDPadMode
{
    None,
    LeftStick,
    RightStick
}

public enum ControllerBindingKind
{
    None,
    GamepadButton,
    GamepadAxis,
    RawButton,
    RawSwitch,
    RawAxis
}

public enum ControllerSwitchDirection
{
    Up,
    Down,
    Left,
    Right
}

public sealed record ControllerDeviceInfo(string Id, string DisplayName, ControllerDeviceSource Source, int DeviceIndex)
{
    public const string NoneId = "none";
    public const string AutoId = "auto";

    public override string ToString() => DisplayName;
}

public sealed record ControllerBinding(
    ControllerBindingKind Kind,
    int Index = -1,
    int Value = 0,
    double Threshold = 0.35)
{
    public static ControllerBinding None() => new(ControllerBindingKind.None);

    public static ControllerBinding GamepadButton(GamepadButtons button) => new(ControllerBindingKind.GamepadButton, (int)button);

    public static ControllerBinding GamepadAxis(int axisIndex, bool positive, double threshold = 0.35) =>
        new(ControllerBindingKind.GamepadAxis, axisIndex, positive ? 1 : -1, threshold);

    public static ControllerBinding RawButton(int buttonIndex) => new(ControllerBindingKind.RawButton, buttonIndex);

    public static ControllerBinding RawSwitch(int switchIndex, ControllerSwitchDirection direction) =>
        new(ControllerBindingKind.RawSwitch, switchIndex, (int)direction);

    public static ControllerBinding RawAxis(int axisIndex, bool positive, double threshold = 0.35) =>
        new(ControllerBindingKind.RawAxis, axisIndex, positive ? 1 : -1, threshold);
}

public sealed record ControllerBindingOption(string Id, string DisplayName, ControllerBinding Binding)
{
    public override string ToString() => DisplayName;
}

public sealed record ControllerAnalogDPadOption(ControllerAnalogDPadMode Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record KeyBindingOption(Key Key, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class InputSettings
{
    public string SelectedControllerId { get; set; } = ControllerDeviceInfo.AutoId;

    public ControllerAnalogDPadMode AnalogDPadMode { get; set; } = ControllerAnalogDPadMode.None;

    public Key KeyboardAKey { get; set; } = Key.X;

    public Key KeyboardBKey { get; set; } = Key.Z;

    public Key KeyboardSelectKey { get; set; } = Key.RightShift;

    public Key KeyboardStartKey { get; set; } = Key.Enter;

    public Key KeyboardUpKey { get; set; } = Key.Up;

    public Key KeyboardDownKey { get; set; } = Key.Down;

    public Key KeyboardLeftKey { get; set; } = Key.Left;

    public Key KeyboardRightKey { get; set; } = Key.Right;

    public ControllerBinding ControllerA { get; set; } = ControllerBinding.GamepadButton(GamepadButtons.A);

    public ControllerBinding ControllerB { get; set; } = ControllerBinding.GamepadButton(GamepadButtons.X);

    public ControllerBinding ControllerSelect { get; set; } = ControllerBinding.GamepadButton(GamepadButtons.View);

    public ControllerBinding ControllerStart { get; set; } = ControllerBinding.GamepadButton(GamepadButtons.Menu);

    public ControllerBinding ControllerUp { get; set; } = ControllerBinding.GamepadButton(GamepadButtons.DPadUp);

    public ControllerBinding ControllerDown { get; set; } = ControllerBinding.GamepadButton(GamepadButtons.DPadDown);

    public ControllerBinding ControllerLeft { get; set; } = ControllerBinding.GamepadButton(GamepadButtons.DPadLeft);

    public ControllerBinding ControllerRight { get; set; } = ControllerBinding.GamepadButton(GamepadButtons.DPadRight);

    public static InputSettings CreateDefault() => new();

    public InputSettings Clone()
    {
        return new InputSettings
        {
            SelectedControllerId = SelectedControllerId,
            AnalogDPadMode = AnalogDPadMode,
            KeyboardAKey = KeyboardAKey,
            KeyboardBKey = KeyboardBKey,
            KeyboardSelectKey = KeyboardSelectKey,
            KeyboardStartKey = KeyboardStartKey,
            KeyboardUpKey = KeyboardUpKey,
            KeyboardDownKey = KeyboardDownKey,
            KeyboardLeftKey = KeyboardLeftKey,
            KeyboardRightKey = KeyboardRightKey,
            ControllerA = ControllerA with { },
            ControllerB = ControllerB with { },
            ControllerSelect = ControllerSelect with { },
            ControllerStart = ControllerStart with { },
            ControllerUp = ControllerUp with { },
            ControllerDown = ControllerDown with { },
            ControllerLeft = ControllerLeft with { },
            ControllerRight = ControllerRight with { }
        };
    }
}

public static class ControllerBindingCatalog
{
    public static IReadOnlyList<ControllerBindingOption> Options { get; } =
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
        new("lsx+", "Left Stick Right", ControllerBinding.GamepadAxis(0, positive: true, 0.35)),
        new("lsx-", "Left Stick Left", ControllerBinding.GamepadAxis(0, positive: false, 0.35)),
        new("lsy+", "Left Stick Down", ControllerBinding.GamepadAxis(1, positive: true, 0.35)),
        new("lsy-", "Left Stick Up", ControllerBinding.GamepadAxis(1, positive: false, 0.35)),
        new("rsx+", "Right Stick Right", ControllerBinding.GamepadAxis(2, positive: true, 0.35)),
        new("rsx-", "Right Stick Left", ControllerBinding.GamepadAxis(2, positive: false, 0.35)),
        new("rsy+", "Right Stick Down", ControllerBinding.GamepadAxis(3, positive: true, 0.35)),
        new("rsy-", "Right Stick Up", ControllerBinding.GamepadAxis(3, positive: false, 0.35)),
        new("lt", "Left Trigger", ControllerBinding.GamepadAxis(4, positive: true, 0.35)),
        new("rt", "Right Trigger", ControllerBinding.GamepadAxis(5, positive: true, 0.35))
    ];

    public static string GetDisplayName(ControllerBinding binding)
    {
        return Options.FirstOrDefault(option => option.Binding == binding)?.DisplayName
            ?? binding.Kind switch
            {
                ControllerBindingKind.None => "None",
                ControllerBindingKind.GamepadButton => $"Button {(GamepadButtons)binding.Index}",
                ControllerBindingKind.GamepadAxis => $"Axis {binding.Index} {(binding.Value >= 0 ? "+" : "-")}",
                ControllerBindingKind.RawButton => $"Raw Button {binding.Index}",
                ControllerBindingKind.RawSwitch => $"Raw Switch {binding.Index}",
                ControllerBindingKind.RawAxis => $"Raw Axis {binding.Index} {(binding.Value >= 0 ? "+" : "-")}",
                _ => "Unknown"
            };
    }
}

public static class ControllerAnalogDPadCatalog
{
    public static IReadOnlyList<ControllerAnalogDPadOption> Options { get; } =
    [
        new(ControllerAnalogDPadMode.None, "Disabled"),
        new(ControllerAnalogDPadMode.LeftStick, "Left stick"),
        new(ControllerAnalogDPadMode.RightStick, "Right stick")
    ];
}

public static class InputCatalog
{
    public static IReadOnlyList<KeyBindingOption> KeyOptions { get; } = BuildKeyOptions();

    private static IReadOnlyList<KeyBindingOption> BuildKeyOptions()
    {
        return
        [
            new KeyBindingOption(Key.None, "None"),
            new KeyBindingOption(Key.Up, "Up Arrow"),
            new KeyBindingOption(Key.Down, "Down Arrow"),
            new KeyBindingOption(Key.Left, "Left Arrow"),
            new KeyBindingOption(Key.Right, "Right Arrow"),
            new KeyBindingOption(Key.W, "W"),
            new KeyBindingOption(Key.A, "A"),
            new KeyBindingOption(Key.S, "S"),
            new KeyBindingOption(Key.D, "D"),
            new KeyBindingOption(Key.I, "I"),
            new KeyBindingOption(Key.J, "J"),
            new KeyBindingOption(Key.K, "K"),
            new KeyBindingOption(Key.L, "L"),
            new KeyBindingOption(Key.Z, "Z"),
            new KeyBindingOption(Key.X, "X"),
            new KeyBindingOption(Key.C, "C"),
            new KeyBindingOption(Key.V, "V"),
            new KeyBindingOption(Key.Space, "Space"),
            new KeyBindingOption(Key.Enter, "Enter"),
            new KeyBindingOption(Key.LeftShift, "Left Shift"),
            new KeyBindingOption(Key.RightShift, "Right Shift"),
            new KeyBindingOption(Key.LeftCtrl, "Left Ctrl"),
            new KeyBindingOption(Key.RightCtrl, "Right Ctrl"),
            new KeyBindingOption(Key.LeftAlt, "Left Alt"),
            new KeyBindingOption(Key.RightAlt, "Right Alt"),
            new KeyBindingOption(Key.Q, "Q"),
            new KeyBindingOption(Key.E, "E"),
            new KeyBindingOption(Key.R, "R"),
            new KeyBindingOption(Key.T, "T"),
            new KeyBindingOption(Key.Y, "Y"),
            new KeyBindingOption(Key.U, "U"),
            new KeyBindingOption(Key.O, "O"),
            new KeyBindingOption(Key.P, "P"),
            new KeyBindingOption(Key.F, "F"),
            new KeyBindingOption(Key.G, "G"),
            new KeyBindingOption(Key.H, "H"),
            new KeyBindingOption(Key.B, "B"),
            new KeyBindingOption(Key.N, "N"),
            new KeyBindingOption(Key.M, "M")
        ];
    }
}
