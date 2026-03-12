namespace NesEmu.Core;

public interface ICpuBus
{
    byte CpuRead(ushort address);

    void CpuWrite(ushort address, byte value);
}
