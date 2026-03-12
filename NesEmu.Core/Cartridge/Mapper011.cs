namespace NesEmu.Core.Cartridge;

public sealed class Mapper011 : Mapper
{
    private readonly MirroringMode _mirroring;
    private byte _bankRegister;

    public Mapper011(byte[] prgRom, byte[] chrMemory, byte[] prgRam, MirroringMode mirroring, bool chrWritable)
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

        return address < 0x8000 ? (byte)0xFF : ReadPrg32(_bankRegister & 0x03, address - 0x8000);
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
            _bankRegister = ResolveBusConflict(address, value);
        }
    }

    public override byte PpuRead(ushort address)
    {
        return address < 0x2000 ? ReadChr8(_bankRegister >> 4, address) : (byte)0xFF;
    }

    public override void PpuWrite(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            WriteChr8(_bankRegister >> 4, address, value);
        }
    }
}
