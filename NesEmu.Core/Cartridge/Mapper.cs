namespace NesEmu.Core.Cartridge;

public abstract class Mapper
{
    protected Mapper(byte[] prgRom, byte[] chrMemory, byte[] prgRam, bool chrWritable)
    {
        PrgRom = prgRom;
        ChrMemory = chrMemory;
        PrgRam = prgRam;
        ChrWritable = chrWritable;
    }

    protected byte[] PrgRom { get; }
    protected byte[] ChrMemory { get; }
    protected byte[] PrgRam { get; }
    protected bool ChrWritable { get; }

    public abstract MirroringMode Mirroring { get; }

    public abstract byte CpuRead(ushort address);

    public abstract void CpuWrite(ushort address, byte value);

    public abstract byte PpuRead(ushort address);

    public abstract void PpuWrite(ushort address, byte value);
}
