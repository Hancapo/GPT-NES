namespace NesEmu.Core.Cartridge;

public sealed class Mapper009 : Mapper
{
    private byte _prgBank;
    private byte _chrFd0;
    private byte _chrFe0;
    private byte _chrFd1;
    private byte _chrFe1;
    private bool _latch0Fe;
    private bool _latch1Fe;
    private MirroringMode _mirroring;

    public Mapper009(byte[] prgRom, byte[] chrMemory, byte[] prgRam, MirroringMode mirroring, bool chrWritable)
        : base(prgRom, chrMemory, prgRam, chrWritable)
    {
        _mirroring = mirroring;
    }

    public override MirroringMode Mirroring => _mirroring;

    public override void Reset()
    {
        _prgBank = 0;
        _chrFd0 = 0;
        _chrFe0 = 0;
        _chrFd1 = 0;
        _chrFe1 = 0;
        _latch0Fe = false;
        _latch1Fe = false;
    }

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

        return address switch
        {
            <= 0x9FFF => ReadPrg8(_prgBank, address - 0x8000),
            <= 0xBFFF => ReadPrg8(Prg8BankCount - 3, address - 0xA000),
            <= 0xDFFF => ReadPrg8(Prg8BankCount - 2, address - 0xC000),
            _ => ReadPrg8(Prg8BankCount - 1, address - 0xE000)
        };
    }

    public override void CpuWrite(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x6000 and <= 0x7FFF:
                WritePrgRam(address, value);
                break;
            case >= 0xA000 and <= 0xAFFF:
                _prgBank = (byte)(value & 0x0F);
                break;
            case >= 0xB000 and <= 0xBFFF:
                _chrFd0 = (byte)(value & 0x1F);
                break;
            case >= 0xC000 and <= 0xCFFF:
                _chrFe0 = (byte)(value & 0x1F);
                break;
            case >= 0xD000 and <= 0xDFFF:
                _chrFd1 = (byte)(value & 0x1F);
                break;
            case >= 0xE000 and <= 0xEFFF:
                _chrFe1 = (byte)(value & 0x1F);
                break;
            case >= 0xF000:
                _mirroring = (value & 0x01) != 0 ? MirroringMode.Horizontal : MirroringMode.Vertical;
                break;
        }
    }

    public override byte PpuRead(ushort address)
    {
        if (address >= 0x2000)
        {
            return 0xFF;
        }

        return address < 0x1000
            ? ReadChr4(_latch0Fe ? _chrFe0 : _chrFd0, address)
            : ReadChr4(_latch1Fe ? _chrFe1 : _chrFd1, address - 0x1000);
    }

    public override void PpuWrite(ushort address, byte value)
    {
        if (address >= 0x2000 || !ChrWritable)
        {
            return;
        }

        if (address < 0x1000)
        {
            WriteChr4(_latch0Fe ? _chrFe0 : _chrFd0, address, value);
            return;
        }

        WriteChr4(_latch1Fe ? _chrFe1 : _chrFd1, address - 0x1000, value);
    }

    public override void OnPpuAddressAccess(ushort address, long ppuCycle = 0)
    {
        switch (address)
        {
            case 0x0FD8:
                _latch0Fe = false;
                break;
            case 0x0FE8:
                _latch0Fe = true;
                break;
            case >= 0x1FD8 and <= 0x1FDF:
                _latch1Fe = false;
                break;
            case >= 0x1FE8 and <= 0x1FEF:
                _latch1Fe = true;
                break;
        }
    }
}
