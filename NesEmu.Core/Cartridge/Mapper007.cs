namespace NesEmu.Core.Cartridge;

public sealed class Mapper007 : Mapper
{
    private byte _bankRegister;

    public Mapper007(byte[] prgRom, byte[] chrMemory, byte[] prgRam, bool chrWritable)
        : base(prgRom, chrMemory, prgRam, chrWritable)
    {
    }

    public override MirroringMode Mirroring => (_bankRegister & 0x10) != 0
        ? MirroringMode.SingleScreenUpper
        : MirroringMode.SingleScreenLower;

    public override byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            return ReadPrgRam(address);
        }

        return address < 0x8000 ? (byte)0xFF : ReadPrg32(_bankRegister & 0x07, address - 0x8000);
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
            _bankRegister = value;
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
