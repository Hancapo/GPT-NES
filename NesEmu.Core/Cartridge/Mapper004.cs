namespace NesEmu.Core.Cartridge;

public sealed class Mapper004 : Mapper
{
    private readonly MirroringMode _hardwiredMirroring;
    private readonly byte[] _registers = new byte[8];

    private MirroringMode _mirroring;
    private byte _bankSelect;
    private byte _irqLatch;
    private byte _irqCounter;
    private int _a12LowAccesses;
    private bool _prgRamEnabled = true;
    private bool _prgRamWriteEnabled = true;
    private bool _irqReloadRequested;
    private bool _irqEnabled;
    private bool _irqPending;
    private bool _lastA12High;

    public Mapper004(byte[] prgRom, byte[] chrMemory, byte[] prgRam, MirroringMode mirroring, bool chrWritable)
        : base(prgRom, chrMemory, prgRam, chrWritable)
    {
        _hardwiredMirroring = mirroring;
        _mirroring = mirroring;
    }

    public override MirroringMode Mirroring => _hardwiredMirroring == MirroringMode.FourScreen ? _hardwiredMirroring : _mirroring;

    public override bool IrqPending => _irqPending;

    public override void Reset()
    {
        Array.Clear(_registers);
        _mirroring = _hardwiredMirroring;
        _bankSelect = 0;
        _irqLatch = 0;
        _irqCounter = 0;
        _a12LowAccesses = 0;
        _prgRamEnabled = true;
        _prgRamWriteEnabled = true;
        _irqReloadRequested = false;
        _irqEnabled = false;
        _irqPending = false;
        _lastA12High = false;
    }

    public override byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            return _prgRamEnabled ? ReadPrgRam(address) : (byte)0xFF;
        }

        if (address < 0x8000)
        {
            return 0xFF;
        }

        var prgMode = (_bankSelect & 0x40) != 0;
        return address switch
        {
            <= 0x9FFF => ReadPrg8(prgMode ? Prg8BankCount - 2 : _registers[6] & 0x3F, address - 0x8000),
            <= 0xBFFF => ReadPrg8(_registers[7] & 0x3F, address - 0xA000),
            <= 0xDFFF => ReadPrg8(prgMode ? _registers[6] & 0x3F : Prg8BankCount - 2, address - 0xC000),
            _ => ReadPrg8(Prg8BankCount - 1, address - 0xE000)
        };
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            if (_prgRamEnabled && _prgRamWriteEnabled)
            {
                WritePrgRam(address, value);
            }

            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        switch (address)
        {
            case <= 0x9FFF:
                if ((address & 0x01) == 0)
                {
                    _bankSelect = value;
                }
                else
                {
                    _registers[_bankSelect & 0x07] = value;
                }

                break;

            case <= 0xBFFF:
                if ((address & 0x01) == 0)
                {
                    if (_hardwiredMirroring != MirroringMode.FourScreen)
                    {
                        _mirroring = (value & 0x01) != 0 ? MirroringMode.Vertical : MirroringMode.Horizontal;
                    }
                }
                else
                {
                    _prgRamEnabled = (value & 0x80) != 0;
                    _prgRamWriteEnabled = (value & 0x40) == 0;
                }

                break;

            case <= 0xDFFF:
                if ((address & 0x01) == 0)
                {
                    _irqLatch = value;
                }
                else
                {
                    _irqCounter = 0;
                    _irqReloadRequested = true;
                }

                break;

            default:
                if ((address & 0x01) == 0)
                {
                    _irqEnabled = false;
                    _irqPending = false;
                }
                else
                {
                    _irqEnabled = true;
                }

                break;
        }
    }

    public override byte PpuRead(ushort address)
    {
        if (address >= 0x2000)
        {
            return 0xFF;
        }

        var chrMode = (_bankSelect & 0x80) != 0;
        return chrMode ? ReadChrModeOne(address) : ReadChrModeZero(address);
    }

    public override void PpuWrite(ushort address, byte value)
    {
        if (address >= 0x2000 || !ChrWritable)
        {
            return;
        }

        var chrMode = (_bankSelect & 0x80) != 0;
        if (chrMode)
        {
            WriteChrModeOne(address, value);
        }
        else
        {
            WriteChrModeZero(address, value);
        }
    }

    public override void OnPpuAddressAccess(ushort address)
    {
        var a12High = (address & 0x1000) != 0;
        if (!a12High)
        {
            _a12LowAccesses++;
            _lastA12High = false;
            return;
        }

        if (_lastA12High || _a12LowAccesses < 3)
        {
            _lastA12High = true;
            return;
        }

        _lastA12High = true;
        _a12LowAccesses = 0;
        ClockIrqCounter();
    }

    private byte ReadChrModeZero(ushort address)
    {
        return address switch
        {
            <= 0x07FF => ReadChr2(_registers[0] >> 1, address),
            <= 0x0FFF => ReadChr2(_registers[1] >> 1, address - 0x0800),
            <= 0x13FF => ReadChr1(_registers[2], address - 0x1000),
            <= 0x17FF => ReadChr1(_registers[3], address - 0x1400),
            <= 0x1BFF => ReadChr1(_registers[4], address - 0x1800),
            _ => ReadChr1(_registers[5], address - 0x1C00)
        };
    }

    private byte ReadChrModeOne(ushort address)
    {
        return address switch
        {
            <= 0x03FF => ReadChr1(_registers[2], address),
            <= 0x07FF => ReadChr1(_registers[3], address - 0x0400),
            <= 0x0BFF => ReadChr1(_registers[4], address - 0x0800),
            <= 0x0FFF => ReadChr1(_registers[5], address - 0x0C00),
            <= 0x17FF => ReadChr2(_registers[0] >> 1, address - 0x1000),
            _ => ReadChr2(_registers[1] >> 1, address - 0x1800)
        };
    }

    private void WriteChrModeZero(ushort address, byte value)
    {
        switch (address)
        {
            case <= 0x07FF:
                WriteChr2(_registers[0] >> 1, address, value);
                break;
            case <= 0x0FFF:
                WriteChr2(_registers[1] >> 1, address - 0x0800, value);
                break;
            case <= 0x13FF:
                WriteChr1(_registers[2], address - 0x1000, value);
                break;
            case <= 0x17FF:
                WriteChr1(_registers[3], address - 0x1400, value);
                break;
            case <= 0x1BFF:
                WriteChr1(_registers[4], address - 0x1800, value);
                break;
            default:
                WriteChr1(_registers[5], address - 0x1C00, value);
                break;
        }
    }

    private void WriteChrModeOne(ushort address, byte value)
    {
        switch (address)
        {
            case <= 0x03FF:
                WriteChr1(_registers[2], address, value);
                break;
            case <= 0x07FF:
                WriteChr1(_registers[3], address - 0x0400, value);
                break;
            case <= 0x0BFF:
                WriteChr1(_registers[4], address - 0x0800, value);
                break;
            case <= 0x0FFF:
                WriteChr1(_registers[5], address - 0x0C00, value);
                break;
            case <= 0x17FF:
                WriteChr2(_registers[0] >> 1, address - 0x1000, value);
                break;
            default:
                WriteChr2(_registers[1] >> 1, address - 0x1800, value);
                break;
        }
    }

    private void ClockIrqCounter()
    {
        if (_irqCounter == 0 || _irqReloadRequested)
        {
            _irqCounter = _irqLatch;
            _irqReloadRequested = false;
        }
        else
        {
            _irqCounter--;
        }

        if (_irqCounter == 0 && _irqEnabled)
        {
            _irqPending = true;
        }
    }
}
