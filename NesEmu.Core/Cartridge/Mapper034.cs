namespace NesEmu.Core.Cartridge;

public sealed class Mapper034 : Mapper
{
    private readonly MirroringMode _mirroring;
    private readonly bool _ninaMode;

    private byte _prgBank;
    private byte _chrBank0;
    private byte _chrBank1;

    public Mapper034(byte[] prgRom, byte[] chrMemory, byte[] prgRam, MirroringMode mirroring, bool chrWritable)
        : base(prgRom, chrMemory, prgRam, chrWritable)
    {
        _mirroring = mirroring;
        _ninaMode = chrMemory.Length > 0x2000;
    }

    public override MirroringMode Mirroring => _mirroring;

    public override byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            return _ninaMode ? ReadPrgRam(address) : (byte)0xFF;
        }

        return address < 0x8000 ? (byte)0xFF : ReadPrg32(_prgBank, address - 0x8000);
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (_ninaMode && address is >= 0x6000 and <= 0x7FFF)
        {
            WritePrgRam(address, value);
        }

        if (_ninaMode)
        {
            switch (address)
            {
                case 0x7FFD:
                    _prgBank = (byte)(value & 0x03);
                    break;
                case 0x7FFE:
                    _chrBank0 = (byte)(value & 0x0F);
                    break;
                case 0x7FFF:
                    _chrBank1 = (byte)(value & 0x0F);
                    break;
            }

            return;
        }

        if (address >= 0x8000)
        {
            _prgBank = (byte)(ResolveBusConflict(address, value) & 0x03);
        }
    }

    public override byte PpuRead(ushort address)
    {
        if (address >= 0x2000)
        {
            return 0xFF;
        }

        if (!_ninaMode)
        {
            return ReadChr8(0, address);
        }

        return address < 0x1000
            ? ReadChr4(_chrBank0, address)
            : ReadChr4(_chrBank1, address - 0x1000);
    }

    public override void PpuWrite(ushort address, byte value)
    {
        if (address >= 0x2000)
        {
            return;
        }

        if (!_ninaMode)
        {
            WriteChr8(0, address, value);
            return;
        }

        if (address < 0x1000)
        {
            WriteChr4(_chrBank0, address, value);
            return;
        }

        WriteChr4(_chrBank1, address - 0x1000, value);
    }
}
