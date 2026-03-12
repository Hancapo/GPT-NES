namespace NesEmu.Core;

public sealed class ControllerPort
{
    private bool _strobe;
    private byte _latchedState;

    public ControllerState State { get; set; }

    public void Write(byte value)
    {
        var strobe = (value & 0x01) != 0;
        _strobe = strobe;

        if (_strobe)
        {
            _latchedState = State.ToByte();
        }
    }

    public byte Read(byte openBus, bool consecutiveRead)
    {
        if (_strobe)
        {
            _latchedState = State.ToByte();
            return (byte)((_latchedState & 0x01) | (openBus & 0xE0));
        }

        var value = (byte)((_latchedState & 0x01) | (openBus & 0xE0));
        if (!consecutiveRead)
        {
            _latchedState = (byte)((_latchedState >> 1) | 0x80);
        }

        return value;
    }
}
