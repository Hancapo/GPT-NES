namespace NesEmu.Core.Cartridge;

public sealed class Mapper013 : Mapper
{
    private readonly MirroringMode _mirroring;
    private byte _upperChrBank;

    public Mapper013(byte[] prgRom, byte[] chrMemory, byte[] prgRam, MirroringMode mirroring, bool chrWritable)
        : base(prgRom, chrMemory, prgRam, chrWritable)
    {
        _mirroring = mirroring;
    }

    public override MirroringMode Mirroring => _mirroring;

    public override byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            return ReadPrgRam(address);
        }

        return address < 0x8000 ? (byte)0xFF : ReadPrg32(0, address - 0x8000);
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            WritePrgRam(address, value);
            return;
        }

        if (address >= 0x8000)
        {
            _upperChrBank = (byte)(ResolveBusConflict(address, value) & 0x03);
        }
    }

    public override byte PpuRead(ushort address)
    {
        if (address >= 0x2000)
        {
            return 0xFF;
        }

        return address < 0x1000
            ? ReadChr4(0, address)
            : ReadChr4(_upperChrBank, address - 0x1000);
    }

    public override void PpuWrite(ushort address, byte value)
    {
        if (address >= 0x2000)
        {
            return;
        }

        if (address < 0x1000)
        {
            WriteChr4(0, address, value);
            return;
        }

        WriteChr4(_upperChrBank, address - 0x1000, value);
    }
}
