namespace NesEmu.Core.Cartridge;

public sealed class Mapper001 : Mapper
{
    private byte _shiftRegister = 0x10;
    private byte _control = 0x0C;
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;
    private bool _prgRamEnabled = true;

    public Mapper001(byte[] prgRom, byte[] chrMemory, byte[] prgRam, bool chrWritable)
        : base(prgRom, chrMemory, prgRam, chrWritable)
    {
    }

    public override MirroringMode Mirroring =>
        (_control & 0x03) switch
        {
            0 => MirroringMode.SingleScreenLower,
            1 => MirroringMode.SingleScreenUpper,
            2 => MirroringMode.Vertical,
            _ => MirroringMode.Horizontal
        };

    public override byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            return _prgRamEnabled && PrgRam.Length != 0
                ? PrgRam[address - 0x6000]
                : (byte)0xFF;
        }

        if (address < 0x8000)
        {
            return 0xFF;
        }

        var prgMode = (_control >> 2) & 0x03;
        var offset = address - 0x8000;
        var bankCount = PrgRom.Length / 0x4000;

        return prgMode switch
        {
            0 or 1 => ReadPrg32(((_prgBank & 0x0E) >> 1), offset),
            2 => offset < 0x4000
                ? ReadPrg16(0, offset)
                : ReadPrg16(_prgBank & 0x0F, offset - 0x4000),
            _ => offset < 0x4000
                ? ReadPrg16(_prgBank & 0x0F, offset)
                : ReadPrg16(bankCount - 1, offset - 0x4000)
        };
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            if (_prgRamEnabled && PrgRam.Length != 0)
            {
                PrgRam[address - 0x6000] = value;
            }

            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        if ((value & 0x80) != 0)
        {
            _shiftRegister = 0x10;
            _control |= 0x0C;
            return;
        }

        var isComplete = (_shiftRegister & 0x01) != 0;
        _shiftRegister >>= 1;
        _shiftRegister |= (byte)((value & 0x01) << 4);

        if (!isComplete)
        {
            return;
        }

        switch (address)
        {
            case <= 0x9FFF:
                _control = _shiftRegister;
                break;
            case <= 0xBFFF:
                _chrBank0 = _shiftRegister;
                break;
            case <= 0xDFFF:
                _chrBank1 = _shiftRegister;
                break;
            default:
                _prgBank = (byte)(_shiftRegister & 0x0F);
                _prgRamEnabled = (_shiftRegister & 0x10) == 0;
                break;
        }

        _shiftRegister = 0x10;
    }

    public override byte PpuRead(ushort address)
    {
        if (address >= 0x2000)
        {
            return 0xFF;
        }

        if (((_control >> 4) & 0x01) == 0)
        {
            var bank = (_chrBank0 & 0x1E) >> 1;
            var index = (bank * 0x2000 + address) % ChrMemory.Length;
            return ChrMemory[index];
        }

        if (address < 0x1000)
        {
            var index = ((_chrBank0 & 0x1F) * 0x1000 + address) % ChrMemory.Length;
            return ChrMemory[index];
        }

        {
            var index = ((_chrBank1 & 0x1F) * 0x1000 + (address - 0x1000)) % ChrMemory.Length;
            return ChrMemory[index];
        }
    }

    public override void PpuWrite(ushort address, byte value)
    {
        if (!ChrWritable || address >= 0x2000 || ChrMemory.Length == 0)
        {
            return;
        }

        if (((_control >> 4) & 0x01) == 0)
        {
            var bank = (_chrBank0 & 0x1E) >> 1;
            var index = (bank * 0x2000 + address) % ChrMemory.Length;
            ChrMemory[index] = value;
            return;
        }

        if (address < 0x1000)
        {
            var index = ((_chrBank0 & 0x1F) * 0x1000 + address) % ChrMemory.Length;
            ChrMemory[index] = value;
            return;
        }

        {
            var index = ((_chrBank1 & 0x1F) * 0x1000 + (address - 0x1000)) % ChrMemory.Length;
            ChrMemory[index] = value;
            return;
        }
    }

    private new byte ReadPrg16(int bank, int offset)
    {
        bank %= Math.Max(1, PrgRom.Length / 0x4000);
        return PrgRom[bank * 0x4000 + (offset & 0x3FFF)];
    }

    private new byte ReadPrg32(int bank, int offset)
    {
        bank %= Math.Max(1, PrgRom.Length / 0x8000);
        return PrgRom[bank * 0x8000 + (offset & 0x7FFF)];
    }
}
