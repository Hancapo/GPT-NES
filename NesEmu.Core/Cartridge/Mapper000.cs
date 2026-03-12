namespace NesEmu.Core.Cartridge;

public sealed class Mapper000 : Mapper
{
    private readonly MirroringMode _mirroring;

    public Mapper000(byte[] prgRom, byte[] chrMemory, byte[] prgRam, MirroringMode mirroring, bool chrWritable)
        : base(prgRom, chrMemory, prgRam, chrWritable)
    {
        _mirroring = mirroring;
    }

    public override MirroringMode Mirroring => _mirroring;

    public override byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            return PrgRam.Length == 0 ? (byte)0xFF : PrgRam[address - 0x6000];
        }

        if (address < 0x8000)
        {
            return 0xFF;
        }

        var offset = address - 0x8000;
        if (PrgRom.Length == 0x4000)
        {
            offset &= 0x3FFF;
        }

        return PrgRom[offset];
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and <= 0x7FFF && PrgRam.Length != 0)
        {
            PrgRam[address - 0x6000] = value;
        }
    }

    public override byte PpuRead(ushort address)
    {
        return address < 0x2000 ? ChrMemory[address % ChrMemory.Length] : (byte)0xFF;
    }

    public override void PpuWrite(ushort address, byte value)
    {
        if (!ChrWritable || address >= 0x2000 || ChrMemory.Length == 0)
        {
            return;
        }

        ChrMemory[address % ChrMemory.Length] = value;
    }
}
