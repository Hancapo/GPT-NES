namespace NesEmu.Core.Cartridge;

public sealed class Mapper094 : Mapper
{
    private readonly MirroringMode _mirroring;
    private byte _prgBank;

    public Mapper094(byte[] prgRom, byte[] chrMemory, byte[] prgRam, MirroringMode mirroring, bool chrWritable)
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

        if (address < 0x8000)
        {
            return 0xFF;
        }

        return address < 0xC000
            ? ReadPrg16(_prgBank, address - 0x8000)
            : ReadPrg16(Prg16BankCount - 1, address - 0xC000);
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
            _prgBank = (byte)((ResolveBusConflict(address, value) >> 2) & 0x07);
        }
    }

    public override byte PpuRead(ushort address)
    {
        return address < 0x2000 ? ReadChr8(0, address) : (byte)0xFF;
    }

    public override void PpuWrite(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            WriteChr8(0, address, value);
        }
    }
}
