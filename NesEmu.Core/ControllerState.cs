namespace NesEmu.Core;

public readonly record struct ControllerState(
    bool A,
    bool B,
    bool Select,
    bool Start,
    bool Up,
    bool Down,
    bool Left,
    bool Right)
{
    public byte ToByte()
    {
        var value = 0;
        value |= A ? 1 << 0 : 0;
        value |= B ? 1 << 1 : 0;
        value |= Select ? 1 << 2 : 0;
        value |= Start ? 1 << 3 : 0;
        value |= Up ? 1 << 4 : 0;
        value |= Down ? 1 << 5 : 0;
        value |= Left ? 1 << 6 : 0;
        value |= Right ? 1 << 7 : 0;
        return (byte)value;
    }
}
