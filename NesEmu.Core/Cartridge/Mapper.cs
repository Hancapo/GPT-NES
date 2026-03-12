namespace NesEmu.Core.Cartridge;

public abstract class Mapper
{
    protected Mapper(byte[] prgRom, byte[] chrMemory, byte[] prgRam, bool chrWritable)
    {
        PrgRom = prgRom;
        ChrMemory = chrMemory;
        PrgRam = prgRam;
        ChrWritable = chrWritable;
    }

    protected byte[] PrgRom { get; }
    protected byte[] ChrMemory { get; }
    protected byte[] PrgRam { get; }
    protected bool ChrWritable { get; }

    public abstract MirroringMode Mirroring { get; }

    public virtual bool IrqPending => false;

    public abstract byte CpuRead(ushort address);

    public abstract void CpuWrite(ushort address, byte value);

    public abstract byte PpuRead(ushort address);

    public abstract void PpuWrite(ushort address, byte value);

    public virtual void Reset()
    {
    }

    public virtual void OnPpuAddressAccess(ushort address)
    {
    }

    protected int Prg8BankCount => Math.Max(1, PrgRom.Length / 0x2000);

    protected int Prg16BankCount => Math.Max(1, PrgRom.Length / 0x4000);

    protected int Prg32BankCount => Math.Max(1, PrgRom.Length / 0x8000);

    protected int Chr1BankCount => Math.Max(1, ChrMemory.Length / 0x0400);

    protected int Chr2BankCount => Math.Max(1, ChrMemory.Length / 0x0800);

    protected int Chr4BankCount => Math.Max(1, ChrMemory.Length / 0x1000);

    protected int Chr8BankCount => Math.Max(1, ChrMemory.Length / 0x2000);

    protected byte ReadPrgRam(ushort address)
    {
        return PrgRam.Length == 0 ? (byte)0xFF : PrgRam[(address - 0x6000) % PrgRam.Length];
    }

    protected void WritePrgRam(ushort address, byte value)
    {
        if (PrgRam.Length == 0)
        {
            return;
        }

        PrgRam[(address - 0x6000) % PrgRam.Length] = value;
    }

    protected byte ReadPrg8(int bank, int offset)
    {
        if (PrgRom.Length == 0)
        {
            return 0xFF;
        }

        bank = NormalizeBank(bank, Prg8BankCount);
        var index = bank * 0x2000 + (offset & 0x1FFF);
        return PrgRom[index % PrgRom.Length];
    }

    protected byte ReadPrg16(int bank, int offset)
    {
        if (PrgRom.Length == 0)
        {
            return 0xFF;
        }

        bank = NormalizeBank(bank, Prg16BankCount);
        var index = bank * 0x4000 + (offset & 0x3FFF);
        return PrgRom[index % PrgRom.Length];
    }

    protected byte ReadPrg32(int bank, int offset)
    {
        if (PrgRom.Length == 0)
        {
            return 0xFF;
        }

        bank = NormalizeBank(bank, Prg32BankCount);
        var index = bank * 0x8000 + (offset & 0x7FFF);
        return PrgRom[index % PrgRom.Length];
    }

    protected byte ReadChr1(int bank, int offset)
    {
        if (ChrMemory.Length == 0)
        {
            return 0xFF;
        }

        bank = NormalizeBank(bank, Chr1BankCount);
        var index = bank * 0x0400 + (offset & 0x03FF);
        return ChrMemory[index % ChrMemory.Length];
    }

    protected byte ReadChr2(int bank, int offset)
    {
        if (ChrMemory.Length == 0)
        {
            return 0xFF;
        }

        bank = NormalizeBank(bank, Chr2BankCount);
        var index = bank * 0x0800 + (offset & 0x07FF);
        return ChrMemory[index % ChrMemory.Length];
    }

    protected byte ReadChr4(int bank, int offset)
    {
        if (ChrMemory.Length == 0)
        {
            return 0xFF;
        }

        bank = NormalizeBank(bank, Chr4BankCount);
        var index = bank * 0x1000 + (offset & 0x0FFF);
        return ChrMemory[index % ChrMemory.Length];
    }

    protected byte ReadChr8(int bank, int offset)
    {
        if (ChrMemory.Length == 0)
        {
            return 0xFF;
        }

        bank = NormalizeBank(bank, Chr8BankCount);
        var index = bank * 0x2000 + (offset & 0x1FFF);
        return ChrMemory[index % ChrMemory.Length];
    }

    protected void WriteChr1(int bank, int offset, byte value)
    {
        if (!ChrWritable || ChrMemory.Length == 0)
        {
            return;
        }

        bank = NormalizeBank(bank, Chr1BankCount);
        var index = bank * 0x0400 + (offset & 0x03FF);
        ChrMemory[index % ChrMemory.Length] = value;
    }

    protected void WriteChr2(int bank, int offset, byte value)
    {
        if (!ChrWritable || ChrMemory.Length == 0)
        {
            return;
        }

        bank = NormalizeBank(bank, Chr2BankCount);
        var index = bank * 0x0800 + (offset & 0x07FF);
        ChrMemory[index % ChrMemory.Length] = value;
    }

    protected void WriteChr4(int bank, int offset, byte value)
    {
        if (!ChrWritable || ChrMemory.Length == 0)
        {
            return;
        }

        bank = NormalizeBank(bank, Chr4BankCount);
        var index = bank * 0x1000 + (offset & 0x0FFF);
        ChrMemory[index % ChrMemory.Length] = value;
    }

    protected void WriteChr8(int bank, int offset, byte value)
    {
        if (!ChrWritable || ChrMemory.Length == 0)
        {
            return;
        }

        bank = NormalizeBank(bank, Chr8BankCount);
        var index = bank * 0x2000 + (offset & 0x1FFF);
        ChrMemory[index % ChrMemory.Length] = value;
    }

    protected byte ResolveBusConflict(ushort address, byte value)
    {
        return (byte)(value & CpuRead(address));
    }

    private static int NormalizeBank(int bank, int bankCount)
    {
        bankCount = Math.Max(1, bankCount);
        bank %= bankCount;
        if (bank < 0)
        {
            bank += bankCount;
        }

        return bank;
    }
}
