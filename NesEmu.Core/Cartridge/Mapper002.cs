namespace NesEmu.Core.Cartridge;

public sealed class Mapper002 : Mapper
{
    private readonly MirroringMode _mirroring;
    private byte _selectedBank;

    public Mapper002(byte[] prgRom, byte[] chrMemory, byte[] prgRam, MirroringMode mirroring, bool chrWritable)
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

        var bankCount = Math.Max(1, PrgRom.Length / 0x4000);
        var bank = address < 0xC000 ? _selectedBank % bankCount : bankCount - 1;
        var offset = (bank * 0x4000) + (address & 0x3FFF);
        return PrgRom[offset];
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            if (PrgRam.Length != 0)
            {
                PrgRam[address - 0x6000] = value;
            }

            return;
        }

        if (address >= 0x8000)
        {
            _selectedBank = (byte)(value & 0x0F);
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
